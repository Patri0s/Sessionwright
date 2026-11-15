using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>
/// The open sessions at a glance (Ctrl+Alt+Down): a live picture of each, drawn by DWM straight from its window (like
/// Alt+Tab), numbered 1 to 9. A digit, a click or the arrows and Enter bring one forward; Esc or a click elsewhere closes.
/// </summary>
public partial class SessionSwitcher : Window
{
    const double SlotWidth = 288, SlotHeight = 162;
    const int MaxColumns = 3;
    // A tile: the slot, its frame's padding (6), border (2) and margin (2) on both sides.
    const double TileWidth = SlotWidth + 2 * (6 + 2 + 2);
    static SessionSwitcher? _open;

    readonly SessionManager _sessions;
    readonly List<Tile> _tiles = [];
    int _selected;

    sealed record Tile(RdpSession Session, Border Frame);

    SessionSwitcher(SessionManager sessions, List<RdpSession> open, RdpSession? current)
    {
        InitializeComponent();
        _sessions = sessions;
        Tiles.Columns = Math.Min(MaxColumns, open.Count);
        Hint.MaxWidth = Math.Max(2, Tiles.Columns) * TileWidth; // the window as wide as its tiles (two at least, for the hint)
        for (var i = 0; i < open.Count; i++) AddTile(open[i], i);
        _selected = Math.Max(0, open.IndexOf(current!));
        Select(_selected);
        SourceInitialized += (_, _) =>
        {
            var round = 2; // DWMWCP_ROUND: Windows 11 rounds it and draws its border and shadow
            DwmSetWindowAttribute(Native.Handle(this), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
        Closed += (_, _) =>
        {
            if (_open == this) _open = null;
        };
    }

    /// <summary>Opens the switcher over <paramref name="current"/>'s monitor (else the main window's), or closes it.</summary>
    public static void Toggle(SessionManager sessions, RdpSession? current)
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        var open = sessions.Open.Where(s => !s.IsClosing).ToList();
        if (open.Count == 0) return;
        var near = current != null ? sessions.WindowOf(current) : Application.Current.MainWindow;
        var area = (near is { IsVisible: true } ? Screen.FromHandle(Native.Handle(near)) : Screen.PrimaryScreen!).WorkingArea;
        var switcher = _open = new SessionSwitcher(sessions, open, current);
        // On its monitor before it is shown (its scale), centered once its size is known, before the first frame.
        new System.Windows.Interop.WindowInteropHelper(switcher).EnsureHandle();
        Native.SetPosition(switcher, area.Left + area.Width / 3, area.Top + area.Height / 3);
        switcher.Show();
        switcher.UpdateLayout();
        var b = Native.Bounds(switcher);
        Native.SetPosition(switcher, area.Left + (area.Width - b.Width) / 2, area.Top + (area.Height - b.Height) / 2);
        switcher.Activate();
        Log.Info($"Session switcher: {open.Count} sessions");
    }

    void AddTile(RdpSession session, int index)
    {
        // What shows where DWM has nothing to draw (a minimized window).
        var fallback = new TextBlock
        {
            Text = Loc.Get("SwitcherMinimized"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        fallback.SetResourceReference(ForegroundProperty, "TextFillColorTertiaryBrush");
        var slot = new LiveThumbnail { Session = session, Width = SlotWidth, Height = SlotHeight, CornerRadius = new CornerRadius(4), Child = fallback };
        slot.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");

        var number = new TextBlock { Text = index < 9 ? $"{index + 1}" : "", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
        number.SetResourceReference(ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var title = new TextBlock { Text = session.WindowTitle, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
        var status = new TextBlock { Text = session.StatusLine, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        status.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        var caption = new DockPanel { Margin = new Thickness(2, 8, 2, 0), Width = SlotWidth };
        DockPanel.SetDock(number, Dock.Left);
        caption.Children.Add(number);
        caption.Children.Add(new StackPanel { Children = { title, status } });

        var frame = new Border
        {
            Padding = new Thickness(6), Margin = new Thickness(2), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
            Child = new StackPanel { Children = { slot, caption } },
        };
        AutomationProperties.SetName(frame, $"{(index < 9 ? $"{index + 1}. " : "")}{session.WindowTitle}, {session.StatusLine}");
        var i = index;
        frame.MouseLeftButtonUp += (_, _) => SwitchTo(i);
        frame.MouseEnter += (_, _) => Select(i);
        _tiles.Add(new Tile(session, frame));
        Tiles.Children.Add(frame);
    }

    void Select(int index)
    {
        _selected = Math.Clamp(index, 0, _tiles.Count - 1);
        for (var i = 0; i < _tiles.Count; i++)
        {
            var frame = _tiles[i].Frame;
            if (i == _selected)
            {
                frame.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                frame.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            }
            else
            {
                frame.BorderBrush = Brushes.Transparent;
                frame.Background = Brushes.Transparent;
            }
        }
    }

    void SwitchTo(int index)
    {
        if (index < 0 || index >= _tiles.Count) return;
        var session = _tiles[index].Session;
        Close();
        _sessions.Show(session);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var columns = Tiles.Columns;
        switch (key)
        {
            case >= Key.D1 and <= Key.D9: SwitchTo(key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9: SwitchTo(key - Key.NumPad1); break;
            case Key.Left: Select(_selected - 1); break;
            case Key.Right or Key.Tab: Select((_selected + 1) % _tiles.Count); break;
            case Key.Up: Select(_selected - columns); break;
            case Key.Down: Select(_selected + columns); break;
            case Key.Enter or Key.Space: SwitchTo(_selected); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Close(); // a click elsewhere, Alt+Tab
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
