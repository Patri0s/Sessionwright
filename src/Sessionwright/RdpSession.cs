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
    public const int CloseGuard = 5000;
    /// <summary>How often the latency to the server is measured while a session is open.</summary>
    public const int LatencyProbe = 30000;
    /// <summary>Azure VM start: how often its state is asked, how long it may take, then how long Windows may take to boot.</summary>
    public const int VmPoll = 5000;
    /// <summary>How often the server is asked while a command run before connecting (a VPN) comes up.</summary>
    public const int BeforeConnectPoll = 2000;
    public static readonly TimeSpan VmStart = TimeSpan.FromMinutes(10), VmBoot = TimeSpan.FromMinutes(5);
}

public sealed partial class RdpSession : ObservableObject
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
    public string Title => Connection.Title;
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
    /// <summary>
    /// The monitors a multi-monitor full screen uses: those chosen in the settings that are plugged in, else all of them
    /// (none chosen, or none of the chosen ones here: the office's monitors, at home).
    /// </summary>
    WinForms.Screen[] SpanScreens()
    {
        var all = WinForms.Screen.AllScreens;
        var chosen = _options.Monitors is { Count: > 0 } names
            ? all.Where(s => names.Contains(s.DeviceName, StringComparer.OrdinalIgnoreCase)).ToArray() : [];
        return chosen.Length > 0 ? chosen : all;
    }

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
            Log.Info($"{Title}: connected in {(DateTime.Now - _logonStarted).TotalMilliseconds:0} ms");
            Phase = SessionPhase.LoggingOn;
            Message = Loc.Get("SigningIn");
            ConnectedAt ??= DateTime.Now;
            _logonStarted = DateTime.Now;
            _logonTimer.Interval = TimeSpan.FromMilliseconds(Timings.OfferRemoteScreen);
            _logonTimer.Start();
        };
        e.LoginComplete += () =>
        {
            Log.Info($"{Title}: logged on in {(DateTime.Now - _logonStarted).TotalMilliseconds:0} ms");
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
            Log.Info($"{Title}: auto-reconnected");
            Phase = SessionPhase.Ready;
        };
        e.NetworkStatusChanged += (quality, bandwidth, rtt) =>
        {
            // Simplification: before (or without) a measurement the control reports exactly 512 kb/s and 400 ms, which would
            // show a "poor" link; the indicator only appears for real measurements. Revisit if a client version changes them.
            if (bandwidth == 512 && rtt == 400) return;
            if (_serverQuality == null && ConnectedAt is { } at) Log.Info($"{Title}: server's network measurement {(DateTime.Now - at).TotalSeconds:0.0} s after connecting: {rtt} ms, {bandwidth} kb/s");
            _serverQuality = Math.Clamp(quality, 0, 4);
            if (bandwidth > 0) _bandwidth = bandwidth; // the server often reports -1: bandwidth not measured
            _rtt = rtt;
            UpdateNetworkInfo();
        };
        e.DesktopSizeChanged += (width, height) =>
        {
            if (_desktop != new System.Drawing.Size(width, height)) Log.Info($"{Title}: remote desktop {width}x{height}");
            _desktop = new System.Drawing.Size(width, height);
            if (!Dynamic) LayoutControl(); // a fixed or all-monitors desktop is fitted to what the server granted
            if (_desktop == _requested.Size) _display?.TrySetResult();
        };
        e.Disconnected += OnDisconnected;
        e.FatalError += code =>
        {
            Log.Warn($"{Title}: fatal error {code}");
            Fail(Loc.Format("FatalError", code));
        };
        e.RequestGoFullScreen += () => FullScreenRequested?.Invoke(true);
        e.RequestLeaveFullScreen += () => FullScreenRequested?.Invoke(false);
        e.RequestMinimize += () => MinimizeRequested?.Invoke();
        // The control's own "release focus" keys: it takes them from the keyboard itself (also physical keys that
        // a low-level hook of ours never saw), so they are used to switch sessions.
        e.FocusReleased += direction =>
        {
            Log.Info($"{Title}: Ctrl+Alt+{(direction < 0 ? "Left" : "Right")} in the session");
            SwitchRequested?.Invoke(this, direction < 0 ? -1 : 1);
        };
    }

    void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Connection.Title)) return;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnGroupPathChanged(string value) => OnPropertyChanged(nameof(WindowTitle));
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

    void OnLogonTimer()
    {
        if (Phase != SessionPhase.LoggingOn)
        {
            _logonTimer.Stop();
            return;
        }
        if (!CanShowRemoteScreen)
        {
            CanShowRemoteScreen = true;
            Message = Loc.Get("SigningInSlow");
            _logonTimer.Interval = TimeSpan.FromMilliseconds(Timings.RevealLogonScreen - Timings.OfferRemoteScreen);
            return;
        }
        _logonTimer.Stop();
        Log.Info($"{Title}: no logon reported after {Timings.RevealLogonScreen} ms, showing the remote screen");
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
            Log.Error(ex, $"{Title}: preparing the connect failed");
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
            Log.Info($"{Title}: connecting to {Connection.Address} ({_options.Resolution})");

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
            a.AuthenticationLevel = 2; // warn on server identity problems, like mstsc
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
            Log.Error(ex, $"{Title}: connect failed");
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
            if (ids.Length > 0) Log.Info($"{Title}: monitors {ids} of {all.Length} ({string.Join(", ", used.Select(s => s.Bounds.Size))})");
        }
        // An older client without it: the session then spans every monitor, in a window over the chosen ones (scaled).
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            if (ids.Length > 0) Log.Warn($"{Title}: the RDP client does not take a choice of monitors ({ex.Message}); using all {all.Length}");
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
        Log.Info($"{Title}: disconnected, reason {reason}, extended {ExtendedReason()}, intent {intent}");
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

    // ---------- Display size ----------

    /// <summary>
    /// Keeps the control at its current size while the window changes bounds and chrome (each resize of the ActiveX
    /// costs hundreds of ms on the UI thread); <see cref="ResumeLayout"/> applies the final size once.
    /// </summary>
    public void SuspendLayout()
    {
        _frozen = true;
        _resizeDebounce.Stop();
        _blackBefore = ShowsDesktop ? BlackFraction() : -1; // what "repainted" looks like for this desktop (see WaitForRepaintAsync)
    }

    double _blackBefore = -1;

    /// <summary>Lays the control out at the final size and sends it to the server right away.</summary>
    public void ResumeLayout()
    {
        _frozen = false;
        LayoutControl();
        _resizeDebounce.Stop();
        FitDisplay();
    }

    void LayoutControl()
    {
        if (_frozen || _disposed) return;
        var frame = _frame.ClientSize;
        if (frame.Width < 64 || frame.Height < 64) return; // minimized or not laid out yet: keep the last size
        System.Drawing.Size size;
        if (Dynamic)
        {
            size = new System.Drawing.Size(Even(frame.Width), Even(frame.Height)); // the server wants even sizes
        }
        else
        {
            // Fixed or all monitors: the desktop scaled down to fit (never up), keeping its aspect ratio.
            var desktop = !_desktop.IsEmpty ? _desktop
                : _options.Resolution == ResolutionMode.Fixed ? new System.Drawing.Size(_options.Width, _options.Height)
                : SpanBounds.Size;
            var fit = Math.Min(1, Math.Min((double)frame.Width / desktop.Width, (double)frame.Height / desktop.Height));
            size = new System.Drawing.Size((int)Math.Floor(desktop.Width * fit), (int)Math.Floor(desktop.Height * fit));
        }
        var bounds = new System.Drawing.Rectangle((frame.Width - size.Width) / 2, (frame.Height - size.Height) / 2, size.Width, size.Height);
        if (_rdp.Bounds == bounds) return;
        _rdp.Bounds = bounds;
        // WinForms sizes are physical, so this also covers a move to a monitor with another scale.
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    /// <summary>The control's size or scale differs from what the server has: <see cref="FitDisplay"/> would send an update.</summary>
    public bool DisplayOutdated => !_disposed && !_frozen && _loggedOn && Dynamic && Phase == SessionPhase.Ready
        && _rdp.Width >= 200 && _rdp.Height >= 200 && _requested != (_rdp.Size, DesktopScale);

    /// <summary>
    /// Sends the control's size (and the local scale) to the server once logged on (it ignores updates before).
    /// Returns whether the server still has to apply it (see <see cref="WaitForDisplayAsync"/>).
    /// </summary>
    public bool FitDisplay()
    {
        if (_disposed || _frozen || !_loggedOn || !Dynamic || Phase != SessionPhase.Ready) return false;
        var size = _rdp.Size;
        if (size.Width < 200 || size.Height < 200) return false;
        var scale = DesktopScale;
        if (_requested == (size, scale)) return _display is { Task.IsCompleted: false };
        try
        {
            // Dynamic resolution (RDP 8.1+). Older servers throw; the session then keeps its size (scaled).
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height, 0u, scale, DeviceScale(scale));
            Log.Info($"{Title}: display {size.Width}x{size.Height} at {scale} %");
            _requested = (size, scale);
            // A scale-only change is not reported back: nothing to wait for.
            _display = size == _desktop ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _display != null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Warn($"{Title}: display update refused: {ex.Message}");
            return false;
        }
    }

    /// <summary>Completes once the server shows the requested size (or after a timeout) and has repainted it.</summary>
    public async Task WaitForDisplayAsync()
    {
        if (_display is not { } pending) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = await Task.WhenAny(pending.Task, Task.Delay(Timings.DisplayTimeout)) == pending.Task;
        if (!done)
        {
            Log.Info($"{Title}: server did not confirm the display update within {Timings.DisplayTimeout} ms");
            return;
        }
        var applied = clock.ElapsedMilliseconds;
        if (ShowsDesktop) await WaitForRepaintAsync(); // a hidden control is waited for once it is shown
        Log.Info($"{Title}: server applied the display update in {applied} ms, repainted in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// After a resolution change the control clears its surface to black (all of it, or the part beyond the old size,
    /// or a few tiles of a scaled stale frame) and the server repaints it over the next 0.1-1 s, partly at first. No
    /// event says when it is done, so the control's pixels are sampled (PrintWindow also reads them under a cover):
    /// done once no more points are black than before the change (a desktop without wallpaper is black), for two
    /// samples in a row.
    /// </summary>
    async Task WaitForRepaintAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var allowed = Math.Max(0, _blackBefore) + 0.01;
        var sawCleared = false;
        var clean = 0;
        while (clock.ElapsedMilliseconds < Timings.RepaintTimeout && !_disposed && Phase == SessionPhase.Ready)
        {
            await Task.Delay(40);
            var black = BlackFraction();
            if (black < 0)
            {
                await Task.Delay(Timings.DisplaySettle); // cannot read the pixels: a fixed guess
                return;
            }
            if (black > allowed)
            {
                sawCleared = true;
                clean = 0;
                continue;
            }
            // Right after the event the old frame may still be presented: wait to see it cleared (or a while).
            if (!sawCleared && clock.ElapsedMilliseconds < 400) continue;
            if (++clean < 2) continue;
            await Task.Delay(150); // the last tiles of a partial repaint can lag the samples
            return;
        }
    }

    /// <summary>Shown again after being hidden: the control takes the server's frames again, black until they arrive.</summary>
    public async Task WaitForPaintAsync(int timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = -1.0;
        while (clock.ElapsedMilliseconds < timeout && !_disposed)
        {
            await Task.Delay(40);
            var black = BlackFraction();
            if (black < 0)
            {
                await Task.Delay(100);
                return;
            }
            // Not all black any more (a desktop without wallpaper still has icons and a taskbar), and settled.
            if (black < 0.97 && Math.Abs(black - last) < 0.01) return;
            last = black;
        }
    }

    System.Drawing.Bitmap? _probe;

    /// <summary>Share of pure black points on a grid over the control, or -1 if the window cannot be read.</summary>
    double BlackFraction() => Sample() is { } points ? points.Count(p => (p & 0xFF) <= 3 && ((p >> 8) & 0xFF) <= 3 && ((p >> 16) & 0xFF) <= 3) / (double)points.Length : -1;

    /// <summary>Share of grid points whose color changed clearly between two samples.</summary>
    static double Changed(int[] before, int[] after) =>
        before.Zip(after).Count(p => Math.Abs((p.First & 0xFF) - (p.Second & 0xFF)) + Math.Abs(((p.First >> 8) & 0xFF) - ((p.Second >> 8) & 0xFF))
                                     + Math.Abs(((p.First >> 16) & 0xFF) - ((p.Second >> 16) & 0xFF)) > 60) / (double)before.Length;

    /// <summary>
    /// The control's colors on a 32 x 18 grid, read through PrintWindow (which also works under a cover), or null if the
    /// window cannot be read.
    /// </summary>
    int[]? Sample()
    {
        if (_disposed || Window.GetWindow(Host) is not { } window || !window.IsVisible) return null;
        var hwnd = Native.Handle(window);
        var bounds = Native.Bounds(hwnd);
        var control = _rdp.RectangleToScreen(_rdp.ClientRectangle);
        control.Offset(-bounds.X, -bounds.Y);
        control.Intersect(new System.Drawing.Rectangle(System.Drawing.Point.Empty, bounds.Size));
        if (control.Width < 64 || control.Height < 64) return null;
        if (_probe == null || _probe.Size != bounds.Size)
        {
            _probe?.Dispose();
            _probe = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        }
        using (var g = System.Drawing.Graphics.FromImage(_probe))
        {
            var dc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
            }
            finally { g.ReleaseHdc(dc); }
        }
        var data = _probe.LockBits(new System.Drawing.Rectangle(System.Drawing.Point.Empty, _probe.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, _probe.PixelFormat);
        try
        {
            const int columns = 32, rows = 18;
            var points = new int[columns * rows];
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = control.X + 4 + (control.Width - 8) * column / (columns - 1);
                var y = control.Y + 4 + (control.Height - 8) * row / (rows - 1);
                points[row * columns + column] = Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4) & 0xFFFFFF;
            }
            return points;
        }
        finally { _probe.UnlockBits(data); }
    }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>Local display scale (100 = 100 %); the remote desktop renders at it, like mstsc, so text keeps its size.</summary>
    uint DesktopScale => (uint)Math.Clamp(Math.Round(VisualTreeHelper.GetDpi(Host).DpiScaleX * 100), 100, 500);

    // RDP only accepts these device scale factors.
    static uint DeviceScale(uint desktopScale) => desktopScale < 125 ? 100u : desktopScale < 165 ? 140u : 180u;

    static int Even(int v) => Math.Clamp(v, 200, 8192) & ~1;

    // IMsTscNonScriptable::ResetPassword, slot 12 (oVft 96 in the mstscax.dll type library). The control keeps the
    // ClearTextPassword of its previous connect otherwise.
    static readonly Guid NonScriptable = new("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Call(IntPtr self);

    // IMsRdpClientNonScriptable::SendKeys, slot 14 (oVft 112 in the mstscax.dll type library); IUnknown-only like the other
    // non-scriptable interfaces. At most 20 keys per call.
    static readonly Guid ClientNonScriptable = new("2f079c4c-87b2-4afd-97ab-20cdb43038ae");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SendKeysCall(IntPtr self, int count, short[] keyUp, int[] keyData);

    /// <summary>
    /// Keys typed while the user may be elsewhere (a sign-out goes on in the background): the control takes keys only
    /// with the keyboard focus, which brings its window forward, so a window the user went to meanwhile (another
    /// session) gets the foreground straight back. Before, every step took it back from them for good.
    /// </summary>
    bool SendKeysFocused(RemoteInput.Key[] keys)
    {
        var elsewhere = GetForegroundWindow();
        var mine = _rdp.IsHandleCreated ? GetAncestor(_rdp.Handle, 2 /* GA_ROOT */) : IntPtr.Zero;
        FocusRemote();
        var sent = SendKeys(keys);
        if (elsewhere != IntPtr.Zero && elsewhere != mine && GetWindow(elsewhere, 4 /* GW_OWNER */) != mine && IsWindowVisible(elsewhere))
            SetForegroundWindow(elsewhere);
        return sent;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>Sends keys into the session (see <see cref="RemoteInput"/>). False when the control refused them.</summary>
    bool SendKeys(RemoteInput.Key[] keys)
    {
        if (_disposed || _rdp.GetOcx() == null) return false;
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Ocx);
        try
        {
            var iid = ClientNonScriptable;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var itf));
            try
            {
                var send = Marshal.GetDelegateForFunctionPointer<SendKeysCall>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), 14 * IntPtr.Size));
                foreach (var chunk in keys.Chunk(20))
                {
                    var hr = send(itf, chunk.Length, [.. chunk.Select(k => k.Up ? (short)-1 /* VARIANT_TRUE */ : (short)0)], [.. chunk.Select(k => k.Code)]);
                    if (hr == 0) continue;
                    Log.Warn($"{Title}: the session refused the keys (0x{hr:X8}; keyboard focus elsewhere?)");
                    return false;
                }
                return true;
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unknown); }
    }

    void ResetPassword()
    {
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Ocx);
        try
        {
            var iid = NonScriptable;
            if (Marshal.QueryInterface(unknown, in iid, out var itf) != 0) return;
            try
            {
                var reset = Marshal.GetDelegateForFunctionPointer<Call>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), 12 * IntPtr.Size));
                if (reset(itf) != 0) Log.Warn($"{Title}: resetting the stored password failed");
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unknown); }
    }

    // IMsRdpClientNonScriptable5 is IUnknown-only (not reachable through `dynamic`) and deep in an inheritance chain, so
    // its setters are called by vtable slot (offset / 8, verified against the mstscax.dll type library).
    static readonly Guid NonScriptable5 = new("4f6996d5-d7b1-412c-b0ff-063718566907");
    const int UseMultimonSlot = 53, DisableConnectionBarSlot = 58; // put_UseMultimon 424, put_DisableConnectionBar 464
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PutBool(IntPtr self, short value);

    void PutNonScriptable5(int slot, bool on)
    {
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Ocx);
        try
        {
            var iid = NonScriptable5;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var itf));
            try
            {
                var put = Marshal.GetDelegateForFunctionPointer<PutBool>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), slot * IntPtr.Size));
                Marshal.ThrowExceptionForHR(put(itf, (short)(on ? -1 : 0))); // VARIANT_TRUE
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unknown); }
    }

    // ---------- Focus and input ----------

    public void FocusRemote()
    {
        if (_disposed || _rdp.GetOcx() == null) return; // not created yet: the window enters full screen before it is shown
        // WPF focus first (else WPF takes focus back from the hosted HWND), then the ActiveX's input window itself:
        // focusing only the AxHost wrapper leaves the control without keyboard focus.
        Host.Focus();
        _rdp.Focus();
        if (RemoteInput.InputWindow(_rdp.Handle) is var input && input != IntPtr.Zero) RemoteInput.Focus(input);
    }

    /// <summary>
    /// Signs out by typing Win+R, "logoff", Enter. Returns whether every key was delivered; the window then waits for the
    /// session to end, and calls <see cref="CancelSignOut"/> if it does not.
    /// </summary>
    public async Task<bool> TypeSignOutAsync()
    {
        try
        {
            return await TypeLogoffAsync();
        }
        catch
        {
            CancelSignOut();
            throw;
        }
    }

    async Task<bool> TypeLogoffAsync()
    {
        if (!await OpenRunAsync() || RemoteInput.Text("logoff") is not { } logoff || !SendKeysFocused(logoff)) return false;
        // Run executes "logoff" on Enter at once; Start search (see OpenRunAsync) only once its results are in, which took
        // over 1.5 s on a freshly logged-on DEV-12: Enter once the screen stopped changing.
        await Task.Delay(500);
        var atEnter = await WaitForStillAsync(700, 5000);
        if (atEnter == null) await Task.Delay(1000); // cannot see the session: a fixed wait
        if (_disposed || Phase != SessionPhase.Ready) return false;
        _intent = Intent.SignOut; // only now: a disconnect before the command was sent is no sign-out
        if (!SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter)))
        {
            CancelSignOut();
            return false;
        }
        // Enter before Start search had its results does nothing: one more, but only if the screen did not change at
        // all (an extra Enter could confirm a "save changes?" prompt of a closing app).
        for (var waited = 0; waited < 4000 && !_disposed; waited += 100) await Task.Delay(100);
        if (!_disposed && _intent == Intent.SignOut && atEnter != null && Sample() is { } now && Changed(atEnter, now) < 0.005)
        {
            Log.Info($"{Title}: Enter changed nothing on screen, pressing Enter once more");
            SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter));
        }
        return true;
    }

    /// <summary>
    /// Waits until the remote screen did not change for <paramref name="stillMs"/>, at most <paramref name="maxMs"/>.
    /// Returns the last sample; null when the session cannot be seen.
    /// </summary>
    async Task<int[]?> WaitForStillAsync(int stillMs, int maxMs)
    {
        var previous = Sample();
        for (int waited = 0, still = 0; previous != null && waited < maxMs && still < stillMs; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now) return null;
            still = Changed(previous, now) < 0.002 ? still + 100 : 0;
            previous = now;
        }
        return previous;
    }

    /// <summary>
    /// Waits until a window came up over a good part of the remote screen (a browser opening) and settled, at most
    /// <paramref name="maxMs"/>: a new tab in a browser already open changes less, and only waits that long.
    /// </summary>
    public async Task WaitForWindowAsync(int maxMs)
    {
        if (Sample() is not { } before)
        {
            await Task.Delay(1500); // cannot see the session: a fixed wait
            return;
        }
        for (var waited = 0; waited < maxMs && !_disposed; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now || Changed(before, now) < 0.15) continue;
            await WaitForStillAsync(250, 1500);
            return;
        }
    }

    /// <summary>Waits while the shown desktop still changes (lock screen sliding away), at most <see cref="Timings.LogonSettleExtra"/>.</summary>
    public async Task WaitWhileChangingAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await WaitForStillAsync(200, Timings.LogonSettleExtra);
        if (clock.ElapsedMilliseconds > 400) Log.Info($"{Title}: screen still changing after logon, waited {clock.ElapsedMilliseconds} ms more");
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the session's browser: Win+R, the address, Enter. Only a safe web address
    /// (<see cref="Links.IsSafe"/>): Run would run anything else as a command. False when it could not be typed.
    /// </summary>
    public async Task<bool> OpenInSessionAsync(string url)
    {
        if (!Links.IsSafe(url) || !IsReady) return false;
        Log.Info($"{Title}: opening a link in the session");
        if (!await OpenRunAsync()) return false;
        if (RemoteInput.Text(url) is null) return false; // a character the keyboard layout cannot type
        // One character at a time, as typed by hand: Run completes from its history while one types, and a burst of a
        // whole address got mixed up with its completion (DEV-13: half the address lost, a part repeated).
        foreach (var ch in url)
        {
            if (!SendKeysFocused(RemoteInput.Text(ch.ToString())!)) return false;
            await Task.Delay(12);
        }
        await Task.Delay(300);
        // A completion appended after the address stays selected: Delete drops it, else Enter would open that instead.
        var sent = SendKeysFocused(RemoteInput.Chord(RemoteInput.Delete)) && SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter));
        Log.Info($"{Title}: typed the link ({url.Length} characters), Enter {(sent ? "sent" : "refused")}");
        return sent;
    }

    /// <summary>The account the session signed in with (as given to the control), or null.</summary>
    public string? SignedInUser => string.IsNullOrEmpty(_user) ? null : string.IsNullOrEmpty(_domain) ? _user : $@"{_domain}\{_user}";

    /// <summary>A sign-out is on its way by another route than typing: the disconnect it brings closes the window.</summary>
    public void ExpectSignOut() => _intent = Intent.SignOut;

    public void CancelSignOut()
    {
        if (_intent == Intent.SignOut) _intent = Intent.None;
    }

    /// <summary>
    /// Sends Ctrl+Alt+Del: the server shows its security screen (lock, change password, Task Manager, sign out), also over
    /// the lock screen. Succeeds once that page is painted: the server switches to its secure desktop through a black frame
    /// first, which the caller keeps covered until then.
    /// </summary>
    public async Task<bool> SendCtrlAltDelAsync()
    {
        if (_disposed || _rdp.GetOcx() == null) return false;
        FocusRemote();
        await Task.Delay(150); // focus settles
        var before = Sample();
        if (!SendKeys(RemoteInput.Chord(RemoteInput.Ctrl, RemoteInput.Alt, RemoteInput.Delete))) return false;
        if (before == null)
        {
            await Task.Delay(700); // cannot see the session: a fixed wait
            return true;
        }
        for (var waited = 0; waited < 3000; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now || Changed(before, now) <= 0.6) continue;
            // Past the black frame (unless the page really is black), then settled.
            for (var black = 0; black < 1500 && BlackFraction() > 0.9; black += 50) await Task.Delay(50);
            await WaitForStillAsync(150, 800);
            return true;
        }
        Log.Warn($"{Title}: the Ctrl+Alt+Del screen did not open");
        return false;
    }

    /// <summary>
    /// Win+R, then waits until something opened on the remote screen: a freshly logged-on server can take well over a
    /// second, and keys typed before land elsewhere (Start search, seen on DEV-12). False when nothing opened.
    /// </summary>
    async Task<bool> OpenRunAsync()
    {
        if (_disposed || _rdp.GetOcx() == null) return false;
        var before = Sample();
        if (!SendKeysFocused(RemoteInput.Chord(RemoteInput.WinKey, RemoteInput.R))) return false;
        if (before == null)
        {
            await Task.Delay(1500); // cannot see the session: a generous fixed wait
            return true;
        }
        // The Run dialog first flashes up as an unpainted box at the top left, then moves into place and only then takes
        // the keyboard (DEV-12): wait for a change, then for the screen to settle.
        int[]? previous = null;
        var settled = 0;
        for (var waited = 0; waited < 4000; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now) continue;
            if (Changed(before, now) < 0.005)
            {
                previous = null;
                continue;
            }
            settled = previous != null && Changed(previous, now) < 0.002 ? settled + 1 : 0;
            previous = now;
            if (settled < 2) continue;
            Log.Info($"{Title}: Win+R opened something after {waited + 100} ms ({Changed(before, now):P1} of the screen changed)");
            if (Environment.GetEnvironmentVariable("SESSIONWRIGHT_PROBE") is { Length: > 0 } dump)
            {
                try { _probe?.Save(System.IO.Path.Combine(dump, $"probe-{DateTime.Now:HHmmss-fff}.png")); }
                catch (Exception ex) { Log.Warn($"{Title}: probe dump failed: {ex.Message}"); }
            }
            await Task.Delay(200);
            return true;
        }
        Log.Warn($"{Title}: nothing opened on Win+R within 4 s");
        return false;
    }

    // ---------- Connection quality ----------

    // The RDP client's own measurement arrives when the server runs its network detection: 4-40 s after connecting on
    // DEV-12/13, on some connections never. A TCP handshake with the RDP port gives the latency at once and then every
    // 30 s; the bandwidth (and the server's quality level) are added when the client reports them.
    int? _serverQuality, _bandwidth, _rtt;
    CancellationTokenSource? _latency;

    void StartLatencyProbe()
    {
        _latency?.Cancel();
        if (!string.IsNullOrWhiteSpace(_options.Gateway)) return; // the handshake would time the gateway, not the server
        var cancel = (_latency = new CancellationTokenSource()).Token;
        _ = LatencyLoopAsync(cancel);
    }

    async Task LatencyLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && !_disposed)
        {
            if (await MeasureRttAsync(Connection.Host, Connection.Port) is { } rtt && !cancel.IsCancellationRequested)
            {
                if (_rtt == null) Log.Info($"{Title}: latency {rtt} ms");
                _rtt = rtt;
                UpdateNetworkInfo();
            }
            try { await Task.Delay(Timings.LatencyProbe, cancel); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Time of a TCP handshake with the server (name resolved first, so it is not counted); null if it fails.</summary>
    internal static async Task<int?> MeasureRttAsync(string host, int port)
    {
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
            using var tcp = new System.Net.Sockets.TcpClient();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await tcp.ConnectAsync(addresses, port, timeout.Token);
            return Math.Max(1, (int)clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    void UpdateNetworkInfo()
    {
        if (_rtt is not { } rtt) return;
        Quality = _serverQuality ?? (rtt < 60 ? 4 : rtt < 120 ? 3 : rtt < 250 ? 2 : 1);
        NetworkInfo = _bandwidth is { } bandwidth
            ? Loc.Format("NetworkInfo", rtt, bandwidth >= 1000 ? $"{bandwidth / 1000.0:0.#} Mb/s" : $"{bandwidth} kb/s")
            : Loc.Format("NetworkLatency", rtt);
    }

    /// <summary>Called by the window once it left full screen: the control's own flag (set by its "Go") goes off.</summary>
    public void LeftFullScreen()
    {
        if (_disposed || _rdp.GetOcx() == null) return;
        try
        {
            if ((bool)_rdp.Ocx.FullScreen) _rdp.Ocx.FullScreen = false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
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
