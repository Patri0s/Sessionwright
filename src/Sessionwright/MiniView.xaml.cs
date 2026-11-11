using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>
/// A small always-on-top window with a session's live picture: to keep an eye on a long build or a database
/// synchronization while working elsewhere. One per session, opened and closed from the session's title bar or its tile on
/// the home page; a double-click brings the session forward. It only shows: nothing in it reaches the session.
/// </summary>
/// <remarks>
/// The picture is DWM's own of the session window (see <see cref="LiveThumbnail"/>), so it costs nothing to draw and is
/// as fresh as the session window itself: a minimized session window has no picture at all.
/// </remarks>
public partial class MiniView : Window
{
    static readonly Dictionary<RdpSession, MiniView> Open = [];
    readonly RdpSession _session;

    /// <summary>Opens the session's small view, or closes the one that is open.</summary>
    public static void Toggle(RdpSession session)
    {
        if (Open.TryGetValue(session, out var open))
        {
            open.Close();
            return;
        }
        if (session.Phase == SessionPhase.Closed) return;
        new MiniView(session).Show();
    }

    /// <summary>
    /// Back above everything, without taking the keyboard: a full-screen session is itself "always on top" while it is
    /// active and went over the small views (of this and of other sessions) each time it came forward.
    /// </summary>
    public static void RaiseAll()
    {
        foreach (var view in Open.Values)
            SetWindowPos(Native.Handle(view), -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | 0x0010 /* NOACTIVATE */);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    MiniView(RdpSession session)
    {
        InitializeComponent();
        _session = session;
        DataContext = session;
        Open[session] = this;
        session.Closed += OnSessionClosed;
        SourceInitialized += (_, _) =>
        {
            var round = 2; // DWMWCP_ROUND: Windows 11 rounds it and draws its border and shadow
            DwmSetWindowAttribute(Native.Handle(this), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
        // Where the last one was closed; each further one a step up and left of it, so none hides another.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var step = (int)(28 * Native.DpiOf(this) / 96.0) * (Open.Count - 1);
        var place = App.Store.Data.Settings.MiniViewPlacement is { } saved && Native.IsOnScreen(saved) ? saved : Corner();
        Native.SetPlacement(this, new WindowPlacement { X = place.X - step, Y = place.Y - step, Width = place.Width, Height = place.Height }, show: false);
        Closing += (_, _) => App.Store.Data.Settings.MiniViewPlacement = Native.GetPlacement(this); // saved with the next save
        Closed += (_, _) =>
        {
            _session.Closed -= OnSessionClosed;
            Open.Remove(_session);
        };
    }

    /// <summary>The bottom right corner of the main monitor's work area.</summary>
    WindowPlacement Corner()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var scale = Native.DpiOf(this) / 96.0;
        int width = (int)(Width * scale), height = (int)(Height * scale), gap = (int)(16 * scale);
        return new WindowPlacement { X = area.Right - width - gap, Y = area.Bottom - height - gap, Width = width, Height = height };
    }

    void OnSessionClosed(RdpSession s) => Dispatcher.BeginInvoke(Close);

    // The whole window drags (it has no title bar); a double-click goes to the session itself.
    void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Sessions.Show(_session);
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { } // the button went up before the drag began
    }

    void Show_Click(object sender, RoutedEventArgs e) => App.Sessions.Show(_session);
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
