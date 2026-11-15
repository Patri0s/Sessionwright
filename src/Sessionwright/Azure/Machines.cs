using System.Windows;
using System.Windows.Input;

namespace Sessionwright;

/// <summary>
/// The Azure virtual machines of the signed-in accounts, with or without a connection, and what can be done to them.
/// Each machine is a row (a <see cref="Connection"/>, which already carries the power state and the next auto-shutdown):
/// the connection that leads to it, else a row of its own kept for this run only, never part of the tree.
/// </summary>
static class Machines
{
    static Store Store => App.Store;
    static readonly Dictionary<string, Connection> OwnRows = [];
    static Task? _loading;

    /// <summary>The last full list (null until loaded once in this run), with what kept some accounts or directories out.</summary>
    public static List<VmInfo>? Last { get; private set; }
    public static List<string> Problems { get; private set; } = [];
    public static List<AzureTenantSignIn> SignIns { get; private set; } = [];

    /// <summary>A fresh list is in (raised on the UI thread).</summary>
    public static event Action? Loaded;

    public static string Key(AzureVmLink vm) => $"{vm.SubscriptionId}/{vm.ResourceGroup}/{vm.Name}".ToLowerInvariant();

    /// <summary>The connections that lead to this VM, in the order of the data file.</summary>
    public static IEnumerable<Connection> LinkedTo(AzureVmLink vm) =>
        Store.Data.Connections.Where(c => c.AzureVm != null && Key(c.AzureVm) == Key(vm));

    /// <summary>Every row carrying this VM's state: its connections and its own row.</summary>
    public static IEnumerable<Connection> SameVm(AzureVmLink vm) =>
        LinkedTo(vm).Concat(OwnRows.TryGetValue(Key(vm), out var own) ? [own] : []);

    /// <summary>The row of a listed VM: its first connection, else its own (whose link keeps a postponed shutdown).</summary>
    public static Connection Row(VmInfo vm)
    {
        if (LinkedTo(vm.Link).FirstOrDefault() is { } linked) return linked;
        if (!OwnRows.TryGetValue(Key(vm.Link), out var row))
        {
            var link = Store.Data.Settings.PendingShutdowns.FirstOrDefault(l => Key(l) == Key(vm.Link)) ?? vm.Link;
            OwnRows[Key(vm.Link)] = row = new Connection { Name = vm.Name, Host = "", AzureVm = link };
        }
        return row;
    }

    /// <summary>The rows of VMs no connection leads to (known once the list was loaded).</summary>
    public static IEnumerable<Connection> Unlinked => OwnRows.Values.Where(r => !LinkedTo(r.AzureVm!).Any());

    public static bool IsHidden(AzureVmLink vm) => Store.Data.Settings.HiddenVms.Contains(Key(vm), StringComparer.OrdinalIgnoreCase);

    public static void SetHidden(AzureVmLink vm, bool hidden)
    {
        Store.Data.Settings.HiddenVms.RemoveAll(k => string.Equals(k, Key(vm), StringComparison.OrdinalIgnoreCase));
        if (hidden) Store.Data.Settings.HiddenVms.Add(Key(vm));
        Store.Save();
    }

    /// <summary>Lists every VM again (a few seconds: every directory and subscription); one load at a time.</summary>
    public static Task LoadAsync() => _loading ??= LoadOnceAsync();

    static async Task LoadOnceAsync()
    {
        await Task.Yield(); // _loading is set before an immediate end clears it
        try
        {
            var problems = new List<string>();
            var signIns = new List<AzureTenantSignIn>();
            var vms = (await AzureVms.ListAllAsync(problems, signIns))
                .OrderBy(v => v.Name, Store.NaturalOrder).ThenBy(v => v.SubscriptionName, Store.NaturalOrder).ToList();
            foreach (var v in vms) Row(v).VmState = v.State;
            (Last, Problems, SignIns) = (vms, problems, signIns);
            Loaded?.Invoke();
        }
        finally { _loading = null; }
    }

    // ---------- Actions (the main window's menus, the machines window and the search use the same) ----------

    /// <summary>Starts the VMs without connecting (e.g. a few minutes ahead).</summary>
    public static Task StartAsync(Window owner, IReadOnlyList<Connection> targets) =>
        ChangeAsync(owner, targets, AzureVms.StartAsync, VmPowerState.Starting, "AzureVmStartFailed");

    /// <summary>Shuts the VMs down (deallocated: no compute charges), asking first and telling who else uses them.</summary>
    public static async Task StopAsync(Window owner, IReadOnlyList<Connection> targets)
    {
        if (targets.Count == 0) return;
        var names = string.Join(", ", targets.Select(c => c.AzureVm!.Name));
        var open = targets.Count(c => App.Sessions.Open.Any(s => s.Connection.AzureVm is { } vm && Key(vm) == Key(c.AzureVm!) && s.IsOpen));
        var message = Loc.Format(targets.Count > 1 ? "AzureVmStopConfirmMany" : "AzureVmStopConfirm", names)
                      + (open > 0 ? "\n\n" + Loc.Get("AzureVmStopOpenSessions") : "");
        // Someone else on a shared machine (a few seconds to find out): the question opens at once and says so meanwhile.
        if (!Dialog.Confirm(owner, message, Loc.Get("AzureVmStop"), DialogKind.Warning, danger: true,
                            more: OthersAsync(targets), waiting: Loc.Get(targets.Count > 1 ? "SharedCheckingMany" : "SharedChecking"))) return;
        await ChangeAsync(owner, targets, AzureVms.DeallocateAsync, VmPowerState.Deallocating, "AzureVmStopFailed");
    }

    static async Task<string?> OthersAsync(IReadOnlyList<Connection> targets)
    {
        var since = DateTime.Now.AddHours(-12);
        var others = (await Task.WhenAll(targets.Select(async c => (await SharedUse.OthersAsync(c, since)).Select(n => $"{c.Title}: {n}"))))
            .SelectMany(l => l).ToList();
        return others.Count > 0 ? Loc.Get("SharedWarning") + "\n" + string.Join("\n", others) : null;
    }

    /// <summary>Restart or shut down with a message and a delay (the power dialog).</summary>
    public static async Task PowerAsync(Window owner, IReadOnlyList<Connection> targets, bool restart = true)
    {
        if (targets.Count == 0) return;
        var dialog = new PowerWindow(targets, restart) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Request is not { } request) return;
        var failures = (await Task.WhenAll(targets.Select(c => RemotePower.RunAsync(c, request)))).OfType<string>().ToList();
        if (failures.Count > 0) Notification.Problem(owner, Loc.Get("ProblemPowerTitle"), string.Join("\n\n", failures));
    }

    static async Task ChangeAsync(Window owner, IReadOnlyList<Connection> targets, Func<AzureVmLink, CancellationToken, Task> change, VmPowerState next, string failedKey)
    {
        var failures = new List<string>();
        await Task.WhenAll(targets.Select(async c =>
        {
            var vm = c.AzureVm!;
            try
            {
                await change(vm, CancellationToken.None);
                foreach (var x in SameVm(vm).Append(c).Distinct()) x.VmState = next;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or AzureSignInRequiredException
                                           or TaskCanceledException)
            {
                Log.Error(ex, $"Azure: {next} of {vm.Name}");
                failures.Add(Loc.Format(failedKey, vm.Name, ex.Message));
            }
        }));
        if (failures.Count > 0)
            Notification.Problem(owner, Loc.Get(next == VmPowerState.Starting ? "ProblemStartTitle" : "ProblemStopTitle"), string.Join("\n\n", failures));
    }
}
