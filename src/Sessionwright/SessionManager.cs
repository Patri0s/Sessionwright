using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>
/// Owns the open sessions, each in its own <see cref="SessionWindow"/>: connects (credentials, settings), reconnects in
/// the same window, brings sessions forward and closes them.
/// </summary>
public sealed class SessionManager
{
    readonly Dictionary<RdpSession, SessionWindow> _windows = [];
    static Store Store => App.Store;

    /// <summary>Sessions with a window, in the order they were opened.</summary>
    public ObservableCollection<RdpSession> Open { get; } = [];

    /// <summary>The data changed (last used, a connection or credential saved from the prompt); true: the tree too.</summary>
    public event Action<bool>? DataChanged;

    /// <summary>A connection's data changed outside a session (e.g. its "left running" mark): saved and shown.</summary>
    public void NotifyDataChanged() => DataChanged?.Invoke(false);

    public SessionWindow? WindowOf(RdpSession s) => _windows.GetValueOrDefault(s);

    /// <summary>Brings the previous (-1) / next (+1) open session forward, in the order of the open sessions list.</summary>
    public SessionManager()
    {
        SessionKeys.Switch += (from, direction) => SwitchFrom(from.Session, direction);
        SessionKeys.Overview += from => SessionSwitcher.Toggle(this, from.Session);
    }

    long _lastSwitch;

    public void SwitchFrom(RdpSession current, int direction)
    {
        // One key press may arrive both through our keyboard hook and as the control's focus-release event.
        if (Environment.TickCount64 - _lastSwitch < 300) return;
        _lastSwitch = Environment.TickCount64;
        var open = Open.Where(s => !s.IsClosing).ToList();
        var index = open.IndexOf(current);
        if (open.Count < 2 || index < 0) return;
        Show(open[(index + direction + open.Count) % open.Count]);
    }

    /// <summary>Opens a session of <paramref name="c"/>, or brings its open one forward.</summary>
    public void Connect(Connection c, bool askCredentials = false)
    {
        var mine = Open.Where(s => s.Connection == c && !s.IsClosing).ToList();
        if (!askCredentials && mine.FirstOrDefault(s => s.IsOpen) is { } live)
        {
            Show(live);
            return;
        }
        if (mine.FirstOrDefault(s => !s.IsOpen) is { } dropped)
        {
            // A dropped session reconnects in its window, also with other credentials: no second window, nothing moves.
            Show(dropped); // first, so the credential prompt is not owned by a minimized window
            Reconnect(dropped, askCredentials);
            return;
        }
        // Only "Connect as" next to a session that is still open (or no session at all) opens a new window.
        if (Resolve(Application.Current.MainWindow, c, askCredentials) is not { } r) return;

        var session = new RdpSession(c, r.Options, r.User, r.Domain, r.Password)
        {
            GroupPath = GroupPath(c), Prepare = PrepareAsync, ShutDownVmAfter = c.AzureVm != null && Store.ShutDownVmAfterOf(c),
        };
        session.ReconnectRequested += Reconnect;
        session.SwitchRequested += SwitchFrom;
        // Logged on or ended: its "left running" mark changed and is saved at once.
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RdpSession.Phase) && session.Phase is SessionPhase.Ready or SessionPhase.Disconnected or SessionPhase.Closed)
                DataChanged?.Invoke(false);
        };
        session.Closed += s =>
        {
            Open.Remove(s);
            _windows.Remove(s);
            if (s.ShutDownVmAfter) _vmShutdowns.Add(ShutDownVmAfterAsync(s.Connection, s.ConnectedAt ?? DateTime.Now));
        };
        var window = new SessionWindow(session);
        window.Closed += (_, _) => DataChanged?.Invoke(false); // its placement was saved
        _windows[session] = window;
        Open.Add(session);
        window.Open(c.Placement is { } saved && Native.IsOnScreen(saved) ? saved : NewPlacement());
    }

    /// <summary>Connects again with what the connection resolves to now (edited credentials and settings apply).</summary>
    void Reconnect(RdpSession s, bool askCredentials)
    {
        if (Resolve(WindowOf(s), s.Connection, askCredentials) is not { } r) return;
        s.GroupPath = GroupPath(s.Connection);
        s.Configure(r.Options, r.User, r.Domain, r.Password);
        s.Restart();
    }

    /// <summary>
    /// Before each connect: the connection's command when its server does not answer yet (a VPN), then the Azure VM,
    /// then the wait for the server the command was run for. A server that already answers runs nothing.
    /// </summary>
    async Task<bool> PrepareAsync(RdpSession s, CancellationToken cancel)
    {
        var c = s.Connection;
        if (Store.BeforeConnectOf(c) is not { } before || await RdpSession.MeasureRttAsync(c.Host, c.Port) != null)
            return await PrepareAzureAsync(s, cancel);

        var command = BeforeConnecting.Expand(before.Command, c);
        // Only the program's name in the log and on screen: the arguments may hold a password.
        var program = System.IO.Path.GetFileName(BeforeConnecting.Split(command).Program);
        try
        {
            BeforeConnecting.Start(command);
            Log.Info($"{c.Title}: started {program} before connecting");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Windows' own reason ("The system cannot find the file specified"), not .NET's sentence around it.
            var reason = ex is System.ComponentModel.Win32Exception w ? new System.ComponentModel.Win32Exception(w.NativeErrorCode).Message : ex.Message;
            Log.Info($"{c.Title}: {program} could not be started: {reason}");
            s.Halt(Loc.Format("BeforeConnectFailed", program, reason));
            return false;
        }

        var minutes = Math.Clamp(before.WaitMinutes, 1, 60);
        // Said at once: the first answer from a server that is not there yet takes a few seconds on Windows.
        s.Message = Loc.Format("BeforeConnectWaiting", program, c.Address, minutes * 60);
        if (!await PrepareAzureAsync(s, cancel)) return false;
        var deadline = DateTime.Now.AddMinutes(minutes);
        while (await RdpSession.MeasureRttAsync(c.Host, c.Port) == null)
        {
            var left = deadline - DateTime.Now;
            if (left <= TimeSpan.Zero)
            {
                Log.Info($"{c.Title}: {c.Address} did not answer within {minutes} min after {program}");
                s.Halt(Loc.Format("BeforeConnectTimeout", program, c.Address, minutes));
                return false;
            }
            s.Message = Loc.Format("BeforeConnectWaiting", program, c.Address, (int)Math.Ceiling(left.TotalSeconds));
            await Task.Delay(Timings.BeforeConnectPoll, cancel);
        }
        return true;
    }

    /// <summary>
    /// Before connecting to a linked Azure VM: starts it when it is off (after asking) and waits until it runs and its RDP
    /// port answers. When Azure cannot be asked, it connects anyway: the machine may well be running.
    /// </summary>
    async Task<bool> PrepareAzureAsync(RdpSession s, CancellationToken cancel)
    {
        var c = s.Connection;
        if (c.AzureVm is not { } vm) return true;
        try
        {
            var started = false;
            var deadline = DateTime.Now + Timings.VmStart;
            while (true)
            {
                var state = await AzureVms.StateAsync(vm, cancel);
                c.VmState = state;
                if (state == VmPowerState.Running) break;
                if (DateTime.Now > deadline)
                {
                    s.Halt(Loc.Format("AzureVmStartTimeout", vm.Name, AzureVms.Describe(state)));
                    return false;
                }
                if (!started && state is VmPowerState.Stopped or VmPowerState.Deallocated)
                {
                    if (!Dialog.Confirm(WindowOf(s), Loc.Format("AzureVmStartConfirm", vm.Name, AzureVms.Describe(state)), Loc.Get("AzureVmStart")))
                    {
                        s.Close();
                        return false;
                    }
                    await AzureVms.StartAsync(vm, cancel);
                    started = true;
                    c.VmState = VmPowerState.Starting;
                }
                // Shutting down: Azure refuses a start until it is off, so wait for that first.
                s.Message = Loc.Format(state is VmPowerState.Stopping or VmPowerState.Deallocating && !started ? "AzureVmWaitStop" : "AzureVmStarting", vm.Name);
                await Task.Delay(Timings.VmPoll, cancel);
            }
            if (!started) return true;
            // Running comes before Windows has booted; connecting now would only fail.
            s.Message = Loc.Format("AzureVmBooting", vm.Name);
            deadline = DateTime.Now + Timings.VmBoot;
            while (await RdpSession.MeasureRttAsync(c.Host, c.Port) == null && DateTime.Now < deadline)
                await Task.Delay(Timings.VmPoll, cancel);
            return true;
        }
        catch (AzureSignInRequiredException ex) when (AzureVms.Account(vm) is { } account)
        {
            Log.Info($"Azure: {ex.Message}");
            if (!Dialog.Confirm(WindowOf(s), ex.Message, Loc.Get("AzureSignInAgain")))
            {
                s.Halt(ex.Message);
                return false;
            }
            s.Message = Loc.Get("AzureSignInBrowser");
            await AzureVms.ReauthenticateAsync(account, vm.TenantId, WindowOf(s) is { } w ? Native.Handle(w) : default, cancel);
            return await PrepareAzureAsync(s, cancel);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException
                                       or AzureSignInRequiredException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            Log.Error(ex, $"Azure: state of {vm.Name}");
            return true;
        }
    }

    /// <summary>"Shut down the machine after disconnecting": once no other session on it is open.</summary>
    async Task ShutDownVmAfterAsync(Connection c, DateTime connectedAt)
    {
        if (c.AzureVm is not { } vm) return;
        if (Open.Any(o => o.Connection.AzureVm is { } other && AzureVms.Same(other, vm)))
        {
            Log.Info($"Azure: {vm.Name} stays on, another session on it is open");
            return;
        }
        // A shared machine stays on while someone else is signed in, or started it again since this session began.
        if (await SharedUse.OthersAsync(c, connectedAt) is { Count: > 0 } others)
        {
            Log.Info($"Azure: {vm.Name} stays on, someone else uses it: {string.Join("; ", others)}");
            Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.People24, Loc.Format("SharedNotShutDown", vm.Name), string.Join("\n", others),
                TimeSpan.FromSeconds(30));
            return;
        }
        try
        {
            await AzureVms.DeallocateAsync(vm);
            c.VmState = VmPowerState.Deallocating;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or AzureSignInRequiredException
                                       or TaskCanceledException)
        {
            Log.Error(ex, $"Azure: shutting {vm.Name} down after the session");
            Notification.Problem(null, Loc.Get("ProblemStopTitle"), Loc.Format("AzureVmStopFailed", vm.Name, ex.Message));
        }
    }

    public void Show(RdpSession s)
    {
        if (!s.IsClosing && WindowOf(s) is { } w) Native.Restore(w); // a closing window is hidden and stays so
    }

    /// <summary>Disconnects every session without asking (the caller asked) and completes once all are closed.</summary>
    public async Task CloseAllAsync()
    {
        var all = Open.ToList();
        var closed = all.Select(s =>
        {
            var done = new TaskCompletionSource();
            s.Closed += _ => done.TrySetResult();
            return done.Task;
        }).ToList();
        foreach (var s in all) WindowOf(s)?.Disconnect();
        await Task.WhenAny(Task.WhenAll(closed), Task.Delay(Timings.CloseGuard + 1000));
        // The caller may exit right after: a VM's "shut down after disconnecting" has to reach Azure first.
        _vmShutdowns.RemoveAll(t => t.IsCompleted);
        await Task.WhenAny(Task.WhenAll(_vmShutdowns), Task.Delay(20000));
    }

    readonly List<Task> _vmShutdowns = [];

    /// <summary>
    /// Signs every signed-in session out, one after another: each types into its own session, which needs the keyboard.
    /// The others (still connecting, ended, or not signed out after all) are left to the caller to disconnect.
    /// </summary>
    public async Task SignOutAllAsync()
    {
        foreach (var s in Open.Where(s => s.IsReady && !s.IsClosing).ToList())
            if (WindowOf(s) is { } window) await window.SignOutAsync(ask: false);
    }

    /// <summary>
    /// Opens a link in the connection's session on the server; connects first when it has none open, and waits until it
    /// is signed in (an Azure VM may have to start: up to a few minutes).
    /// </summary>
    public async Task OpenLinkInSessionAsync(Connection c, string url)
    {
        var session = Open.FirstOrDefault(s => s.Connection == c && s.IsOpen);
        var fresh = session == null;
        if (session == null)
        {
            Connect(c);
            session = Open.LastOrDefault(s => s.Connection == c);
            if (session == null) return; // the credential prompt was cancelled
        }
        for (var waited = 0; !session.IsReady && session.Phase != SessionPhase.Closed && waited < 300_000; waited += 500) await Task.Delay(500);
        if (!session.IsReady) return;
        if (fresh) await session.WaitWhileChangingAsync(); // the desktop still coming up after the sign-in
        if (WindowOf(session) is { } window) await window.OpenLinkAsync(url);
    }

    public string GroupPath(Connection c) => string.Join(" › ", Store.Ancestors(c).Reverse().Select(g => g.Name));

    public void UpdateGroupPaths()
    {
        foreach (var s in Open) s.GroupPath = GroupPath(s.Connection);
    }

    sealed record Resolved(RdpOptions Options, string? User, string? Domain, byte[]? Password);

    /// <summary>The connection's credential and settings; asks for credentials when none (or an unusable one) is stored.</summary>
    Resolved? Resolve(Window? owner, Connection c, bool askCredentials)
    {
        var stored = Store.Data.Connections.Contains(c);
        var (credential, options) = Store.Resolve(c);
        string? user = credential?.User, domain = credential?.Domain;
        byte[]? password = null;
        var unreadable = false;

        if (!askCredentials && credential?.Secret is { } secret)
        {
            try
            {
                Store.Unprotect(secret); // readable for this Windows account (not a file copied from another one)
                password = secret; // the session keeps it protected
            }
            catch (CryptographicException)
            {
                askCredentials = unreadable = true;
            }
        }
        else askCredentials = true;

        var treeChanged = false;
        if (askCredentials)
        {
            var dlg = new CredentialPrompt(c, credential, stored, unreadable) { Owner = owner is { IsVisible: true } ? owner : null };
            if (dlg.ShowDialog() != true) return null;
            (user, domain) = (dlg.User, dlg.Domain);
            password = string.IsNullOrEmpty(dlg.Password) ? null : Store.Protect(dlg.Password);

            if (dlg.SaveConnection && !stored)
            {
                Store.Data.Connections.Add(c);
                stored = treeChanged = true;
            }
            if (dlg.SaveCredential && dlg.Password.Length > 0 && (dlg.Credential ?? Store.FindOrCreateCredential(user, domain)) is { } target)
            {
                if (target.User.Length == 0) (target.User, target.Domain) = (user ?? "", domain); // a library entry without a user
                target.SetPassword(dlg.Password);
                // A one-off "Connect as" keeps the connection's own (or inherited) credential.
                if (stored && c.CredentialId == null && credential == null) c.CredentialId = target.Id;
            }
            options = Store.Resolve(c).Options;
        }

        if (stored) c.LastUsed = DateTime.Now;
        DataChanged?.Invoke(treeChanged);
        return new Resolved(options, user, domain, password);
    }

    /// <summary>A new window: the size of the last closed one, cascaded over the main window so a burst stays visible.</summary>
    WindowPlacement NewPlacement()
    {
        var main = Application.Current.MainWindow;
        var mainBounds = main != null && main.IsLoaded ? Native.Bounds(main) : Screen.PrimaryScreen!.WorkingArea;
        var area = Screen.FromRectangle(mainBounds).WorkingArea;
        var scale = main != null && main.IsLoaded ? Native.DpiOf(main) / 96.0 : 1;
        var last = Store.Data.Settings.SessionPlacement;
        var width = Math.Min(last?.Width ?? (int)(1280 * scale), area.Width);
        var height = Math.Min(last?.Height ?? (int)(800 * scale), area.Height);
        var step = (int)(28 * scale) * (Open.Count % 8);
        return new WindowPlacement
        {
            X = Math.Clamp(mainBounds.X + (mainBounds.Width - width) / 2 + step, area.Left, Math.Max(area.Left, area.Right - width)),
            Y = Math.Clamp(mainBounds.Y + (mainBounds.Height - height) / 2 + step, area.Top, Math.Max(area.Top, area.Bottom - height)),
            Width = width,
            Height = height,
            Maximized = last?.Maximized == true,
        };
    }
}
