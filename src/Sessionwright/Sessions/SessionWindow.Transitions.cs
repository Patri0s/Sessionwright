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

// Covered changes of what the window shows: full screen, the desktop's size, reconnecting. Part of SessionWindow (see SessionWindow.xaml.cs).
public partial class SessionWindow
{
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
            catch (Exception ex) { Log.Error(ex, $"{Session.Connection.Title}: transition failed"); }
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
        using var _ = Log.Time($"{Session.Connection.Title}: enter full screen");
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
        using var _ = Log.Time($"{Session.Connection.Title}: exit full screen");
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
            // Said, since the bar's own hint promises the session follows it: without a word it looked broken.
            Log.Info($"{Session.Connection.Title}: the bar was dropped on a monitor the session does not use");
            Notification.Show(this, Wpf.Ui.Controls.SymbolRegular.Desktop24, Loc.Get("BarMonitorNotUsed"), Loc.Get("BarMonitorNotUsedHint"), TimeSpan.FromSeconds(12));
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
        Log.Info($"{Session.Connection.Title}: monitors changed, full screen on {monitor} (was {old})");
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
        // Active is not enough: keys typed into the session while the user is in another program (a sign-out going on)
        // make this window the active one of the app, with that program still in front. Counted as in front, it went
        // "always on top" there, over its own cover: the typing showed.
        var topmost = IsFullScreen && (IsActive || OwnedWindows.Cast<Window>().Any(w => w.IsActive)) && !Native.IsBehindAnotherProgram(this);
        if (!topmost && Topmost) Native.DropBelowForeground(this); // else it would cover the window just activated
        Topmost = topmost;
        _cover?.StayAbove(); // whatever just happened to this window's place among the others
        if (!topmost) return;
        MiniView.RaiseAll(); // the small views stay over a full-screen session too
        Notification.RaiseAll(); // and what the app has to say
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
}
