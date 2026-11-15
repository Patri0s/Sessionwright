using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>
/// Connection bar shown over a full-screen session. A separate topmost window, because WPF cannot draw over the
/// RDP HWND. Drop it on another monitor to move the session there; unpinned it slides away like the mstsc bar.
/// </summary>
public partial class FullScreenBar : Window
{
    const double Peek = 4; // visible strip of a hidden bar, so the mouse can bring it back
    const double MinBarWidth = 128, DefaultBarWidth = 500; // DIPs; the minimum is exactly minimize, exit full screen and disconnect
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    Rectangle _monitor; // physical pixels
    bool _pinned = true;
    int _slide; // ignores Completed of a superseded slide animation
    RdpSession? _session;

    public event Action? SignOutClicked, CtrlAltDelClicked, MinimizeClicked, RestoreClicked, CloseClicked, FocusReturned;
    public event Action<RdpSession>? SessionChosen;
    /// <summary>The bar was dropped after a drag; the monitor under its center.</summary>
    public event Action<Rectangle>? Dropped;

    public FullScreenBar()
    {
        InitializeComponent();
        // Windows 11 rounds the corners and draws its own border + shadow for the opaque window.
        SourceInitialized += (_, _) =>
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Native.Handle(this), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
        UpdatePinIcon();
        SetWidth(App.Store.Data.Settings.FullScreenBarWidth is > 0 and var saved ? saved : DefaultBarWidth);
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            SlideTo(HiddenTop);
        };
    }

    /// <summary>Shows <paramref name="session"/>; with several open sessions the title becomes a menu to switch.</summary>
    public void Update(RdpSession session)
    {
        if (_session != session)
        {
            if (_session != null)
            {
                _session.PropertyChanged -= OnSessionChanged;
                _session.Connection.PropertyChanged -= OnConnectionChanged;
            }
            else App.Sessions.Open.CollectionChanged += OnOpenChanged; // the switcher appears with a second session
            _session = session;
            session.PropertyChanged += OnSessionChanged;
            session.Connection.PropertyChanged += OnConnectionChanged;
        }
        TitleText.Text = session.Title;
        Signal.Level = session.Quality;
        Signal.ToolTip = session.NetworkInfo;
        CtrlAltDelButton.IsEnabled = SignOutButton.IsEnabled = session.IsReady;
        if (session.Connection.AzureVm != null)
        {
            var (icon, text, tip) = AzureShutdown.Indicator(session);
            ShutdownIcon.Symbol = icon;
            ShutdownText.Text = text;
            ShutdownText.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
            ShutdownButton.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(ShutdownButton, tip);
        }
        var color = new HexBrushConverter().Convert(session.Connection.EffectiveColor, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture) as Brush;
        Panel.BorderBrush = color;
        Panel.BorderThickness = new Thickness(0, 0, 0, color == null ? 0 : 3);
        var many = App.Sessions.Open.Count > 1;
        Chevron.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        TitleButton.IsHitTestVisible = many; // single session: the title area drags the bar like the rest of it
        TitleButton.ToolTip = many ? Loc.Get("OpenSessionsTip") : null;
        SetWidth(Panel.Width); // the chevron needs room too
    }

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RdpSession.Quality) or nameof(RdpSession.NetworkInfo) or nameof(RdpSession.IsReady) or nameof(RdpSession.Title)
            or nameof(RdpSession.ShutDownVmAfter))
            Update(_session!);
    }

    void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Node.EffectiveColor) or nameof(Connection.VmShutdownAt) or nameof(Connection.VmState)) Update(_session!);
    }

    void OnOpenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update(_session!);

    /// <summary>Bar center in physical pixels (used to find the monitor it was dropped on).</summary>
    System.Drawing.Point CenterPx
    {
        get
        {
            var b = Native.Bounds(this);
            return new(b.X + b.Width / 2, b.Y + b.Height / 2);
        }
    }

    // Top is in DIPs of the monitor the bar is on; the monitor edge is known in pixels.
    double VisibleTop => _monitor.Top / VisualTreeHelper.GetDpi(this).DpiScaleY;

    double HiddenTop => VisibleTop - ActualHeight + Peek;

    /// <summary>
    /// At the top edge of <paramref name="monitor"/>: where it was last dragged to (centered until then), or keeping its
    /// horizontal position.
    /// </summary>
    public void Dock(Rectangle monitor, bool center)
    {
        _monitor = monitor;
        StopSlide();
        if (!IsVisible)
        {
            // Positioned before it is shown: no frame at the default position first.
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            Native.SetPosition(this, monitor.Left, monitor.Top);
            Show();
        }
        UpdateLayout();
        Place(center);
        // Moving to a monitor with another scale resizes the bar (SizeToContent): place it again at its final width.
        Dispatcher.BeginInvoke(() => Place(center), DispatcherPriority.Loaded);
        if (!_pinned) _hideTimer.Start();
    }

    void Place(bool center)
    {
        var b = Native.Bounds(this);
        var x = center ? _monitor.Left + (int)(_monitor.Width * (App.Store.Data.Settings.FullScreenBarCenter ?? 0.5)) - b.Width / 2 : b.X;
        Native.SetPosition(this, Math.Clamp(x, _monitor.Left, Math.Max(_monitor.Left, _monitor.Right - b.Width)), _monitor.Top);
    }

    void SlideTo(double top)
    {
        var id = ++_slide;
        var anim = Motion.To(top, 200);
        anim.Completed += (_, _) =>
        {
            if (id != _slide) return;
            StopSlide();
            Top = top;
        };
        BeginAnimation(TopProperty, anim);
    }

    // An animation keeps owning Top; release it so DragMove / Dock can set Top again.
    void StopSlide()
    {
        _slide++;
        var current = Top;
        BeginAnimation(TopProperty, null);
        Top = current;
    }

    void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RestoreClicked?.Invoke();
            return;
        }
        _hideTimer.Stop();
        StopSlide();
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        // Only the bar moves while dragging; the session follows once it is dropped (behind a cover).
        var target = Screen.FromPoint(CenterPx).Bounds;
        Dock(target, center: false);
        // Kept for the next time it shows (after a minimize, in another session): saved with the next save.
        if (_monitor.Width > 0) App.Store.Data.Settings.FullScreenBarCenter = Math.Clamp((CenterPx.X - _monitor.Left) / (double)_monitor.Width, 0, 1);
        Dropped?.Invoke(target);
        FocusReturned?.Invoke();
    }

    /// <summary>
    /// Narrower, the bar drops what matters least first: the signal bars, the pin, the title, Ctrl+Alt+Del, sign out.
    /// Minimize, exit full screen and disconnect always stay; the title takes whatever width is left.
    /// </summary>
    void SetWidth(double width)
    {
        Panel.Width = Math.Max(MinBarWidth, width);
        const double Button = 40, TitleMin = 60;
        var rest = Panel.Width - 8 /* padding */ - 3 * Button /* the buttons that stay */;
        var dropping = false; // once something is dropped, everything less important goes too
        bool Fit(FrameworkElement element, double need)
        {
            var fits = !dropping && rest >= need;
            dropping = !fits;
            element.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
            if (fits) rest -= need;
            return fits;
        }
        Fit(SignOutButton, Button);
        Fit(CtrlAltDelButton, Button);
        // Next to sign out in importance: an unnoticed shutdown ends the session.
        if (_session?.Connection.AzureVm != null) Fit(ShutdownButton, _session.Connection.VmShutdownAt != null ? 72 : 32);
        else ShutdownButton.Visibility = Visibility.Collapsed;
        if (_session != null && App.Store.LinksOf(_session.Connection).Count > 0) Fit(LinksButton, Button);
        else LinksButton.Visibility = Visibility.Collapsed;
        Fit(MiniButton, Button);
        var title = Fit(TitleButton, TitleMin + 20 /* its padding */ + 24 /* drag room around it */ + (Chevron.Visibility == Visibility.Visible ? 24 : 0));
        var whole = 0.0; // the rest of the full title: before the pin and the signal bars get any room
        if (title)
        {
            TitleText.MaxWidth = double.PositiveInfinity;
            TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            whole = Math.Clamp(TitleText.DesiredSize.Width - TitleMin, 0, rest);
            rest -= whole;
        }
        Fit(PinButton, Button);
        Fit(Signal, 18);
        if (title) TitleText.MaxWidth = TitleMin + whole + rest;
    }

    // Resizing by an edge: both edges move, so the bar stays centered where it is (it is docked centered).
    double _gripStartX, _gripStartWidth;
    int _gripCenter;

    void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _hideTimer.Stop();
        StopSlide();
        _gripStartX = PointToScreen(e.GetPosition(this)).X; // physical px
        _gripStartWidth = Panel.Width;
        var b = Native.Bounds(this);
        _gripCenter = b.X + b.Width / 2;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true; // not a drag of the whole bar
    }

    void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!((UIElement)sender).IsMouseCaptured) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var moved = (PointToScreen(e.GetPosition(this)).X - _gripStartX) / scale * (sender == RightGrip ? 2 : -2);
        var width = Math.Clamp(_gripStartWidth + moved, MinBarWidth, Math.Max(MinBarWidth, _monitor.Width / scale));
        if (Math.Abs(width - Panel.Width) < 0.5) return;
        SetWidth(width);
        UpdateLayout();
        var b = Native.Bounds(this);
        var x = Math.Clamp(_gripCenter - b.Width / 2, _monitor.Left, Math.Max(_monitor.Left, _monitor.Right - b.Width));
        Native.SetPosition(this, x, b.Y);
    }

    void Grip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!((UIElement)sender).IsMouseCaptured) return;
        ((UIElement)sender).ReleaseMouseCapture();
        App.Store.Data.Settings.FullScreenBarWidth = Panel.Width; // saved with the next save; new bars open at it
        if (!_pinned && !IsMouseOver) _hideTimer.Start();
        FocusReturned?.Invoke();
        e.Handled = true;
    }

    void Title_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = TitleButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        RdpSession? chosen = null;
        foreach (var s in App.Sessions.Open)
        {
            var item = new System.Windows.Controls.MenuItem { Header = s.Title, IsChecked = s == _session };
            item.Click += (_, _) => chosen = s;
            menu.Items.Add(item);
        }
        TitleButton.IsHitTestVisible = false; // a click on it while open only closes the menu
        menu.Closed += (_, _) =>
        {
            TitleButton.IsHitTestVisible = App.Sessions.Open.Count > 1;
            // Once the item's click is in (it can follow the close): either the chosen session comes forward, or the
            // keyboard goes back to this one. Both took the foreground straight back from the chosen window.
            Dispatcher.BeginInvoke(() =>
            {
                if (chosen != null && chosen != _session) SessionChosen?.Invoke(chosen);
                else if (IsActive) FocusReturned?.Invoke(); // see Shutdown_Click
            }, DispatcherPriority.Background);
        };
        menu.IsOpen = true;
    }

    void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        if (Top < VisibleTop - 0.5) SlideTo(VisibleTop);
    }

    void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_pinned) _hideTimer.Start();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        UpdatePinIcon();
        if (!_pinned && !IsMouseOver) _hideTimer.Start();
        FocusReturned?.Invoke(); // the click activated the bar; typing should go on in the session
    }

    void UpdatePinIcon()
    {
        PinButton.Icon = new SymbolIcon { Symbol = _pinned ? SymbolRegular.Pin24 : SymbolRegular.PinOff24 };
        PinButton.ToolTip = Loc.Get(_pinned ? "UnpinBar" : "PinBar");
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        if (_session != null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Connection.PropertyChanged -= OnConnectionChanged;
            App.Sessions.Open.CollectionChanged -= OnOpenChanged;
        }
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        _hideTimer.Stop(); // the bar stays while the menu is open
        if (_session != null) AzureShutdown.ShowVmMenu(ShutdownButton, this, _session, () =>
        {
            if (!_pinned && !IsMouseOver) _hideTimer.Start();
            // Closed by a click elsewhere (another window, another monitor): the keyboard stays there. Taking it back made
            // the session jump forward over what was clicked, and the bar was lost behind it until the session was clicked.
            if (IsActive) FocusReturned?.Invoke();
        });
    }

    void Links_Click(object sender, RoutedEventArgs e)
    {
        _hideTimer.Stop(); // the bar stays while the menu is open
        if (_session != null) LinkMenu.Show(LinksButton, App.Store.LinksOf(_session.Connection), _session.Connection, () =>
        {
            if (!_pinned && !IsMouseOver) _hideTimer.Start();
            if (IsActive) FocusReturned?.Invoke(); // see Shutdown_Click
        });
    }

    void Mini_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null) MiniView.Toggle(_session);
        FocusReturned?.Invoke(); // the click activated the bar; typing should go on in the session
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeClicked?.Invoke();
    void SignOut_Click(object sender, RoutedEventArgs e) => SignOutClicked?.Invoke();
    void CtrlAltDel_Click(object sender, RoutedEventArgs e) => CtrlAltDelClicked?.Invoke();
    void Restore_Click(object sender, RoutedEventArgs e) => RestoreClicked?.Invoke();
    void Close_Click(object sender, RoutedEventArgs e) => CloseClicked?.Invoke();
}
