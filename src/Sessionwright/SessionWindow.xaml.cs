using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;
using SystemInformation = System.Windows.Forms.SystemInformation;

namespace Sessionwright;

/// <summary>
/// One session in its own top-level window (own taskbar entry and thumbnail). Every visible change of the session -
/// full screen on/off, moving to another monitor, showing the desktop after logon, sign-out - runs as a transition:
/// a cover fades in, the change happens behind it with the RDP control resized once, and the cover lifts only once the
/// server shows the desktop at its new size. Transitions run one at a time.
/// </summary>
public partial class SessionWindow : FluentWindow
{
    FullScreenBar? _bar;
    Cover? _cover; // the running transition's or operation's cover
    Task _queue = Task.CompletedTask;
    bool _wantFullScreen, _applyQueued, _closing, _closed, _confirmed, _asking, _refitPending, _exitWhenActive;
    SessionPhase _phase;
    int _phaseChanges; // a wait outside the queue gives up when the phase changed meanwhile
    WindowPlacement? _restore; // the window before full screen
    Rectangle? _restoreBounds; // its exact bounds, for a snapped window (its placement holds the rect from before the snap)
    Rectangle _covered; // physical: the monitor, or the virtual screen for a session on all monitors
    Rectangle _monitor; // physical: the monitor with the full-screen bar

    public RdpSession Session { get; }
    public bool IsFullScreen { get; private set; }
    /// <summary>Physical bounds of the monitor the full-screen bar is on (dialogs and cover spinners over an all-monitors session go there).</summary>
    public Rectangle? MessageMonitor => IsFullScreen && Session.Multimon ? _monitor : null;

    internal SessionWindow(RdpSession session)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Session = session;
        DataContext = session;
        // In a shown desktop "Go" comes from the connection bar, and the control's own flag is off after entering by a key
        // or a button: a toggle. While connecting the control raises "Go" by itself for a full-screen size: no toggle then.
        // "Leave" stays off (leaving sets the flag off, which may raise it again).
        session.FullScreenRequested += on => RequestFullScreen(on && !(_wantFullScreen && Session.IsReady && Session.ShowsDesktop));
        session.MinimizeRequested += () => WindowState = WindowState.Minimized;
        session.RevealRequested += () => _ = RevealAsync();
        session.PropertyChanged += OnSessionChanged;
        session.Connection.PropertyChanged += OnConnectionChanged;
        session.Closed += OnSessionClosed;
        UpdateVmButton();
        SourceInitialized += (_, _) => Native.Subclass(this, Hook);
        Bar.MaximizeActionOverride = (_, _) => _ = SetWindowStateAsync(WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        void SetThumbButtons() => Taskbar.SetThumbButtons(this,
            ('', Loc.Get("FullScreenTip"), () => RequestFullScreen(!_wantFullScreen)),
            ('' /* SignOut */, Loc.Get("SignOut"), () => SignOutFromThumbnail()),
            ('', Loc.Get("Disconnect"), RequestClose));
        SetThumbButtons();
        // Their glyphs follow the Windows light/dark mode (WM_SETTINGCHANGE "ImmersiveColorSet" = General).
        UserPreferenceChangedEventHandler onPreferences = (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && !_closed) SetThumbButtons();
        };
        SystemEvents.UserPreferenceChanged += onPreferences;
        // Clicking the taskbar thumbnail should put the keyboard straight into the remote desktop.
        Activated += (_, _) =>
        {
            SessionKeys.Start(); // set again: newer than the RDP control's own keyboard hook
            UpdateTopmost();
            // Dropped while in the background: full screen ends once the user comes back, not behind their back.
            if (_exitWhenActive && IsFullScreen && Session.Phase == SessionPhase.Disconnected) RequestFullScreen(false);
            _exitWhenActive = false;
            if (Session.ShowsDesktop) Session.FocusRemote();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(UpdateTopmost);
        // Moving to a monitor with another scale makes WPF keep the DIP size; full screen must still cover the monitor.
        // A minimized full-screen window is left alone (no restoring it behind the user's back): finished once restored.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) return;
            if (_wantFullScreen != IsFullScreen) RequestFullScreen(_wantFullScreen);
            if (_refitPending && IsFullScreen) _ = Enqueue(RefitAsync);
            // A transition that ended while minimized did not show the bar: after any transition still running.
            if (IsFullScreen) _ = Enqueue(() =>
            {
                if (_bar?.IsVisible != true) ShowBar();
                return Task.CompletedTask;
            });
        };
        DpiChanged += (_, _) =>
        {
            if (IsFullScreen) Dispatcher.BeginInvoke(() => Native.SetBounds(this, _covered));
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= onPreferences;
            _closed = true;
            _cover?.Close();
            _bar?.Close();
        };
    }

    /// <summary>Opens the window where this connection's window was last (or <paramref name="fallback"/>), in full screen if its settings say so.</summary>
    public void Open(WindowPlacement placement)
    {
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Native.SetPlacement(this, placement, show: false);
        if (Session.StartsFullScreen)
        {
            // Straight into full screen (connecting there): no window that first appears and then jumps.
            _restore = placement;
            _restoreBounds = null; // never shown: no real bounds yet
            _wantFullScreen = true;
            ApplyFullScreen(BarMonitor(Screen.FromRectangle(new Rectangle(placement.X, placement.Y, placement.Width, placement.Height)).Bounds));
            ContentRendered += (_, _) => ShowBar(); // an owned window needs a shown owner
        }
        else if (placement.Maximized) WindowState = WindowState.Maximized;
        Show();
    }

    void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Connection.VmShutdownAt) or nameof(Connection.VmState)) UpdateVmButton();
    }

    void UpdateVmButton()
    {
        VmButton.Visibility = Session.Connection.AzureVm != null ? Visibility.Visible : Visibility.Collapsed;
        if (Session.Connection.AzureVm == null) return;
        var (icon, text, tip) = AzureShutdown.Indicator(Session);
        VmIcon.Symbol = icon;
        VmText.Text = text;
        VmText.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        VmButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(VmButton, tip);
    }

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RdpSession.ShutDownVmAfter)) UpdateVmButton();
        if (e.PropertyName == nameof(RdpSession.IsBusy)) SetCoveredPreview(Session.IsBusy); // sign-out, Ctrl+Alt+Del
        switch (e.PropertyName)
        {
            case nameof(RdpSession.Title):
                _bar?.Update(Session);
                break;
            case nameof(RdpSession.Phase):
                var previous = _phase;
                _phase = Session.Phase;
                _phaseChanges++;
                switch (Session.Phase)
                {
                    case SessionPhase.Ready:
                        _ = OnReadyAsync(afterLogon: previous == SessionPhase.LoggingOn);
                        break;
                    case SessionPhase.Connecting or SessionPhase.Reconnecting or SessionPhase.Disconnected:
                        _ = ConcealAsync();
                        if (Session.Phase != SessionPhase.Disconnected) break;
                        // Dropped while looking elsewhere: the taskbar button flashes until the window is brought forward.
                        Native.FlashUntilActive(this);
                        if (!IsFullScreen) break;
                        if (IsActive) RequestFullScreen(false);
                        else _exitWhenActive = true;
                        break;
                }
                break;
        }
    }

    /// <summary>Logged on (or reconnected): fit the desktop to the window, then show it.</summary>
    async Task OnReadyAsync(bool afterLogon)
    {
        var phase = _phaseChanges;
        if (!Session.ShowsDesktop)
        {
            if (Session.FitDisplay()) await Session.WaitForDisplayAsync();
            // Simplification: fixed wait. Right after logon the control still shows a stale cached frame and the lock screen
            // sliding away (about 1 s on DEV-13), and no event marks the desktop as ready. A slower server gets more
            // time under the reveal's cover (see RevealAsync).
            if (afterLogon) await Task.Delay(Timings.LogonSettle);
            if (phase == _phaseChanges) await RevealAsync(afterLogon);
        }
        else if (Session.DisplayOutdated)
        {
            // Shown before logon completed (slow logon): the size change after logon happens behind a cover.
            await Enqueue(async () =>
            {
                if (phase != _phaseChanges || !Session.DisplayOutdated) return;
                await CoverWhileAsync(Native.Bounds(this), async () =>
                {
                    Session.SuspendLayout();
                    Session.ResumeLayout(); // sends the update, now behind the cover
                    await Session.WaitForDisplayAsync();
                });
            });
        }
        // A conceal queued before this Ready (reconnected while its cover faded in) hid the desktop: show it again.
        if (phase == _phaseChanges) _ = RevealAsync();
    }

    // ---------- Transitions ----------

    Task Enqueue(Func<Task> work)
    {
        var previous = _queue;
        var next = Run();
        _queue = next;
        return next;

        async Task Run()
        {
            await previous;
            if (_closed || _closing) return;
            try { await work(); }
            catch (Exception ex) { Log.Error(ex, $"{Session.Title}: transition failed"); }
        }
    }

    /// <summary>Covers <paramref name="bounds"/> while <paramref name="work"/> runs; shows a spinner if the wait gets long.</summary>
    /// <param name="content">Where its spinner goes: the monitor or window the session ends up on (a cover over two
    /// monitors would center it on their union, near the edge between them).</param>
    async Task CoverWhileAsync(Rectangle bounds, Func<Task> work, Rectangle? content = null)
    {
        var cover = _cover = await Cover.ShowAsync(this, bounds, content: content ?? MessageMonitor);
        if (_closing)
        {
            cover.Close(); // closed while the cover faded in: the window is hidden, nothing left to change
            return;
        }
        try
        {
            var task = work();
            if (await Task.WhenAny(task, Task.Delay(500)) != task) cover.SetMessage("");
            await task;
        }
        finally
        {
            await LiftAsync(cover);
        }
    }

    /// <summary>From the status screen to the remote desktop through the stage color, without a hard cut.</summary>
    Task RevealAsync(bool afterLogon = false) => Enqueue(async () =>
    {
        if (Session.ShowsDesktop || Session.Phase is not (SessionPhase.LoggingOn or SessionPhase.Ready)) return;
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            Session.ShowsDesktop = true;
            return;
        }
        var cover = _cover = await Cover.ShowAsync(this, StageBounds(), fadeMs: 150);
        try
        {
            if (_closing)
            {
                cover.Close();
                return;
            }
            Session.ShowsDesktop = true;
            if (Native.IsForeground(this)) Session.FocusRemote();
            await Session.WaitForPaintAsync(800); // a hidden control repaints once shown
            if (afterLogon) await Session.WaitWhileChangingAsync();
        }
        finally
        {
            await LiftAsync(cover, 250);
        }
    });

    /// <summary>From the remote desktop to the status screen (connection lost, reconnecting), without a hard cut.</summary>
    Task ConcealAsync() => Enqueue(async () =>
    {
        if (!Session.ShowsDesktop || Session.Phase is SessionPhase.Ready or SessionPhase.Closed) return;
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            Session.ShowsDesktop = false;
            return;
        }
        var cover = _cover = await Cover.ShowAsync(this, StageBounds(), fadeMs: 120);
        try
        {
            if (_closing)
            {
                cover.Close();
                return;
            }
            // Reconnected while the cover faded in: the desktop stays (OnReadyAsync saw it shown).
            if (Session.Phase is SessionPhase.Ready or SessionPhase.Closed) return;
            Session.ShowsDesktop = false;
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        finally
        {
            await LiftAsync(cover, 200);
        }
    });

    /// <summary>The session area in physical pixels.</summary>
    Rectangle StageBounds()
    {
        var topLeft = Stage.PointToScreen(new Point(0, 0));
        var bottomRight = Stage.PointToScreen(new Point(Stage.ActualWidth, Stage.ActualHeight));
        return Rectangle.FromLTRB((int)topLeft.X, (int)topLeft.Y, (int)Math.Ceiling(bottomRight.X), (int)Math.Ceiling(bottomRight.Y));
    }

    /// <summary>Full screen on/off (button, F11, Ctrl+Alt+Enter, bar). Requests made during a transition coalesce.</summary>
    public void RequestFullScreen(bool on)
    {
        _wantFullScreen = on;
        if (_applyQueued) return;
        _applyQueued = true;
        _ = Enqueue(async () =>
        {
            _applyQueued = false;
            if (_wantFullScreen == IsFullScreen) return;
            if (!_wantFullScreen && WindowState == WindowState.Minimized) return; // finished when the user restores it
            if (_wantFullScreen) await EnterFullScreenAsync(null);
            else await ExitFullScreenAsync();
        });
    }

    async Task EnterFullScreenAsync(Screen? target)
    {
        Native.Restore(this);
        var before = Native.Bounds(this);
        var monitor = BarMonitor((target ?? Screen.FromRectangle(before)).Bounds);
        var covered = Session.Multimon ? Session.SpanBounds : monitor;
        using var _ = Log.Time($"{Session.Title}: enter full screen");
        await CoverWhileAsync(Rectangle.Union(before, covered), async () =>
        {
            Session.SuspendLayout();
            _restore = Native.GetPlacement(this);
            _restoreBounds = _restore.Maximized ? null : before;
            ApplyFullScreen(monitor);
            await Dispatcher.Yield(DispatcherPriority.Loaded); // layout at the new size
            Session.ResumeLayout();
            await Session.WaitForDisplayAsync();
        }, monitor);
        ShowBar();
        Session.FocusRemote();
    }

    /// <summary>On several monitors the bar and the messages stay on one the session uses (not every monitor may be chosen).</summary>
    Rectangle BarMonitor(Rectangle monitor) => Session.Multimon ? Session.SpanMonitor(monitor) : monitor;

    /// <summary>Borderless over the monitor (or several of them), chrome hidden. Also used before the window is first shown.</summary>
    void ApplyFullScreen(Rectangle monitor, bool chosen = true)
    {
        IsFullScreen = true;
        _monitor = monitor;
        // The monitor the user put it on (not one it was moved to because that monitor went): it goes back there.
        if (chosen && Screen.AllScreens.FirstOrDefault(s => s.Bounds == monitor) is { } home) (_homeDevice, _homeBounds) = (home.DeviceName, home.Bounds);
        _covered = Session.Multimon ? Session.SpanBounds : monitor;
        SetChrome(false);
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        WindowCornerPreference = WindowCornerPreference.DoNotRound;
        Native.SetBorderVisible(this, false);
        Native.SetBounds(this, _covered);
        PlaceStatus();
        UpdateTopmost();
    }

    /// <summary>Where the window goes when it leaves full screen: back on the monitor the session is on now (the bar may
    /// have been dragged to another one, or the monitor unplugged).</summary>
    WindowPlacement RestorePlacement()
    {
        var restore = _restore ?? Native.GetPlacement(this);
        return Session.Multimon ? restore : Native.MoveTo(restore, Screen.FromRectangle(_monitor));
    }

    async Task ExitFullScreenAsync()
    {
        var restore = RestorePlacement();
        // A snapped window: its placement holds the rect from before the snap, the snapped rect is restored exactly.
        var exact = _restoreBounds is { } b && !restore.Maximized && (Session.Multimon || Screen.FromRectangle(b).Bounds == Screen.FromRectangle(_monitor).Bounds)
            ? b : (Rectangle?)null;
        var target = exact ?? (restore.Maximized
            ? Screen.FromRectangle(new Rectangle(restore.X, restore.Y, restore.Width, restore.Height)).WorkingArea
            : new Rectangle(restore.X, restore.Y, restore.Width, restore.Height));
        using var _ = Log.Time($"{Session.Title}: exit full screen");
        HideBar();
        await CoverWhileAsync(Rectangle.Union(_covered, target), async () =>
        {
            Session.SuspendLayout();
            IsFullScreen = false;
            Session.StatusMargin = default;
            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            WindowCornerPreference = WindowCornerPreference.Round;
            Native.SetBorderVisible(this, true);
            SetChrome(true);
            Native.SetPlacement(this, restore, show: true); // bounds and maximized state in one step
            if (exact is { } snapped) Native.SetBounds(this, snapped);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Session.ResumeLayout();
            Session.LeftFullScreen();
            await Session.WaitForDisplayAsync();
        }, target);
        Session.FocusRemote();
    }

    /// <summary>The full-screen bar was dropped on another monitor: the session follows it there.</summary>
    Task MoveToMonitorAsync(Rectangle monitor) => Enqueue(async () =>
    {
        if (Session.Multimon && BarMonitor(monitor) != monitor)
        {
            _bar?.Dock(_monitor, center: true); // dropped on a monitor the session does not use: back where it was
            return;
        }
        if (!IsFullScreen || monitor == _monitor) return;
        if (Session.Multimon)
        {
            _monitor = monitor; // the desktop already spans all monitors: only the bar and the status text move
            PlaceStatus();
            return;
        }
        HideBar();
        // Only the monitor it goes to: the old one just shows what is behind once the window has left it.
        await CoverWhileAsync(monitor, async () =>
        {
            Session.SuspendLayout();
            ApplyFullScreen(monitor);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Session.ResumeLayout();
            await Session.WaitForDisplayAsync();
        }, monitor);
        if (_closing) return;
        ShowBar();
        Activate();
        Session.FocusRemote();
    });

    string? _homeDevice;
    Rectangle _homeBounds;
    DispatcherTimer? _displaySettled;

    // A monitor unplugged, plugged back or changed resolution. Windows sends several of these and moves the windows only
    // after them, so the session settles once they have stopped (acting on the first one put the session on one monitor
    // and its bar on another).
    void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed) return;
        if (_displaySettled == null)
        {
            _displaySettled = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _displaySettled.Tick += (_, _) =>
            {
                _displaySettled.Stop();
                // Minimized: when it is restored (restoring it here would bring every minimized session back, e.g. when
                // the monitors wake up).
                if (!IsFullScreen || _closed) return;
                if (WindowState == WindowState.Minimized) _refitPending = true;
                else _ = Enqueue(RefitAsync);
            };
        }
        _displaySettled.Stop();
        _displaySettled.Start();
    });

    /// <summary>
    /// After the monitors changed: full screen on the monitor the user put it on when that one is there (plugged back),
    /// else on the one it was on, else where Windows moved the window. Under a cover when it moves: the remote desktop
    /// changes its size.
    /// </summary>
    async Task RefitAsync()
    {
        _refitPending = false;
        if (!IsFullScreen) return;
        var screens = Screen.AllScreens;
        var monitor = (screens.FirstOrDefault(s => s.DeviceName == _homeDevice && s.Bounds == _homeBounds)
                       ?? screens.FirstOrDefault(s => s.Bounds == _homeBounds)
                       ?? screens.FirstOrDefault(s => s.Bounds == _monitor)
                       ?? Screen.FromHandle(Native.Handle(this))).Bounds;
        monitor = BarMonitor(monitor);
        var covered = Session.Multimon ? Session.SpanBounds : monitor;
        var old = _monitor;
        if (monitor == old && covered == _covered && Native.Bounds(this) == _covered)
        {
            if (_bar?.IsVisible == true) _bar.Dock(monitor, center: false); // Windows may have moved the bar meanwhile
            return;
        }
        Log.Info($"{Session.Title}: monitors changed, full screen on {monitor} (was {old})");
        HideBar();
        await CoverWhileAsync(covered, async () =>
        {
            Session.SuspendLayout();
            ApplyFullScreen(monitor, chosen: false);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Session.ResumeLayout();
            await Session.WaitForDisplayAsync();
        }, monitor);
        if (_closing) return;
        ShowBar();
    }

    void SetChrome(bool visible)
    {
        Bar.Visibility = EnvStrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Over all monitors, the status text (connecting, errors) is centered on the bar's monitor.</summary>
    void PlaceStatus()
    {
        if (!IsFullScreen || !Session.Multimon)
        {
            Session.StatusMargin = default;
            return;
        }
        var scale = Native.DpiOf(this) / 96.0; // the window may not be shown yet: physical px to its DIPs
        Session.StatusMargin = new Thickness((_monitor.Left - _covered.Left) / scale, (_monitor.Top - _covered.Top) / scale,
            (_covered.Right - _monitor.Right) / scale, (_covered.Bottom - _monitor.Bottom) / scale);
    }

    /// <summary>
    /// Covers the taskbar only while this window (or its bar or a dialog over it) is active: after switching to another
    /// app or another session window, the session may not stay on top of it.
    /// </summary>
    void UpdateTopmost()
    {
        var topmost = IsFullScreen && (IsActive || OwnedWindows.Cast<Window>().Any(w => w.IsActive));
        if (!topmost && Topmost) Native.DropBelowForeground(this); // else it would cover the window just activated
        Topmost = topmost;
        if (topmost) MiniView.RaiseAll(); // the small views stay over a full-screen session too
    }

    // FluentWindow puts the 1 px DWM border back on every activation, deactivation and theme change: in full screen it
    // showed as a gray line around the session.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        HideBorderInFullScreen();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        HideBorderInFullScreen();
    }

    internal void HideBorderInFullScreen()
    {
        if (IsFullScreen && !_closed) Native.SetBorderVisible(this, false);
    }

    // ---------- Taskbar preview while covered ----------
    // A cover is a window of its own, so the taskbar preview (and Peek) of this window showed what the cover hides: the
    // typing of a sign-out, a stretched frame. Meanwhile Windows gets a picture of the cover instead (an iconic preview).

    void SetCoveredPreview(bool on)
    {
        var hwnd = Native.Handle(this);
        if (hwnd == IntPtr.Zero) return;
        var value = on ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 7 /* DWMWA_FORCE_ICONIC_REPRESENTATION */, ref value, sizeof(int));
        DwmSetWindowAttribute(hwnd, 10 /* DWMWA_HAS_ICONIC_BITMAP */, ref value, sizeof(int));
        if (!on) return;
        DwmInvalidateIconicBitmaps(hwnd);
        // Given at once too: during a sign-out of a few seconds Windows did not ask at all (its placeholder showed). It
        // takes a thumbnail it did not ask for up to 200 x 108 (larger ones fail with E_INVALIDARG; measured), and draws
        // it unscaled: anything smaller leaves the preview's frame partly empty.
        SetThumbnail(hwnd, 200, 108);
        SetLivePreview(hwnd);
    }

    // Signing out: what the cover says; Ctrl+Alt+Del (a moment): just the session.
    (string Title, string? Detail) CoverText() =>
        _signingOut ? (Loc.Get("SigningOut"), Session.Title)
        : _askingSignOut ? (Session.Title, Loc.Get("SignOutQuestion"))
        : (Session.Title, null);

    /// <summary>The largest picture in the window's proportions within the size given.</summary>
    void SetThumbnail(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        var bounds = Native.Bounds(this);
        var scale = Math.Min(maxWidth / (double)Math.Max(1, bounds.Width), maxHeight / (double)Math.Max(1, bounds.Height));
        var (title, detail) = CoverText();
        using var picture = Cover.Picture((int)(bounds.Width * scale), (int)(bounds.Height * scale), title, detail);
        var bitmap = Cover.Dib(picture);
        try
        {
            if (DwmSetIconicThumbnail(hwnd, bitmap, 0) is var hr and not 0)
                Log.Info($"{Session.Title}: taskbar preview {picture.Width}x{picture.Height} (within {maxWidth}x{maxHeight}) refused: 0x{hr:X8}");
        }
        finally { DeleteObject(bitmap); }
    }

    /// <summary>What Peek (hovering the thumbnail) shows: the cover at the window's size.</summary>
    void SetLivePreview(IntPtr hwnd)
    {
        var bounds = Native.Bounds(this);
        var (title, detail) = CoverText();
        using var picture = Cover.Picture(bounds.Width, bounds.Height, title, detail);
        var bitmap = Cover.Dib(picture);
        try { if (DwmSetIconicLivePreviewBitmap(hwnd, bitmap, IntPtr.Zero, 0) is var hr and not 0) Log.Info($"{Session.Title}: Peek preview: 0x{hr:X8}"); }
        finally { DeleteObject(bitmap); }
    }

    IntPtr? SendCoveredPreview(IntPtr hwnd, uint msg, IntPtr lParam)
    {
        if (msg == 0x0323 /* WM_DWMSENDICONICTHUMBNAIL: the size it can take, width high, height low */)
            SetThumbnail(hwnd, (int)((long)lParam >> 16) & 0xFFFF, (int)(long)lParam & 0xFFFF);
        else SetLivePreview(hwnd);
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr bitmap, int flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr bitmap, IntPtr client, int flags);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr gdiObject);

    IntPtr? Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0323 /* WM_DWMSENDICONICTHUMBNAIL */ or 0x0326 /* WM_DWMSENDICONICLIVEPREVIEWBITMAP */:
                if (!Session.IsBusy && !_askingSignOut) return null;
                try { return SendCoveredPreview(hwnd, msg, lParam); }
                catch (Exception ex) // never out of a window procedure: Windows keeps its placeholder picture then
                {
                    Log.Error(ex, $"{Session.Title}: taskbar preview");
                    return null;
                }
            // In full screen every point of the window is client area: WPF-UI's chrome keeps its resize border whatever
            // ResizeMode says, so wherever the RDP control does not cover the edge (status screen) the window could be resized.
            case 0x84 /* WM_NCHITTEST */ when IsFullScreen:
                return 1; // HTCLIENT
            // Maximize / restore from the system menu, a title bar double-click or Win+arrows: through the cover too.
            case 0x0112 /* WM_SYSCOMMAND */ when ((int)wParam & 0xFFF0) is var cmd && !IsFullScreen
                                               && (cmd == 0xF030 /* SC_MAXIMIZE */ && WindowState == WindowState.Normal
                                                   || cmd == 0xF120 /* SC_RESTORE */ && WindowState == WindowState.Maximized):
                Dispatcher.BeginInvoke(() => SetWindowStateAsync(cmd == 0xF030 ? WindowState.Maximized : WindowState.Normal));
                return 0;
            default:
                return null;
        }
    }

    /// <summary>
    /// Maximize / restore behind a cover, like full screen: Windows' own animation would show the remote desktop
    /// stretched, then jumping to its new resolution once the server repainted it.
    /// </summary>
    Task SetWindowStateAsync(WindowState state) => Enqueue(async () =>
    {
        if (IsFullScreen || WindowState == state || WindowState == WindowState.Minimized) return;
        if (!Session.ShowsDesktop || !IsVisible)
        {
            WindowState = state; // the status screen has nothing to hide
            return;
        }
        var before = Native.Bounds(this);
        var normal = Native.GetPlacement(this);
        var after = state == WindowState.Maximized
            ? Screen.FromRectangle(before).WorkingArea
            : new Rectangle(normal.X, normal.Y, normal.Width, normal.Height);
        using var _ = Log.Time($"{Session.Title}: {(state == WindowState.Maximized ? "maximize" : "restore")}");
        await CoverWhileAsync(Rectangle.Union(before, after), async () =>
        {
            Session.SuspendLayout();
            WindowState = state;
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Session.ResumeLayout();
            await Session.WaitForDisplayAsync();
        }, after);
        Session.FocusRemote();
    });

    void ShowBar()
    {
        if (!IsFullScreen || _closed || _closing || WindowState == WindowState.Minimized) return;
        if (_bar == null)
        {
            _bar = new FullScreenBar { Owner = this };
            _bar.MinimizeClicked += () => WindowState = WindowState.Minimized;
            _bar.RestoreClicked += () => RequestFullScreen(false);
            _bar.SignOutClicked += () => _ = SignOutAsync();
            _bar.CtrlAltDelClicked += () => _ = CtrlAltDelAsync();
            _bar.CloseClicked += RequestClose;
            _bar.SessionChosen += s => App.Sessions.Show(s);
            _bar.Dropped += monitor => _ = MoveToMonitorAsync(monitor);
            _bar.FocusReturned += () => Session.FocusRemote();
            _bar.Activated += (_, _) => UpdateTopmost();
            _bar.Deactivated += (_, _) => Dispatcher.BeginInvoke(UpdateTopmost);
        }
        _bar.Update(Session);
        _bar.Dock(_monitor, center: true);
    }

    void HideBar() => _bar?.Hide();

    /// <summary>The bar and a running cover go before the window: owned windows would otherwise outlive it on screen.</summary>
    void HideOverlays()
    {
        HideBar();
        _cover?.Close();
        _cover = null;
    }

    // ---------- Remote actions ----------

    /// <summary>The cover for typing into the session: the whole window (in full screen, everything it covers).</summary>
    Rectangle OperationBounds() => IsFullScreen ? _covered : Native.Bounds(this);

    bool _signingOut, _askingSignOut;

    // Locked meanwhile, the session draws nothing new: the typing could not see the Run dialog open and gave up.
    static bool _locked;
    static int _locks;

    static SessionWindow() => SystemEvents.SessionSwitch += (_, e) =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _locked = true;
            _locks++;
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
    };

    static async Task WaitUntilUnlockedAsync()
    {
        while (_locked) await Task.Delay(250);
    }

    /// <param name="ask">False when the user already chose to sign several sessions out at once.</param>
    internal async Task SignOutAsync(bool ask = true)
    {
        if (!Session.IsReady) return;
        if (ask && !App.Store.Data.Settings.SkipSessionConfirmations)
        {
            // While the question is open the taskbar preview showed a cropped, enlarged part of the remote desktop: the
            // same picture as under the cover instead, asking.
            _askingSignOut = true;
            SetCoveredPreview(true);
            var confirmed = false;
            try { confirmed = Dialog.Confirm(this, Loc.Format("ConfirmSignOut", Session.Title), Loc.Get("SignOut"), DialogKind.Warning); }
            finally
            {
                _askingSignOut = false;
                // Confirmed: it stays until the sign-out's own (busy) preview takes over, with no live frame in between.
                if (!confirmed && !Session.IsBusy) SetCoveredPreview(false);
            }
            if (!confirmed)
            {
                Session.FocusRemote();
                return;
            }
        }
        await Enqueue(async () =>
        {
            _signingOut = true;
            Session.IsBusy = true;
            // Signed out (the window closes), or disconnected otherwise (its status screen with Reconnect shows).
            var ended = new TaskCompletionSource();
            void OnClosed(RdpSession _) => ended.TrySetResult();
            void OnPhase(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(RdpSession.Phase) && Session.Phase == SessionPhase.Disconnected) ended.TrySetResult();
            }
            Session.Closed += OnClosed;
            Session.PropertyChanged += OnPhase;
            try
            {
                // Through the server's API when it can be asked: nothing typed, the window and keyboard stay the user's.
                if (Session.SignedInUser is { } user)
                {
                    // Covered as the typed sign-out is: Windows' own signing-out screen would show in the window otherwise.
                    // A minimized window shows nothing to cover.
                    if (WindowState != WindowState.Minimized) _cover = await Cover.ShowAsync(this, OperationBounds(), Loc.Get("SigningOut"), Session.Title, 120, MessageMonitor ?? StageBounds());
                    if (_closing) return; // closed while the cover faded in: the finally lifts it
                    Session.ExpectSignOut();
                    if (await RemoteSessions.TrySignOutAsync(Session.Connection, user))
                    {
                        if (await Task.WhenAny(ended.Task, Task.Delay(20000)) == ended.Task) return;
                        Log.Warn($"{Session.Title}: signed out through the API, but the session did not end: typing it");
                    }
                    Session.CancelSignOut();
                }
                await WaitUntilUnlockedAsync();
                // Not pulled forward: the user may have gone to another window (another session) meanwhile, and the
                // typing hands the foreground back after every key (see RdpSession.SendKeysFocused).
                await BringForwardAsync(activate: false);
                // Opaque before the first key: the Run dialog and the typing must never be seen.
                var cover = _cover ??= await Cover.ShowAsync(this, OperationBounds(), Loc.Get("SigningOut"), Session.Title, 120, MessageMonitor ?? StageBounds());
                if (_closing) return; // closed while the cover faded in: the finally lifts it
                EnsureCovers(cover);
                var locks = _locks;
                var typed = await Session.TypeSignOutAsync();
                if (!typed && _locks != locks && !_closing && Session.IsReady)
                {
                    Log.Info($"{Session.Title}: the screen was locked during the sign-out: typing it again once unlocked");
                    await WaitUntilUnlockedAsync();
                    await BringForwardAsync(activate: false);
                    EnsureCovers(cover);
                    typed = await Session.TypeSignOutAsync();
                }
                if (!typed && Session.Connection.AzureVm is { } vm && Session.SignedInUser is { } who && !_closing && Session.IsReady)
                {
                    Log.Info($"{Session.Title}: typing the sign-out failed: signing out through Azure");
                    Session.ExpectSignOut();
                    typed = await RemoteSessions.TrySignOutThroughAzureAsync(vm, who);
                    if (!typed) Session.CancelSignOut();
                }
                if (!typed)
                {
                    await HideCoverAsync();
                    if (!_closing) Notification.Problem(this, Loc.Format("ProblemSignOutTitle", Session.Title), Loc.Format("SignOutFailed", Session.Title));
                    return;
                }
                // The window closes with the session; if the server does not end it, say so.
                if (await Task.WhenAny(ended.Task, Task.Delay(20000)) == ended.Task) return;
                Session.CancelSignOut();
                await HideCoverAsync();
                if (!_closing) Notification.Problem(this, Loc.Format("ProblemSignOutTitle", Session.Title), Loc.Format("SignOutNotConfirmed", Session.Title), warning: true);
            }
            finally
            {
                Session.Closed -= OnClosed;
                Session.PropertyChanged -= OnPhase;
                // Also when the typing threw: no cover left over the session, no sign-out intent left armed.
                if (Session.Phase != SessionPhase.Closed) Session.CancelSignOut();
                await HideCoverAsync();
                Session.IsBusy = false;
                _signingOut = false;
            }
        });
    }

    /// <summary>A link opened in this session's browser, the window brought forward to show it.</summary>
    /// <remarks>
    /// RDP has no way to tell the server's Windows to open an address (only keys, mouse, screen and clipboard), so it is
    /// typed into Run: under a cover, as a sign-out's typing, until the browser has come up. Only the browser shows.
    /// </remarks>
    internal Task OpenLinkAsync(string url) => Enqueue(async () =>
    {
        await BringForwardAsync();
        var opened = false;
        var cover = _cover = await Cover.ShowAsync(this, OperationBounds(), Loc.Get("OpeningLink"),
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url, 120, MessageMonitor ?? StageBounds());
        try
        {
            if (_closing) return;
            EnsureCovers(cover);
            opened = await Session.OpenInSessionAsync(url);
            if (opened) await Session.WaitForWindowAsync(4000); // the browser up, not the desktop it opens over
        }
        finally { await HideCoverAsync(); }
        if (!opened && !_closing)
            Notification.Problem(this, Loc.Get("LinkNotOpenedTitle"), Loc.Format("LinkNotOpenedInSession", url, Session.Title));
        Session.FocusRemote();
    });

    // The server switches to its secure desktop through a black frame: faded through the stage color instead.
    internal async Task CtrlAltDelAsync()
    {
        if (!Session.IsReady) return;
        await Enqueue(async () =>
        {
            Session.IsBusy = true;
            var ok = true;
            try
            {
                await BringForwardAsync();
                await CoverWhileAsync(OperationBounds(), async () => ok = await Session.SendCtrlAltDelAsync());
            }
            finally { Session.IsBusy = false; }
            if (!ok && !_closing) Dialog.Show(this, Loc.Get("SendCtrlAltDelFailed"), DialogKind.Warning);
        });
    }

    /// <summary>
    /// Forward, and only once it is really back: right after a restore from the taskbar a minimized window still reports
    /// its off-screen place (or its size is still changing), and a cover measured then went there, leaving the typing seen.
    /// </summary>
    /// <param name="activate">False: only a minimized window is restored (and so activated); one in the background stays there.</param>
    async Task BringForwardAsync(bool activate = true)
    {
        if (activate || WindowState == WindowState.Minimized) Native.Restore(this);
        var last = Rectangle.Empty;
        for (var i = 0; i < 20; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var now = OperationBounds();
            if (WindowState != WindowState.Minimized && now.X > -10000 && now == last) return; // restored and settled
            last = now;
            await Task.Delay(25);
        }
    }

    /// <summary>Right before typing: the cover still over the whole window (it may have moved or grown meanwhile) and on top.</summary>
    void EnsureCovers(Cover cover)
    {
        var target = OperationBounds();
        cover.SetBounds(target);
        if (Native.IsForeground(this)) cover.Raise();
        Log.Info($"{Session.Title}: cover over {target} (window {Native.Bounds(this)}, {WindowState}{(IsFullScreen ? ", full screen" : "")}, " +
                 $"{(Native.IsForeground(this) ? "foreground" : "not foreground")})");
    }

    async Task HideCoverAsync()
    {
        if (_cover is { } cover) await LiftAsync(cover);
    }

    async Task LiftAsync(Cover cover, int fadeMs = 180)
    {
        await cover.HideAsync(fadeMs);
        if (_cover == cover) _cover = null;
    }

    void CtrlAltDel_Click(object sender, RoutedEventArgs e) => _ = CtrlAltDelAsync();

    /// <summary>
    /// What is not in sight in the title bar: the small view, reconnect, Ctrl+Alt+Del, the connection's links, and how
    /// good the link is. Named items, so nothing has to be learned by its icon.
    /// </summary>
    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, SymbolRegular icon, Action run, bool enabled = true)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text, Icon = new SymbolIcon { Symbol = icon }, IsEnabled = enabled };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add(Loc.Get("MiniView"), SymbolRegular.PictureInPicture24, () => MiniView.Toggle(Session));
        Add(Loc.Get("SendCtrlAltDel"), SymbolRegular.ShieldKeyhole24, () => _ = CtrlAltDelAsync(), Session.IsReady);
        Add(Loc.Get("Reconnect"), SymbolRegular.ArrowClockwise24, () => Reconnect_Click(this, new RoutedEventArgs()));
        if (App.Store.LinksOf(Session.Connection) is { Count: > 0 } links)
        {
            var open = new System.Windows.Controls.MenuItem { Header = Loc.Get("OpenLink"), Icon = new SymbolIcon { Symbol = SymbolRegular.Link24 } };
            foreach (var link in LinkMenu.Items(links, Session.Connection)) open.Items.Add(link);
            menu.Items.Add(open);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = Session.NetworkInfo, IsEnabled = false, Icon = new SymbolIcon { Symbol = SymbolRegular.Wifi124 } });
        MoreButton.IsHitTestVisible = false; // a click on it while open only closes the menu
        menu.Closed += (_, _) =>
        {
            MoreButton.IsHitTestVisible = true;
            if (IsActive) Session.FocusRemote();
        };
        menu.IsOpen = true;
    }

    // Next to Ctrl+Alt+Del and sign out: a slip would drop a working session, so that one is asked about first.
    void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Phase == SessionPhase.Ready && !App.Store.Data.Settings.SkipSessionConfirmations
            && !Dialog.Confirm(this, Loc.Format("ConfirmReconnect", Session.Title), Loc.Get("Reconnect"), DialogKind.Warning))
        {
            Session.FocusRemote();
            return;
        }
        Session.ReconnectCommand.Execute(null);
    }

    void SignOut_Click(object sender, RoutedEventArgs e) => _ = SignOutAsync();
    // The session comes forward first: a question owned by a minimized window would stay hidden with it (and the
    // sign-out wait for an answer nobody sees), and one behind other windows would be missed.
    void SignOutFromThumbnail()
    {
        Native.Restore(this);
        Dispatcher.BeginInvoke(() => _ = SignOutAsync(), System.Windows.Threading.DispatcherPriority.Background);
    }
    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    public void ToggleFullScreen() => RequestFullScreen(!_wantFullScreen);
    void CloseWindow_Click(object sender, RoutedEventArgs e) => RequestClose();

    // WPF throws on Close() while a Closing handler runs (the disconnect question is open): that window closes itself
    // once the question is answered.
    void RequestClose()
    {
        if (!_asking) Close();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // with Alt held WPF reports Key.System
        if (key == Key.F11) RequestFullScreen(!_wantFullScreen);
        // Ctrl+Alt+Left / Right while the window chrome has the keyboard; in the session the control raises them itself.
        else if (key is Key.Left or Key.Right && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            App.Sessions.SwitchFrom(Session, key == Key.Left ? -1 : 1);
        else return;
        e.Handled = true;
    }

    // ---------- Closing ----------

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Session.Phase == SessionPhase.Closed) return; // the session is gone: let the window go
        e.Cancel = true;
        if (_closing) return;
        if (Session.IsOpen && !_confirmed && !App.IsExiting && !App.Store.Data.Settings.SkipSessionConfirmations)
        {
            bool ok;
            _asking = true;
            try { ok = Dialog.Confirm(this, Loc.Format("ConfirmDisconnect", Session.Title), Loc.Get("Disconnect"), DialogKind.Warning); }
            finally { _asking = false; }
            if (Session.Phase == SessionPhase.Closed)
            {
                e.Cancel = false; // ended while asking (a sign-out completed): its own Close was skipped
                return;
            }
            if (!ok)
            {
                Session.FocusRemote();
                return;
            }
        }
        // Gone at once, still in full screen if it was: the window has to outlive the disconnect (destroying it would
        // destroy the ActiveX mid-teardown), so it is hidden until the session reports back.
        _closing = true;
        SavePlacement();
        HideOverlays();
        Hide();
        Session.Close();
    }

    /// <summary>Disconnects and closes without asking (the caller already asked).</summary>
    internal void Disconnect()
    {
        _confirmed = true;
        RequestClose();
    }

    void Shutdown_Click(object sender, RoutedEventArgs e) => AzureShutdown.ShowVmMenu((UIElement)sender, this, Session);

    void OnSessionClosed(RdpSession s)
    {
        s.Connection.PropertyChanged -= OnConnectionChanged;
        if (!_closing) SavePlacement(); // signed out, or ended from the server side
        _closing = true;
        HideOverlays();
        Hide();
        Dispatcher.BeginInvoke(RequestClose); // may be raised from inside OnClosing (session that was not connected)
    }

    void SavePlacement()
    {
        if (!IsLoaded) return;
        var placement = IsFullScreen ? RestorePlacement() : Native.GetPlacement(this);
        App.Store.Data.Settings.SessionPlacement = placement; // size for connections without their own placement
        if (App.Store.Data.Connections.Contains(Session.Connection)) Session.Connection.Placement = placement;
    }
}
