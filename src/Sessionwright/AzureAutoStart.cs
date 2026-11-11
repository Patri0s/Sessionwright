using System.Globalization;
using System.IO;

namespace Sessionwright;

/// <summary>
/// Starts linked Azure VMs ahead of work, at the time set on their connection or group. Plain Azure VMs have no start
/// schedule of their own (only auto-shutdown), so this is Sessionwright's: it runs while Sessionwright runs (Settings can start it
/// with Windows, in the tray). A time missed while it did not run (the PC was off) is caught up within a few hours the
/// same day; each VM is started at most once a day, so a machine shut down by hand later that day stays off.
/// </summary>
static class AzureAutoStart
{
    static Store Store => App.Store;
    internal static readonly TimeSpan CatchUp = TimeSpan.FromHours(3);
    static bool _busy;
    const int Attempts = 3;
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    static readonly Dictionary<string, (DateTime Day, int Count, DateTime RetryAt)> Failed = [];

    static string Key(AzureVmLink vm) => $"{vm.SubscriptionId}/{vm.ResourceGroup}/{vm.Name}".ToLowerInvariant();

    /// <summary>Starts the VMs that are due at <paramref name="now"/> (local time).</summary>
    public static async Task CheckAsync(DateTime now)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var due = Store.Data.Connections
                .Where(c => c.AzureVm != null && Due(Store.AutoStartOf(c), c.AzureVm, now))
                .DistinctBy(c => Key(c.AzureVm!)).ToList();
            foreach (var c in due)
            {
                var vm = c.AzureVm!;
                var same = Store.Data.Connections.Where(x => x.AzureVm != null && Key(x.AzureVm) == Key(vm)).ToList();
                // Once a day: done for today when it started, was running, or failed for the last time.
                void MarkDone()
                {
                    foreach (var x in same) x.AzureVm!.AutoStartedOn = now.Date;
                    Failed.Remove(Key(vm));
                    try { Store.Save(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error(ex, "Saving data failed"); // the mark stays in memory and goes out with the next save
                    }
                }
                var failed = Failed.GetValueOrDefault(Key(vm));
                if (failed.Day != now.Date) failed = default;
                if (now < failed.RetryAt) continue;
                if (c.VmState is VmPowerState.Running or VmPowerState.Starting)
                {
                    MarkDone();
                    Log.Info($"Azure: {vm.Name} already running at its auto-start time");
                    continue;
                }
                try
                {
                    await AzureVms.StartAsync(vm);
                    MarkDone();
                    foreach (var x in same) x.VmState = VmPowerState.Starting;
                    Log.Info($"Azure: {vm.Name} started by its auto-start ({Store.AutoStartOf(c)})");
                    Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.Play24, Loc.Format("AutoStartToastTitle", vm.Name),
                        Loc.Format("AutoStartToastText", Store.AutoStartOf(c)), TimeSpan.FromSeconds(20),
                        (Loc.Get("Connect"), () => App.Sessions.Connect(c))); // the connect waits until it runs
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or AzureSignInRequiredException
                                               or TaskCanceledException)
                {
                    Log.Error(ex, $"Azure: auto-start of {vm.Name}");
                    // The network is often not up yet right after the PC woke: tried again, a few times, minutes apart
                    // (no retry storm against Azure every 20 s).
                    if (failed.Count + 1 < Attempts)
                    {
                        Failed[Key(vm)] = (now.Date, failed.Count + 1, now + RetryAfter);
                        continue;
                    }
                    MarkDone();
                    Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, Loc.Format("AutoStartFailedTitle", vm.Name), ex.Message,
                        TimeSpan.FromMinutes(1));
                }
            }
        }
        finally { _busy = false; }
    }

    /// <summary>Its start time today has come, not longer ago than <see cref="CatchUp"/>, and it was not started today yet.</summary>
    internal static bool Due(VmAutoStart? plan, AzureVmLink vm, DateTime now)
    {
        if (plan is not { Days: not AutoStartDays.Off } || !plan.Includes(now.DayOfWeek) || ParseTime(plan.Time) is not { } time) return false;
        var at = now.Date + time.ToTimeSpan();
        return now >= at && now < at + CatchUp && vm.AutoStartedOn?.Date != now.Date;
    }

    /// <summary>"8:00", "08:00", "8.00" → 08:00; null if not a time.</summary>
    internal static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim().Replace('.', ':'), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
