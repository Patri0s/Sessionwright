namespace Sessionwright;

/// <summary>
/// Auto-shutdown of the linked Azure VMs ("Auto-shutdown" in the portal): when it comes, a warning while a session is
/// open, postponing it and skipping it once. Azure has no call for either (the e-mail links go through its own
/// service), so Sessionwright changes the schedule and puts it back once the moment has passed:
/// - postpone moves the daily time; should Sessionwright not run to put it back, the machine still shuts down daily, later;
/// - skip turns the schedule off until the skipped time has passed; put back the next time Sessionwright runs (until then
///   the machine is not shut down automatically).
/// </summary>
static class AzureShutdown
{
    static Store Store => App.Store;
    static readonly TimeSpan WarnAhead = TimeSpan.FromMinutes(15);
    static readonly HashSet<(string Vm, DateTime At)> Warned = [];
    const string WriteSchedules = "Microsoft.DevTestLab/schedules/write";
    // Per resource group: whether the account may change its schedules (asked again after a while: roles change).
    static readonly Dictionary<string, (bool Can, DateTime Asked)> Rights = [];

    static async Task<bool> CanChangeAsync(AzureVmLink vm)
    {
        var key = $"{vm.Account}/{vm.SubscriptionId}/{vm.ResourceGroup}".ToLowerInvariant();
        if (Rights.TryGetValue(key, out var known) && DateTime.UtcNow - known.Asked < TimeSpan.FromMinutes(30)) return known.Can;
        bool can;
        try { can = await AzureVms.CanAsync(vm, WriteSchedules); }
        catch (Exception ex) when (IsAzureFailure(ex))
        {
            Log.Info($"Azure: rights in {vm.ResourceGroup} unknown: {ex.Message}");
            return true; // unknown: offer it, the change itself reports a refusal
        }
        if (!can && !(known.Asked != default && !known.Can)) Log.Info($"Azure: {vm.Account} may not change the schedules in {vm.ResourceGroup}");
        Rights[key] = (can, DateTime.UtcNow);
        return can;
    }

    static string Key(AzureVmLink vm) => Machines.Key(vm);

    // Its connections and, for a machine without one, its row in the machines window.
    static IEnumerable<Connection> SameVm(AzureVmLink vm) => Machines.SameVm(vm);

    /// <summary>Next auto-shutdown of the running linked VMs (their connections' VmShutdownAt); puts back schedules due.</summary>
    public static async Task RefreshAsync(IReadOnlyCollection<Connection> linked)
    {
        await RestoreDueAsync();
        var running = linked.Where(c => c.AzureVm != null && c.VmState is VmPowerState.Running or VmPowerState.Starting).ToList();
        foreach (var c in linked.Except(running)) c.VmShutdownAt = null;
        // One list call per resource group: the schedules live next to their VMs.
        foreach (var group in running.GroupBy(c => (c.AzureVm!.SubscriptionId.ToLowerInvariant(), c.AzureVm.ResourceGroup.ToLowerInvariant())))
        {
            try
            {
                var schedules = await AzureVms.ShutdownSchedulesAsync(group.First().AzureVm!);
                var readOnly = schedules.Count > 0 && !await CanChangeAsync(group.First().AzureVm!);
                foreach (var c in group) c.VmShutdownReadOnly = readOnly;
                foreach (var c in group)
                {
                    var vm = c.AzureVm!;
                    DateTime? at = null;
                    if (schedules.TryGetValue(vm.Name, out var s))
                    {
                        if (s.Enabled) at = AzureVms.NextShutdown(s.Time, s.TimeZoneId, DateTime.UtcNow);
                        // Skipped (off until then): the one after the skipped shutdown.
                        else if (vm is { ShutdownSkipped: true, ShutdownRestoreAfter: { } skipped })
                            at = AzureVms.NextShutdown(vm.ShutdownOriginal ?? s.Time, s.TimeZoneId, skipped > DateTime.UtcNow ? skipped : DateTime.UtcNow);
                    }
                    if (at != c.VmShutdownAt) Log.Info($"Azure: {vm.Name} shuts down at {at?.ToLocalTime().ToString("g") ?? "(no auto-shutdown)"}");
                    c.VmShutdownAt = at;
                }
            }
            catch (Exception ex) when (IsAzureFailure(ex))
            {
                Log.Info($"Azure: auto-shutdown of {group.First().AzureVm!.ResourceGroup} unavailable: {ex.Message}");
            }
        }
    }

    static bool IsAzureFailure(Exception ex) =>
        ex is System.Net.Http.HttpRequestException or InvalidOperationException or AzureSignInRequiredException or TaskCanceledException;

    static async Task<ShutdownSchedule> ScheduleAsync(AzureVmLink vm, bool enabled = true)
    {
        var schedules = await AzureVms.ShutdownSchedulesAsync(vm);
        return schedules.TryGetValue(vm.Name, out var s) && (s.Enabled || !enabled) ? s
            : throw new InvalidOperationException(Loc.Format("AzureNoShutdown", vm.Name));
    }

    // The usual daily time: kept from the first change until it is put back.
    static string Original(AzureVmLink vm, ShutdownSchedule s) =>
        SameVm(vm).Select(x => x.AzureVm!.ShutdownOriginal).FirstOrDefault(o => o != null) ?? s.Time;

    static void Remember(AzureVmLink vm, string? original, DateTime? restoreAfter, bool skipped, DateTime? next)
    {
        foreach (var x in SameVm(vm))
        {
            (x.AzureVm!.ShutdownOriginal, x.AzureVm.ShutdownRestoreAfter, x.AzureVm.ShutdownSkipped) = (original, restoreAfter, skipped);
            x.VmShutdownAt = null; // the skipped flag lives on the link: raise the change even when the time stays
            x.VmShutdownAt = next;
        }
        // No connection keeps the usual time for a machine without one: the settings do, until it is put back.
        var pending = Store.Data.Settings.PendingShutdowns;
        pending.RemoveAll(l => Key(l) == Key(vm));
        if (original != null && !Machines.LinkedTo(vm).Any()) pending.Add(SameVm(vm).FirstOrDefault()?.AzureVm ?? vm);
        Store.Save();
    }

    /// <summary>Moves the VM's auto-shutdown <paramref name="hours"/> later; throws with a message for the user.</summary>
    public static async Task PostponeAsync(Connection c, int hours)
    {
        var vm = c.AzureVm ?? throw new InvalidOperationException();
        var s = await ScheduleAsync(vm);
        var (time, fireAt) = AzureVms.ShiftShutdown(s.Time, s.TimeZoneId, DateTime.UtcNow, hours);
        await AzureVms.UpdateShutdownAsync(vm, s, time: time);
        var original = Original(vm, s);
        Remember(vm, original, fireAt, skipped: false, fireAt);
        Log.Info($"Azure: auto-shutdown of {vm.Name} postponed to {time} (normally {original})");
    }

    /// <summary>Skips the next auto-shutdown ("Skip this instance" in Azure's e-mail); the one after it happens as usual.</summary>
    public static async Task SkipAsync(Connection c)
    {
        var vm = c.AzureVm ?? throw new InvalidOperationException();
        var s = await ScheduleAsync(vm);
        var skipped = AzureVms.NextShutdown(s.Time, s.TimeZoneId, DateTime.UtcNow) ?? throw new InvalidOperationException(Loc.Format("AzureNoShutdown", vm.Name));
        await AzureVms.UpdateShutdownAsync(vm, s, enabled: false);
        var original = Original(vm, s);
        Remember(vm, original, skipped, skipped: true, AzureVms.NextShutdown(original, s.TimeZoneId, skipped));
        Log.Info($"Azure: auto-shutdown of {vm.Name} at {skipped.ToLocalTime():g} skipped");
    }

    /// <summary>Undoes a skip or a postponement: the usual time, on.</summary>
    public static async Task ResetAsync(Connection c)
    {
        var vm = c.AzureVm ?? throw new InvalidOperationException();
        var s = await ScheduleAsync(vm, enabled: false);
        var original = Original(vm, s);
        await AzureVms.UpdateShutdownAsync(vm, s, time: original, enabled: true);
        Remember(vm, null, null, skipped: false, AzureVms.NextShutdown(original, s.TimeZoneId, DateTime.UtcNow));
        Log.Info($"Azure: auto-shutdown of {vm.Name} back to {original}");
    }

    /// <summary>Puts the usual time back on (and turns on again) schedules whose postponed or skipped shutdown has passed.</summary>
    static async Task RestoreDueAsync()
    {
        var due = Store.Data.Connections.Select(c => c.AzureVm).OfType<AzureVmLink>().Concat(Store.Data.Settings.PendingShutdowns)
            .Where(l => l is { ShutdownOriginal: not null, ShutdownRestoreAfter: { } after } && after < DateTime.UtcNow - TimeSpan.FromMinutes(1))
            .DistinctBy(Key).ToList();
        foreach (var vm in due)
        {
            try
            {
                if ((await AzureVms.ShutdownSchedulesAsync(vm)).TryGetValue(vm.Name, out var s))
                {
                    var time = s.Time != vm.ShutdownOriginal ? vm.ShutdownOriginal : null;
                    bool? enable = vm.ShutdownSkipped && !s.Enabled ? true : null;
                    if (time != null || enable != null) await AzureVms.UpdateShutdownAsync(vm, s, time, enable);
                }
                Log.Info($"Azure: auto-shutdown of {vm.Name} back at {vm.ShutdownOriginal}");
                foreach (var link in SameVm(vm).Select(x => x.AzureVm!).Append(vm))
                    (link.ShutdownOriginal, link.ShutdownRestoreAfter, link.ShutdownSkipped) = (null, null, false);
                Store.Data.Settings.PendingShutdowns.RemoveAll(l => Key(l) == Key(vm));
                Store.Save();
            }
            catch (Exception ex) when (IsAzureFailure(ex))
            {
                Log.Info($"Azure: auto-shutdown of {vm.Name} not put back yet: {ex.Message}"); // tried again with the next refresh
            }
        }
    }

    /// <summary>Asks once, 15 minutes ahead, whether to postpone the shutdown of a VM with an open session.</summary>
    public static void CheckWarnings(SessionManager sessions)
    {
        foreach (var s in sessions.Open.Where(s => s.IsOpen && !s.IsClosing).ToList())
        {
            if (s.Connection is not { AzureVm: { ShutdownSkipped: false } vm, VmShutdownAt: { } at }) continue;
            var left = at - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || left > WarnAhead || !Warned.Add((Key(vm), at))) continue;
            Warn(sessions, s, vm, at, left);
        }
    }

    // A corner notification, not a dialog over the work in the session; it goes once the machine is down.
    static void Warn(SessionManager sessions, RdpSession s, AzureVmLink vm, DateTime at, TimeSpan left)
    {
        var owner = sessions.WindowOf(s);
        var c = s.Connection;
        var title = Loc.Format("AzureShutdownToastTitle", vm.Name, at.ToLocalTime().ToString("t"));
        var minutes = Math.Max(1, (int)Math.Round(left.TotalMinutes));
        var stays = left + TimeSpan.FromMinutes(1);
        if (c.VmShutdownReadOnly)
        {
            // Without the right to change it, Azure's own e-mail (its "Postpone" / "Skip" links) is the way.
            Notification.Show(owner, Wpf.Ui.Controls.SymbolRegular.Clock24, title, Loc.Format("AzureShutdownToastReadOnly", minutes), stays);
            return;
        }
        Notification.Show(owner, Wpf.Ui.Controls.SymbolRegular.Clock24, title, Loc.Format("AzureShutdownToastText", minutes), stays,
            (Loc.Get("AzurePostpone1h"), () => _ = WithFeedbackAsync(owner, c, () => PostponeAsync(c, 1))),
            (Loc.Get("AzureSkipShort"), () => _ = WithFeedbackAsync(owner, c, () => SkipAsync(c))));
    }

    /// <summary>The VM button of a session (title bar, full-screen bar): icon, short text (the shutdown time) and tooltip.</summary>
    public static (Wpf.Ui.Controls.SymbolRegular Icon, string? Text, string Tip) Indicator(RdpSession s)
    {
        var c = s.Connection;
        var icon = s.ShutDownVmAfter ? Wpf.Ui.Controls.SymbolRegular.Power20
            : c.VmShutdownSkipped ? Wpf.Ui.Controls.SymbolRegular.CalendarCancel20
            : c.VmShutdownAt != null ? Wpf.Ui.Controls.SymbolRegular.Clock20
            : Wpf.Ui.Controls.SymbolRegular.Cloud20;
        var lines = new[]
        {
            Loc.Format("AzureVmIndicator", c.AzureVm?.Name, AzureVms.Describe(c.VmState)),
            c.VmShutdownTip,
            s.ShutDownVmAfter ? Loc.Get("AzureShutDownAfterOn") : null,
        };
        return (icon, c.VmShutdownText, string.Join("\n", lines.OfType<string>()));
    }

    /// <summary>
    /// The VM menu of a session under <paramref name="target"/>: postpone by 1 / 2 / 4 h and skip (or undo the skip) the
    /// auto-shutdown, and shutting the machine down after disconnecting.
    /// </summary>
    public static void ShowVmMenu(System.Windows.UIElement target, System.Windows.Window owner, RdpSession session, Action? closed = null)
    {
        var c = session.Connection;
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        void Add(string header, Func<Task> action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => _ = WithFeedbackAsync(owner, c, action);
            menu.Items.Add(item);
        }
        // What is run on the machine itself (restart IIS, free disk space): at hand where the session is.
        var scripts = new System.Windows.Controls.MenuItem { Header = Loc.Get("ScriptsMenu"), Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Code24 } };
        foreach (var item in Scripts.MenuItems(owner, c)) scripts.Items.Add(item);
        menu.Items.Add(scripts);
        menu.Items.Add(new System.Windows.Controls.Separator());
        if (c.VmShutdownReadOnly && c.VmShutdownAt != null)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = Loc.Get("AzureShutdownNoRightsMenu"), IsEnabled = false });
        else if (c.VmShutdownSkipped) Add(Loc.Get("AzureUnskipShutdown"), () => ResetAsync(c));
        else if (c.VmShutdownAt != null)
        {
            foreach (var hours in new[] { 1, 2, 4 }) Add(Loc.Format("AzurePostponeHours", hours), () => PostponeAsync(c, hours));
            menu.Items.Add(new System.Windows.Controls.Separator());
            Add(Loc.Format("AzureSkipShutdown", c.VmShutdownWhen), () => SkipAsync(c));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        // The menu's own check mark has no color in this theme: the same mark, in the accent color, while it is on.
        var on = session.ShutDownVmAfter;
        Wpf.Ui.Controls.SymbolIcon? mark = null;
        if (on)
        {
            mark = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark20 };
            mark.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        }
        var after = new System.Windows.Controls.MenuItem { Header = Loc.Get("AzureShutDownAfter"), Icon = mark };
        System.Windows.Automation.AutomationProperties.SetName(after, $"{Loc.Get("AzureShutDownAfter")} ({Loc.Get(on ? "On" : "Off")})");
        after.Click += (_, _) => session.ShutDownVmAfter = !on;
        menu.Items.Add(after);
        // A click on the button while open only closes the menu (it would reopen it at once), like the main "More" menu.
        target.IsHitTestVisible = false;
        menu.Closed += (_, _) =>
        {
            target.IsHitTestVisible = true;
            closed?.Invoke();
        };
        menu.IsOpen = true;
    }

    /// <summary>Runs a schedule change and reports a failure in a dialog (menus, the warning).</summary>
    public static async Task WithFeedbackAsync(System.Windows.Window? owner, Connection c, Func<Task> change)
    {
        try { await change(); }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden && c.AzureVm is { } vm)
        {
            Log.Info($"Azure: {vm.Account} may not change the auto-shutdown of {vm.Name}: {ex.Message}");
            foreach (var x in SameVm(vm)) x.VmShutdownReadOnly = true;
            Rights[$"{vm.Account}/{vm.SubscriptionId}/{vm.ResourceGroup}".ToLowerInvariant()] = (false, DateTime.UtcNow);
            Notification.Problem(owner, Loc.Get("ProblemNoRightsTitle"), Loc.Format("AzureShutdownNoRights", vm.Name, vm.ResourceGroup), warning: true);
        }
        catch (Exception ex) when (IsAzureFailure(ex))
        {
            Log.Error(ex, $"Azure: changing the auto-shutdown of {c.AzureVm?.Name}");
            Notification.Problem(owner, Loc.Get("ProblemPostponeTitle"), Loc.Format("AzurePostponeFailed", c.AzureVm?.Name, ex.Message));
        }
    }
}
