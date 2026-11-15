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
