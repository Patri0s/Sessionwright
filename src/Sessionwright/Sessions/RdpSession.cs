using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinForms = System.Windows.Forms;

namespace Sessionwright;

// Late-bound wrapper over mstscax.dll. CLSID / DIID / DispIds verified against the
// type library shipped with Windows (MsRdpClient9NotSafeForScripting, IMsTscAxEvents),
// so no aximp-generated interop assemblies are needed.
sealed class RdpControl : WinForms.AxHost
{
    ConnectionPointCookie? _cookie;

    public RdpEvents Sink { get; } = new();

    public RdpControl() : base("8b918b82-7985-4c24-89df-c33ad2bbfbcd") { }

    public dynamic Ocx => GetOcx() ?? throw new InvalidOperationException("RDP ActiveX not created.");

    protected override void CreateSink() => _cookie = new ConnectionPointCookie(GetOcx()!, Sink, typeof(IMsTscAxEvents));

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }
}

[ComImport, Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(8)] void OnRequestGoFullScreen();
    [DispId(9)] void OnRequestLeaveFullScreen();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(23)] void OnFocusReleased(int direction);
    [DispId(32)] void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt);
    [DispId(33)] void OnAutoReconnected();
    [DispId(34)] void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEvents : IMsTscAxEvents
{
    public event Action? Connected, LoginComplete, RequestGoFullScreen, RequestLeaveFullScreen, RequestMinimize, AutoReconnected;
    public event Action<int>? Disconnected, FatalError, FocusReleased;
    public event Action<int, int>? DesktopSizeChanged, AutoReconnecting;
    public event Action<int, int, int>? NetworkStatusChanged;

    public void OnConnected() => Connected?.Invoke();
    public void OnLoginComplete() => LoginComplete?.Invoke();
    public void OnDisconnected(int discReason) => Disconnected?.Invoke(discReason);
    public void OnRequestGoFullScreen() => RequestGoFullScreen?.Invoke();
    public void OnRequestLeaveFullScreen() => RequestLeaveFullScreen?.Invoke();
    public void OnFatalError(int errorCode) => FatalError?.Invoke(errorCode);
    public void OnRemoteDesktopSizeChange(int width, int height) => DesktopSizeChanged?.Invoke(width, height);
    public void OnRequestContainerMinimize() => RequestMinimize?.Invoke();
    public void OnFocusReleased(int direction) => FocusReleased?.Invoke(direction);
    public void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt) => NetworkStatusChanged?.Invoke((int)qualityLevel, bandwidth, rtt);
    public void OnAutoReconnected() => AutoReconnected?.Invoke();
    public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount) =>
        AutoReconnecting?.Invoke(attemptCount, maxAttemptCount);
}

// IUnknown-based (not reachable through `dynamic`); vtable verified against mstscax.dll: put_Property is slot 3.
[ComImport, Guid("302d8188-0052-4807-806a-362b628f9ac5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMsRdpExtendedSettings
{
    void PutProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    void GetProperty([MarshalAs(UnmanagedType.BStr)] string name, out object value);
}

/// <summary>
/// Connecting: transport and authentication. LoggingOn: connected, the server is signing the user in (its own logon
/// screens stay behind the status screen). Ready: logged on. Reconnecting: the control reconnects by itself after a
/// network drop. Disconnected: ended or failed, the window offers Reconnect. Closed: disposed.
/// </summary>
public enum SessionPhase { Connecting, LoggingOn, Ready, Reconnecting, Disconnected, Closed }

/// <summary>Heuristics in one place; each is logged with its measured counterpart where one exists.</summary>
static class Timings
{
    /// <summary>After logon Windows still shows a stale cached frame and the lock screen sliding away, with no event.</summary>
    public const int LogonSettle = 1300;
    /// <summary>The longest extra wait, under the reveal's cover, while a slow server's screen still changes after it.</summary>
    public const int LogonSettleExtra = 1500;
    /// <summary>The repaint after a resolution change, when the control's pixels cannot be read.</summary>
    public const int DisplaySettle = 500;
    public const int DisplayTimeout = 3000;
    /// <summary>The longest wait for the repaint after a resolution change (a desktop that really is black).</summary>
    public const int RepaintTimeout = 1500;
    /// <summary>Live window resizing: the remote resolution follows once the size stops changing.</summary>
    public const int ResizeDebounce = 300;
    public const int OfferRemoteScreen = 6000;
    /// <summary>Logon never reported (a logon banner or the server's own sign-in screen waiting for input).</summary>
    public const int RevealLogonScreen = 30000;
    /// <summary>
    /// The same two for a new sign-in (no session of the user's left on the server): Windows then loads the profile and
    /// builds the desktop, which right after the machine has started took 28 s on a dev VM. Slow is the rule there, not
    /// a sign that the server waits for something: "preparing the desktop" is said for 45 s (at 25 s, a 28 s sign-in was
    /// told it took longer than usual), the server's screen shown after the same 30 s more as before.
    /// </summary>
    public const int OfferRemoteScreenNew = 45000, RevealLogonScreenNew = 75000;
    public const int CloseGuard = 5000;
    /// <summary>How often the latency to the server is measured while a session is open.</summary>
    public const int LatencyProbe = 30000;
    /// <summary>Azure VM start: how often its state is asked, how long it may take, then how long Windows may take to boot.</summary>
    public const int VmPoll = 5000;
    /// <summary>How often the server is asked while a command run before connecting (a VPN) comes up.</summary>
    public const int BeforeConnectPoll = 2000;
    public static readonly TimeSpan VmStart = TimeSpan.FromMinutes(10), VmBoot = TimeSpan.FromMinutes(5);
    /// <summary>How long a machine woken by Wake-on-LAN may take until its RDP port answers.</summary>
    public static readonly TimeSpan WakeBoot = TimeSpan.FromMinutes(3);
}

public sealed partial class RdpSession : ObservableObject, IConnecting
{
    enum Intent { None, Close, Reconnect, SignOut }

    readonly RdpControl _rdp = new();
    // The control's parent: the control is laid out inside it in physical pixels (even sizes for the server, a fixed
    // or all-monitors desktop scaled to fit and centered).
    readonly WinForms.Panel _frame = new() { Dock = WinForms.DockStyle.Fill, BackColor = Cover.Stage };
    readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(Timings.ResizeDebounce) };
    readonly DispatcherTimer _logonTimer = new();
    RdpOptions _options;
    string? _user, _domain;
    byte[]? _password; // DPAPI-protected, decrypted only while connecting
    System.Drawing.Size _desktop; // the remote desktop size, as the control reports it
    (System.Drawing.Size Size, uint Scale) _requested; // last display update sent (or the size connected with)
    TaskCompletionSource? _display; // completes when the server reports the requested size
    Intent _intent;
    bool _started, _frozen, _loggedOn, _disposed;
    DateTime _logonStarted;

    [ObservableProperty] private SessionPhase _phase;
    [ObservableProperty] private string _message = "";
    /// <summary>More on <see cref="Message"/>, for the status screen only: what Windows says about a failed connection.</summary>
    [ObservableProperty] private string _details = "";
    /// <summary>The session ended with an error (not a normal sign-out or disconnect).</summary>
    [ObservableProperty] private bool _isError;
    /// <summary>The remote desktop is on screen (set by the window once it revealed it).</summary>
    [ObservableProperty] private bool _showsDesktop;
    /// <summary>Logon is taking long: the status screen offers to show what the server shows.</summary>
    [ObservableProperty] private bool _canShowRemoteScreen;
    /// <summary>Sign-out or Ctrl+Alt+Del is typing into the session.</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>
    /// What stands in for its picture while the window gives Windows a still one (a sign-out, the question before it):
    /// the words of its cover. Null when its live picture shows.
    /// </summary>
    [ObservableProperty] private string? _coverTitle;
    [ObservableProperty] private string? _coverDetail;
    /// <summary>Keeps the status text on one monitor when full screen spans all of them (set by the window).</summary>
    [ObservableProperty] private Thickness _statusMargin;
    [ObservableProperty] private DateTime? _connectedAt;
    /// <summary>"Group › Subgroup" of the connection, shown in the window title.</summary>
    [ObservableProperty] private string _groupPath = "";
    /// <summary>Connection quality 1 (poor) to 4 (excellent); 0 = unknown.</summary>
    [ObservableProperty] private int _quality;
    [ObservableProperty] private string _networkInfo = Loc.Get("NetworkMeasuring");
    /// <summary>Shut the Azure VM behind it down once this session's window is closed (chosen in its VM menu).</summary>
    [ObservableProperty] private bool _shutDownVmAfter;

    /// <summary>The frame around the remote desktop (letterbox) in the current theme's stage color.</summary>
    public void ApplyStage() => _frame.BackColor = Cover.Stage;

    public Connection Connection { get; }
    public WindowsFormsHost Host { get; }
    /// <summary>
    /// The name everywhere the session shows (its window, the taskbar, the switcher, the full-screen bar), with the
    /// account after it while another open session of the same machine signs in with another one.
    /// </summary>
    public string Title => AccountShown is { } account ? $"{Connection.Title} ({account})" : Connection.Title;
    /// <summary>The account it signs in with (DOMAIN\user, or user); empty when the server asks for one itself.</summary>
    public string Account => string.IsNullOrEmpty(_user) ? "" : string.IsNullOrEmpty(_domain) ? _user : $@"{_domain}\{_user}";
    /// <summary>Set by the session manager: the account to show after the name, or null.</summary>
    [ObservableProperty] private string? _accountShown;
    public string WindowTitle => GroupPath.Length == 0 ? Title : $"{Title} — {GroupPath}";
    /// <summary>Remote actions (sign-out, Ctrl+Alt+Del) make sense now.</summary>
    public bool IsReady => Phase == SessionPhase.Ready && ShowsDesktop && !IsBusy;
    public bool IsOpen => Phase is not (SessionPhase.Disconnected or SessionPhase.Closed);
    /// <summary>Its window was closed and the disconnect is under way: it is no longer the connection's session.</summary>
    public bool IsClosing => _intent == Intent.Close && !_disposed;
    /// <summary>One line for the open-sessions list: connected since, or what is going on.</summary>
    public string StatusLine => Phase == SessionPhase.Ready && ConnectedAt is { } at ? Loc.Format("ConnectedSince", at.ToString("t")) : FirstLine(Message);

    static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
    /// <summary>Full screen spans all monitors, which the server sees as separate monitors (mstsc /multimon).</summary>
    public bool Multimon => _options.Resolution == ResolutionMode.AllMonitors;
    /// <summary>Full screen on several monitors that are plugged in now (one chosen monitor is a full screen like any other).</summary>
    public bool SpansMonitors => Multimon && SpanScreens().Length > 1;
    /// <summary>
    /// The monitors a multi-monitor full screen uses: those chosen in the settings that are plugged in, else all of them
    /// (none chosen, or none of the chosen ones here: the office's monitors, at home). Chosen monitors that do not lie
    /// next to each other take those between them along (see <see cref="MonitorSpan"/>).
    /// </summary>
    WinForms.Screen[] SpanScreens()
    {
        var all = WinForms.Screen.AllScreens;
        var chosen = _options.Monitors is { Count: > 0 } names
            ? Enumerable.Range(0, all.Length).Where(i => names.Contains(all[i].DeviceName, StringComparer.OrdinalIgnoreCase)).ToArray() : [];
        if (chosen.Length == 0) return all;
        var used = MonitorSpan.Contiguous([.. all.Select(s => s.Bounds)], chosen);
        if (used.Length != chosen.Length && _saidApart != string.Join(",", used))
        {
            _saidApart = string.Join(",", used); // once per layout: this is asked for at every move of the window
            Log.Warn($"{Connection.Title}: the chosen monitors {string.Join(",", chosen)} do not lie next to each other, which Remote Desktop needs; using {_saidApart}");
        }
        return [.. used.Select(i => all[i])];
    }

    string? _saidApart;

    /// <summary>What the multi-monitor full screen covers (physical pixels): the monitors it uses and what lies between them.</summary>
    public System.Drawing.Rectangle SpanBounds => SpanScreens().Select(s => s.Bounds).Aggregate(System.Drawing.Rectangle.Union);

    /// <summary>The monitor for the bar and the messages of a multi-monitor full screen: <paramref name="wanted"/> when the session uses it, else its first one.</summary>
    public System.Drawing.Rectangle SpanMonitor(System.Drawing.Rectangle wanted)
    {
        var screens = SpanScreens();
        return screens.Any(s => s.Bounds == wanted) ? wanted : screens[0].Bounds;
    }

    /// <summary>Opens straight into full screen (the window enters it before it is first shown).</summary>
    public bool StartsFullScreen => _options.Resolution is ResolutionMode.Fullscreen or ResolutionMode.AllMonitors;
    bool Dynamic => _options.Resolution is ResolutionMode.FitWindow or ResolutionMode.Fullscreen;

    public event Action<RdpSession>? Closed;
    /// <summary>Who it signs in as; the password as stored (DPAPI), decrypted only by whoever uses it.</summary>
    public (string? User, string? Domain, byte[]? Password) SignIn => (_user, _domain, _password);
    /// <summary>Runs before each connect (starts the Azure VM behind it); false: it does not connect (the hook said why).</summary>
    public Func<RdpSession, CancellationToken, Task<bool>>? Prepare { get; set; }
    CancellationTokenSource? _preparing;
    /// <summary>The session (connection bar) wants full screen on/off; the window does the work.</summary>
    public event Action<bool>? FullScreenRequested;
    public event Action? MinimizeRequested;
    /// <summary>Ctrl+Alt+Left (-1) / Right (+1) pressed in the session: switch to the previous / next session.</summary>
    public event Action<RdpSession, int>? SwitchRequested;
    /// <summary>Show the remote screen before logon completed (the user asked, or logon is waiting for input).</summary>
    public event Action? RevealRequested;
    /// <summary>Reconnect with the credentials and settings the connection resolves to now (true: ask for credentials).</summary>
    public event Action<RdpSession, bool>? ReconnectRequested;

    public RdpSession(Connection connection, RdpOptions options, string? user, string? domain, byte[]? password)
    {
        Connection = connection;
        (_options, _user, _domain, _password) = (options, user, domain, password);
        Connection.PropertyChanged += OnConnectionChanged;

        _frame.Controls.Add(_rdp);
        _frame.SizeChanged += (_, _) => LayoutControl();
        // Drops over the control reach its host panel (the control registers no drop target of its own).
        _frame.AllowDrop = true;
        _frame.DragEnter += OnDragOver;
        _frame.DragOver += OnDragOver;
        _frame.DragDrop += (_, e) =>
        {
            if (IsReady && e.Data?.GetData(WinForms.DataFormats.FileDrop) is string[] { Length: > 0 } files)
                FilesDropped?.Invoke(files, new System.Drawing.Point(e.X, e.Y));
        };
        // Starts visible, as AxHost only instantiates the ActiveX for a visible window, but 1 px small: a new host window
        // is white until it first paints. Full size and hidden once the control exists (see Connect).
        Host = new WindowsFormsHost { Child = _frame, Width = 1, Height = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Host.Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            Host.Dispatcher.BeginInvoke(StartConnect, DispatcherPriority.Loaded);
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            FitDisplay();
        };
        _logonTimer.Tick += (_, _) => OnLogonTimer();

        var e = _rdp.Sink;
        e.Connected += () =>
        {
            Log.Info($"{Connection.Title}: connected in {(DateTime.Now - _logonStarted).TotalMilliseconds:0} ms");
            Phase = SessionPhase.LoggingOn;
            Message = Loc.Get("SigningIn");
            ConnectedAt ??= DateTime.Now;
            _logonStarted = DateTime.Now;
            (_newSignIn, _saidPreparing) = (Connection.LeftRunningAt == null, false);
            _logonTimer.Interval = TimeSpan.FromMilliseconds(Timings.OfferRemoteScreen);
            _logonTimer.Start();
        };
        e.LoginComplete += () =>
        {
            Log.Info($"{Connection.Title}: logged on in {(DateTime.Now - _logonStarted).TotalMilliseconds:0} ms");
            _logonTimer.Stop();
            _loggedOn = true;
            Connection.LeftRunningAt = DateTime.Now; // until signed out, also if this app ends unexpectedly
            CanShowRemoteScreen = false;
            Phase = SessionPhase.Ready;
            StartLatencyProbe();
        };
        // The control reconnects by itself after a network drop: say so instead of showing a frozen desktop.
        e.AutoReconnecting += (attempt, max) =>
        {
            Phase = SessionPhase.Reconnecting;
            Message = Loc.Format("AutoReconnecting", attempt, max);
        };
        e.AutoReconnected += () =>
        {
            Log.Info($"{Connection.Title}: auto-reconnected");
            Phase = SessionPhase.Ready;
        };
        e.NetworkStatusChanged += (quality, bandwidth, rtt) =>
        {
            // Simplification: before (or without) a measurement the control reports exactly 512 kb/s and 400 ms, which would
            // show a "poor" link; the indicator only appears for real measurements. Revisit if a client version changes them.
            if (bandwidth == 512 && rtt == 400) return;
            if (_serverQuality == null && ConnectedAt is { } at) Log.Info($"{Connection.Title}: server's network measurement {(DateTime.Now - at).TotalSeconds:0.0} s after connecting: {rtt} ms, {bandwidth} kb/s");
            _serverQuality = Math.Clamp(quality, 0, 4);
            if (bandwidth > 0) _bandwidth = bandwidth; // the server often reports -1: bandwidth not measured
            _rtt = rtt;
            UpdateNetworkInfo();
        };
        e.DesktopSizeChanged += (width, height) =>
        {
            if (_desktop != new System.Drawing.Size(width, height)) Log.Info($"{Connection.Title}: remote desktop {width}x{height}");
            _desktop = new System.Drawing.Size(width, height);
            if (!Dynamic) LayoutControl(); // a fixed or all-monitors desktop is fitted to what the server granted
            if (_desktop == _requested.Size) _display?.TrySetResult();
        };
        e.Disconnected += OnDisconnected;
        e.FatalError += code =>
        {
            Log.Warn($"{Connection.Title}: fatal error {code}");
            Fail(Loc.Format("FatalError", code));
        };
        e.RequestGoFullScreen += () => FullScreenRequested?.Invoke(true);
        e.RequestLeaveFullScreen += () => FullScreenRequested?.Invoke(false);
        e.RequestMinimize += () => MinimizeRequested?.Invoke();
        // The control's own "release focus" keys: it takes them from the keyboard itself (also physical keys that
        // a low-level hook of ours never saw), so they are used to switch sessions.
        e.FocusReleased += direction =>
        {
            Log.Info($"{Connection.Title}: Ctrl+Alt+{(direction < 0 ? "Left" : "Right")} in the session");
            SwitchRequested?.Invoke(this, direction < 0 ? -1 : 1);
        };
    }

    /// <summary>The connection's host or port changed (edited, an Azure sync): the session manager regroups the labels.</summary>
    public event Action? AddressChanged;

    void OnDragOver(object? sender, WinForms.DragEventArgs e) =>
        e.Effect = IsReady && e.Data?.GetDataPresent(WinForms.DataFormats.FileDrop) == true ? WinForms.DragDropEffects.Copy : WinForms.DragDropEffects.None;

    /// <summary>Files (or folders) dragged from this PC and dropped on the remote screen.</summary>
    public event Action<string[], System.Drawing.Point>? FilesDropped; // and where, on the screen

    /// <summary>Whether the session shares this PC's clipboard, the way dropped files go over.</summary>
    public bool SharesClipboard => _options.RedirectClipboard;

    void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Connection.Host) or nameof(Connection.Port)) AddressChanged?.Invoke();
        if (e.PropertyName != nameof(Connection.Title)) return;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnGroupPathChanged(string value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnAccountShownChanged(string? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WindowTitle));
    }
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnConnectedAtChanged(DateTime? value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReady));

    partial void OnPhaseChanged(SessionPhase value)
    {
        if (value != SessionPhase.LoggingOn) CanShowRemoteScreen = false;
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsOpen));
        // Leaving Ready, the window hides the desktop behind a cover (ShowsDesktop = false) once it has faded in.
    }

    // Hidden (not Collapsed) keeps the layout size; WPF overlays can't draw over the HWND (airspace).
    partial void OnShowsDesktopChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReady));
        if (_rdp.GetOcx() != null) Host.Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    bool _newSignIn, _saidPreparing;

    void OnLogonTimer()
    {
        if (Phase != SessionPhase.LoggingOn)
        {
            _logonTimer.Stop();
            return;
        }
        var (offer, reveal) = _newSignIn ? (Timings.OfferRemoteScreenNew, Timings.RevealLogonScreenNew) : (Timings.OfferRemoteScreen, Timings.RevealLogonScreen);
        // A new sign-in first only says what goes on: "longer than usual, show the server's screen" came up at every
        // first sign-in after the machine started, where nothing is wrong and there is nothing to do.
        if (_newSignIn && !_saidPreparing)
        {
            _saidPreparing = true;
            Message = Loc.Get("SigningInNew");
            _logonTimer.Interval = TimeSpan.FromMilliseconds(offer - Timings.OfferRemoteScreen);
            return;
        }
        if (!CanShowRemoteScreen)
        {
            CanShowRemoteScreen = true;
            Message = Loc.Get("SigningInSlow");
            _logonTimer.Interval = TimeSpan.FromMilliseconds(reveal - offer);
            return;
        }
        _logonTimer.Stop();
        Log.Info($"{Connection.Title}: no logon reported after {reveal} ms, showing the remote screen");
        RevealRequested?.Invoke();
    }

    [RelayCommand]
    void ShowRemoteScreen() => RevealRequested?.Invoke();

    /// <summary>Credentials and settings for the next connect (reconnect with what the connection uses now).</summary>
    public void Configure(RdpOptions options, string? user, string? domain, byte[]? password) =>
        (_options, _user, _domain, _password) = (options, user, domain, password);

    /// <summary>Connects after <see cref="Prepare"/> (if any) let it.</summary>
    async void StartConnect()
    {
        if (Prepare == null)
        {
            Connect();
            return;
        }
        _preparing?.Cancel();
        var preparing = _preparing = new CancellationTokenSource();
        _rdp.CreateControl(); // while the host is visible (see the constructor)
        IsError = false;
        Phase = SessionPhase.Connecting;
        Message = Loc.Format("ConnectingTo", Connection.Address);
        bool go;
        try { go = await Prepare(this, preparing.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Error(ex, $"{Connection.Title}: preparing the connect failed");
            if (!_disposed) Fail(ex.Message);
            return;
        }
        if (go && !preparing.IsCancellationRequested && !_disposed && _intent != Intent.Close) Connect();
    }

    /// <summary>Stops before connecting, not as an error (the user said no, the machine did not start).</summary>
    public void Halt(string message)
    {
        Phase = SessionPhase.Disconnected;
        IsError = false;
        Message = message;
    }

    public void Connect()
    {
        try
        {
            _rdp.CreateControl();
            var c = _rdp.Ocx;
            if (Host.Width == 1)
            {
                Host.Visibility = Visibility.Hidden;
                Host.Width = Host.Height = double.NaN;
                (Host.HorizontalAlignment, Host.VerticalAlignment) = (HorizontalAlignment.Stretch, VerticalAlignment.Stretch);
                Host.UpdateLayout(); // the initial desktop size below is the control's real size
            }
            _intent = Intent.None;
            _loggedOn = false;
            (_desktop, _display) = (default, null);
            CanShowRemoteScreen = IsError = false;
            Details = "";
            ConnectedAt = null;
            Phase = SessionPhase.Connecting;
            Message = Loc.Format("ConnectingTo", Connection.Address);
            _logonStarted = DateTime.Now;
            Log.Info($"{Connection.Title}: connecting to {Connection.Address} ({_options.Resolution})");

            c.Server = Connection.Host;
            c.UserName = _user ?? "";
            c.Domain = _domain ?? "";
            c.ColorDepth = _options.ColorDepth;
            c.ConnectingText = "";
            c.DisconnectedText = "";
            c.FullScreenTitle = Title;

            LayoutControl();
            var size = _options.Resolution switch
            {
                ResolutionMode.Fixed => new System.Drawing.Size(_options.Width, _options.Height),
                ResolutionMode.AllMonitors => SpanBounds.Size,
                _ => _rdp.Size,
            };
            c.DesktopWidth = Even(size.Width);
            c.DesktopHeight = Even(size.Height);
            _requested = (new System.Drawing.Size(Even(size.Width), Even(size.Height)), DesktopScale);

            var a = c.AdvancedSettings9;
            a.RDPPort = Connection.Port;
            if (_password != null) a.ClearTextPassword = Store.Unprotect(_password);
            else ResetPassword(); // a reconnect without one must not send the previous connect's password
            // Scaled whenever the control is not exactly the desktop: a fixed or all-monitors desktop always (fitted to
            // the window), a dynamic one while the server catches up with a new window size (then it is 1:1 again).
            // Unscaled, a stale frame would show cropped and the rest of the control empty.
            a.SmartSizing = true;
            a.RedirectClipboard = _options.RedirectClipboard;
            a.RedirectDrives = _options.RedirectDrives;
            a.RedirectPrinters = _options.RedirectPrinters;
            a.RedirectSmartCards = _options.RedirectSmartCards;
            a.ConnectToAdministerServer = _options.AdminSession;
            a.EnableCredSspSupport = true;
            // 2 = warn on server identity problems, like mstsc; 0 = connect without checking (the user's choice in Settings).
            a.AuthenticationLevel = App.Store.Data.Settings.SkipCertificateWarning ? 0u : 2u;
            a.EnableAutoReconnect = true;
            a.MaxReconnectAttempts = 20;
            a.GrabFocusOnConnect = true;
            a.BitmapPersistence = 1;
            // Like mstsc's "detect connection quality automatically": the session adapts to the link, and the control
            // reports measured quality (without it only placeholder values arrive, once).
            try
            {
                a.NetworkConnectionType = 7; // CONNECTION_TYPE_AUTODETECT
                a.BandwidthDetection = true;
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { } // older client
            // Full screen is done by the window (any monitor, draggable FullScreenBar). The control's own full-screen
            // mode is never switched on: it would draw the mstsc connection bar under ours.
            a.ContainerHandledFullScreen = 1;
            a.DisplayConnectionBar = false;

            var s = c.SecuredSettings2;
            s.AudioRedirectionMode = (int)_options.Audio;
            // 2 ("full screen only") would need the control's own full-screen mode, and it cannot change while connected.
            s.KeyboardHookMode = _options.RemoteShortcuts ? 1 : 0;

            var t = c.TransportSettings4;
            if (!string.IsNullOrWhiteSpace(_options.Gateway))
            {
                t.GatewayHostname = _options.Gateway;
                t.GatewayUsageMethod = 1;        // always use gateway
                t.GatewayProfileUsageMethod = 1; // explicit settings
                t.GatewayCredSharing = 1;        // reuse session credentials
            }
            else if ((int)t.GatewayUsageMethod == 1)
            {
                t.GatewayUsageMethod = 0; // a reconnect after the gateway was removed from the settings
            }

            // The mstsc connection bar would flash up while connecting (UseMultimon makes the control act full screen).
            PutNonScriptable5(DisableConnectionBarSlot, true);
            PutNonScriptable5(UseMultimonSlot, Multimon);
            SelectMonitors();

            // Initial scale (UpdateSessionDisplaySettings only works once connected). Same properties mRemoteNG sets.
            try
            {
                var ext = (IMsRdpExtendedSettings)(object)c;
                object desktop = _requested.Scale, device = DeviceScale(_requested.Scale);
                ext.PutProperty("DesktopScaleFactor", ref desktop);
                ext.PutProperty("DeviceScaleFactor", ref device);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { } // old client: 100 %

            c.Connect();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"{Connection.Title}: connect failed");
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// Some of the monitors only: the control's "SelectedMonitors" (what mstsc's selectedmonitors:s:0,2 sets), the
    /// monitors by their place in the system's list. Empty for all of them, also to undo an earlier connect's choice.
    /// </summary>
    void SelectMonitors()
    {
        var all = WinForms.Screen.AllScreens;
        var used = Multimon ? SpanScreens() : all;
        var ids = used.Length == all.Length ? "" : string.Join(",", used.Select(s => Array.IndexOf(all, s)));
        try
        {
            object value = ids;
            ((IMsRdpExtendedSettings)(object)_rdp.Ocx).PutProperty("SelectedMonitors", ref value);
            if (ids.Length > 0) Log.Info($"{Connection.Title}: monitors {ids} of {all.Length} ({string.Join(", ", used.Select(s => s.Bounds.Size))})");
        }
        // An older client without it: the session then spans every monitor, in a window over the chosen ones (scaled).
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            if (ids.Length > 0) Log.Warn($"{Connection.Title}: the RDP client does not take a choice of monitors ({ex.Message}); using all {all.Length}");
        }
    }

    void Fail(string message)
    {
        _logonTimer.Stop();
        Phase = SessionPhase.Disconnected;
        IsError = true;
        Message = message;
    }

    /// <summary>Reconnects with the credentials and settings the connection resolves to now (via the session manager).</summary>
    [RelayCommand]
    void Reconnect() => ReconnectRequested?.Invoke(this, false);

    [RelayCommand]
    void ConnectAs() => ReconnectRequested?.Invoke(this, true);

    /// <summary>Connects again (after <see cref="Configure"/>): at once when disconnected, else once the old connection is down.</summary>
    public void Restart()
    {
        if (_disposed || _intent == Intent.Close) return;
        if (!IsConnected)
        {
            StartConnect();
            return;
        }
        _intent = Intent.Reconnect;
        try { _rdp.Ocx.Disconnect(); }
        catch (COMException) { Connect(); }
    }

    bool IsConnected
    {
        get
        {
            try { return _rdp.GetOcx() != null && (int)_rdp.Ocx.Connected != 0; }
            catch (COMException) { return false; }
        }
    }

    void OnDisconnected(int reason)
    {
        var intent = _intent;
        _intent = Intent.None;
        _logonTimer.Stop();
        _latency?.Cancel();
        Log.Info($"{Connection.Title}: disconnected, reason {reason}, extended {ExtendedReason()}, intent {intent}");
        // A logged-on session stays on the server unless it was signed out (by anyone: this app, the Start menu, an admin).
        if (_loggedOn) Connection.LeftRunningAt = ExtendedReason() is 2 /* APIInitiatedLogoff */ or 12 /* LogoffByUser */ ? null : DateTime.Now;
        switch (intent)
        {
            case Intent.Close:
            // Like mstsc, the window goes away after signing out; any other disconnect meanwhile is shown as one.
            case Intent.SignOut when ExtendedReason() is 2 /* APIInitiatedLogoff */ or 12 /* LogoffByUser */:
            // Signed out inside the session (Start menu): the user ended it themselves, nothing to say or reconnect to.
            // Signed out by someone else (2, an administrator) without this app asking: that is shown.
            case Intent.None when _loggedOn && ExtendedReason() == 12 /* LogoffByUser */:
                Dispose();
                return;
            case Intent.Reconnect:
                Connect();
                return;
        }
        Phase = SessionPhase.Disconnected;
        // 1-3 = local/remote user or server ended the session normally (logoff, disconnect).
        IsError = reason > 3;
        (Message, Details) = reason <= 3 ? (Loc.Get("SessionEnded"), "") : Explain(reason, Describe(reason));
    }

    /// <summary>
    /// What to say about a failed connection: for the usual reasons one line of Sessionwright's own, in its language, saying what
    /// to check (Windows' text is long, in the system's language, and went whole into the session's tile); Windows' text
    /// stays as the details. Any other reason: Windows' first line, the rest as the details.
    /// </summary>
    internal static (string Message, string Details) Explain(int reason, string system)
    {
        var own = reason switch
        {
            260 or 520 or 1288 or 1540 => "DisconnectNoHost",   // the name was not resolved
            264 or 516 or 1796 => "DisconnectNoAnswer",        // timed out, the connect failed
            772 or 1028 or 2308 => "DisconnectDropped",        // send / receive failed, the socket closed
            2055 or 2567 => "DisconnectLogon",                 // logon failed, no such user
            _ => null,
        };
        if (own != null) return (Loc.Get(own), system.Trim());
        var first = FirstLine(system);
        return (first, system.Trim() == first ? "" : system.Trim());
    }

    int ExtendedReason()
    {
        try { return (int)_rdp.Ocx.ExtendedDisconnectReason; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return -1; }
    }

    string Describe(int reason)
    {
        try
        {
            string text = _rdp.Ocx.GetErrorDescription((uint)reason, (uint)ExtendedReason());
            return string.IsNullOrWhiteSpace(text) ? Loc.Format("DisconnectCode", reason) : text;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return Loc.Format("DisconnectCode", reason);
        }
    }

    // ---------- Closing ----------

    [RelayCommand]
    public void Close()
    {
        if (_disposed || _intent == Intent.Close) return;
        _intent = Intent.Close;
        _logonTimer.Stop();
        if (IsConnected)
        {
            try
            {
                _rdp.Ocx.Disconnect();
                // Dispose happens in OnDisconnected; force it if the control never reports back.
                var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Timings.CloseGuard) };
                guard.Tick += (_, _) =>
                {
                    guard.Stop();
                    Dispose();
                };
                guard.Start();
                return;
            }
            catch (COMException) { }
        }
        Dispose();
    }

    void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connection.PropertyChanged -= OnConnectionChanged;
        _resizeDebounce.Stop();
        _logonTimer.Stop();
        _latency?.Cancel();
        _preparing?.Cancel();
        Phase = SessionPhase.Closed;
        Closed?.Invoke(this);
        Host.Child = null;
        _probe?.Dispose();
        _rdp.Dispose();
        _frame.Dispose();
        Host.Dispose();
    }
}
