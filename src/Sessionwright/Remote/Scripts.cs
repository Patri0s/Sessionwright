using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Sessionwright;

/// <summary>A PowerShell script from the library, run on a machine and its output shown: "restart IIS", "free disk space".</summary>
public sealed partial class RemoteScript : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _name = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _body = "";
    /// <summary>It changes something on the machine (restarts a service): asked about before it runs.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _confirm;

    public RemoteScript Clone() => new() { Id = Id, Name = Name, Body = Body, Confirm = Confirm };
}

/// <summary>
/// The script library and running a script on the machine behind a connection: through Azure Run Command (no port has
/// to be open, runs as SYSTEM, takes up to a minute, one at a time per machine; needs
/// Microsoft.Compute/virtualMachines/runCommand/action), so on connections linked to an Azure VM.
/// </summary>
static class Scripts
{
    /// <summary>The user's library; the built-in scripts until it was first edited.</summary>
    public static List<RemoteScript> All => App.Store.Data.Settings.Scripts ?? Defaults();

    // Ids are fixed: a later version can tell its own scripts from the user's.
    // Service names of a Dynamics 365 Finance & Operations development VM; a service the machine does not have is skipped.
    internal static List<RemoteScript> Defaults() =>
    [
        new()
        {
            Id = new("5c1d0a51-0000-4000-8000-000000000001"), Name = Loc.Get("ScriptDisk"),
            Body = """
                Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" |
                    Select-Object DeviceID, VolumeName,
                        @{ n = 'Size GB'; e = { [math]::Round($_.Size / 1GB, 1) } },
                        @{ n = 'Free GB'; e = { [math]::Round($_.FreeSpace / 1GB, 1) } },
                        @{ n = 'Free %'; e = { [math]::Round(100 * $_.FreeSpace / $_.Size) } } |
                    Format-Table -AutoSize | Out-String -Width 120
                """,
        },
        new()
        {
            Id = new("5c1d0a51-0000-4000-8000-000000000002"), Name = Loc.Get("ScriptServices"),
            Body = """
                $names = 'W3SVC', 'DynamicsAxBatch', 'Microsoft.Dynamics.AX.Framework.Tools.DMF.SSISHelperService.exe',
                         'MR2012ProcessService', 'MSSQLSERVER', 'SQLSERVERAGENT', 'ReportServer', 'SQLServerReportingServices'
                Get-Service -Name $names -ErrorAction SilentlyContinue |
                    Sort-Object DisplayName | Format-Table Status, StartType, Name, DisplayName -AutoSize | Out-String -Width 160
                """,
        },
        new()
        {
            Id = new("5c1d0a51-0000-4000-8000-000000000003"), Name = Loc.Get("ScriptUptime"),
            Body = """
                $os = Get-CimInstance Win32_OperatingSystem
                "Started:     $($os.LastBootUpTime)  (up $([math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)) h)"
                "Memory free: $([math]::Round($os.FreePhysicalMemory / 1MB, 1)) of $([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) GB"
                Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 8 Name, Id,
                    @{ n = 'Memory MB'; e = { [math]::Round($_.WorkingSet64 / 1MB) } },
                    @{ n = 'CPU s'; e = { [math]::Round($_.CPU) } } |
                    Format-Table -AutoSize | Out-String -Width 120
                """,
        },
        new()
        {
            Id = new("5c1d0a51-0000-4000-8000-000000000004"), Name = Loc.Get("ScriptRestartIis"), Confirm = true,
            Body = """
                $ErrorActionPreference = 'Stop'
                try {
                    iisreset /restart | Out-String
                    Get-Service W3SVC | Format-Table Status, Name, DisplayName -AutoSize | Out-String
                }
                catch { Write-Error "IIS was not restarted: $($_.Exception.Message)" }
                """,
        },
        new()
        {
            Id = new("5c1d0a51-0000-4000-8000-000000000005"), Name = Loc.Get("ScriptRestartBatch"), Confirm = true,
            Body = """
                $ErrorActionPreference = 'Stop'
                try {
                    Restart-Service -Name DynamicsAxBatch -Force
                    Get-Service DynamicsAxBatch | Format-Table Status, Name, DisplayName -AutoSize | Out-String
                }
                catch { Write-Error "The batch service was not restarted: $($_.Exception.Message)" }
                """,
        },
        new()
        {
            Id = new("5c1d0a51-0000-4000-8000-000000000006"), Name = Loc.Get("ScriptPendingReboot"),
            Body = """
                $signs = [ordered]@{
                    'Windows Update'         = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
                    'Component servicing'    = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
                    'Pending file renames'   = $null -ne (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue)
                }
                $signs.GetEnumerator() | Format-Table Name, Value -AutoSize | Out-String
                if ($signs.Values -contains $true) { 'A restart is waiting.' } else { 'No restart is waiting.' }
                """,
        },
    ];

    /// <summary>Whether scripts can run on it: through Azure when it is linked to a VM, over PowerShell remoting otherwise.</summary>
    public static bool CanRun(Connection? c) => c is { Host.Length: > 0 } || c?.AzureVm != null;

    /// <summary>The machine as the texts name it.</summary>
    public static string Target(Connection c) => c.AzureVm?.Name ?? c.Host;

    /// <summary>
    /// What the script signs in with over remoting: the connection's saved credential, or nothing, which is the
    /// signed-in Windows account (a domain's single sign-on). An Azure machine needs neither. Reads the saved password,
    /// so it may ask for the master password: call it where a prompt can show.
    /// </summary>
    public static (string? User, string? Password) SignIn(Connection c)
    {
        if (c.AzureVm != null || App.Store.Resolve(c).Credential is not { } credential) return (null, null);
        // An account to use, but no password to use it with: said, instead of running as this Windows user unseen.
        if (credential.Secret is not { } secret)
            throw new InvalidOperationException(Loc.Format("ScriptNoPassword", credential.FullUser));
        return (credential.FullUser, Store.Unprotect(secret));
    }

    /// <summary>Its sign-in needs the master password, which is not entered.</summary>
    public static bool NeedsMasterPassword(Connection c) =>
        c.AzureVm == null && App.Store.Resolve(c).Credential?.Secret is { } secret && MasterPassword.IsSealed(secret) && !MasterPassword.Unlocked;

    /// <summary>Runs it on the machine: through Azure when the connection is linked to a VM, over PowerShell remoting otherwise.</summary>
    public static Task<(string Output, string Errors)> RunOnAsync(Connection c, RemoteScript script, (string? User, string? Password) signIn,
                                                                 CancellationToken cancel) =>
        c.AzureVm is { } vm ? AzureVms.RunScriptAsync(vm, script.Body, cancel)
            : RemotePowerShell.RunAsync(c.Host, signIn.User, signIn.Password, script.Body, cancel);

    /// <summary>
    /// Asks (a script that changes something), then runs it in a window of its own that shows what it wrote: one
    /// machine's output, or a table of several.
    /// </summary>
    public static void Run(Window? owner, IReadOnlyList<Connection> machines, RemoteScript script)
    {
        if (machines.Count == 0) return;
        var question = machines.Count == 1 ? Loc.Format("ScriptConfirm", script.Name, Target(machines[0])) : Loc.Format("ScriptConfirmMany", script.Name, machines.Count);
        if (script.Confirm && !Dialog.Confirm(owner, question, Loc.Get("ScriptRun"), DialogKind.Warning)) return;
        if (machines.Count == 1) new ScriptRunWindow(machines[0], script).Show();
        else new ScriptBatchWindow(machines, script).Show();
    }

    public static List<Control> MenuItems(Window? owner, Connection c) => MenuItems(owner, [c]);

    /// <summary>The menu's items: one per script, then the way into the library.</summary>
    public static List<Control> MenuItems(Window? owner, IReadOnlyList<Connection> machines)
    {
        var items = new List<Control>();
        foreach (var script in All)
        {
            var item = new MenuItem { Header = script.Name, Icon = new SymbolIcon { Symbol = script.Confirm ? SymbolRegular.ArrowSync24 : SymbolRegular.Code24 } };
            item.Click += (_, _) => Run(owner, machines, script);
            items.Add(item);
        }
        if (items.Count > 0) items.Add(new Separator());
        var edit = new MenuItem { Header = Loc.Get("ScriptsEdit"), Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24 } };
        edit.Click += (_, _) => new ScriptsWindow { Owner = owner is { IsVisible: true } ? owner : Application.Current.MainWindow }.ShowDialog();
        items.Add(edit);
        return items;
    }
}
