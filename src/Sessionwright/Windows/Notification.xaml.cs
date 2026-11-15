using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>
/// Sessionwright's own notification in the bottom-right corner (auto-shutdown warning, a VM started in the morning). Not a
/// Windows toast: Windows holds toasts back while a full-screen app runs (a full-screen session, just when the warning
/// matters), and toasts with buttons would need the WinRT SDK. It never takes the focus: typing stays in the session.
/// </summary>
public partial class Notification : Window
{
    static readonly List<Notification> Shown = [];
    Rectangle _area; // work area of its monitor, physical pixels

    Notification()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = Native.Handle(this);
            SetWindowLong(hwnd, -20 /* GWL_EXSTYLE */, GetWindowLong(hwnd, -20) | 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */);
            var round = 2; // DWMWCP_ROUND: Windows 11 rounds it and draws its border and shadow
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
        Closed += (_, _) =>
        {
            Shown.Remove(this);
            Stack();
        };
    }

    /// <summary>
    /// Something that went on in the background did not work (a machine not started, a sign-out not done): in the
    /// corner, staying until closed, instead of a dialog that stops whatever the user went on to do meanwhile. Dialogs are
    /// for what the user has to decide, or for the answer to what they are waiting on.
    /// </summary>
    public static void Problem(Window? near, string title, string message, bool warning = false) =>
        Show(near, warning ? SymbolRegular.Warning24 : SymbolRegular.ErrorCircle24, title, message);

    /// <summary>
    /// Shows a notification on the monitor of <paramref name="near"/> (else the primary one). Each action closes it
    /// first; the first is the main one. Without <paramref name="autoClose"/> it stays until closed.
    /// </summary>
    public static Notification Show(Window? near, SymbolRegular icon, string title, string message, TimeSpan? autoClose = null,
                                    params (string Text, Action Click)[] actions)
    {
        var n = new Notification();
        n.Glyph.Symbol = icon;
        n.TitleText.Text = title;
        n.MessageText.Text = message;
        System.Windows.Automation.AutomationProperties.SetName(n, $"{title}. {message}");
        foreach (var (text, click) in actions)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 8, 0),
                Appearance = n.Actions.Children.Count == 0 ? ControlAppearance.Primary : ControlAppearance.Secondary,
            };
            button.Click += (_, _) =>
            {
                n.Close();
                click();
            };
            n.Actions.Children.Add(button);
        }
        n.Actions.Visibility = actions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        n._area = (near is { IsVisible: true } ? Screen.FromHandle(Native.Handle(near)) : Screen.PrimaryScreen!).WorkingArea;
        // On its monitor before it is shown (its scale), placed exactly once its height is known, before the first frame.
        new WindowInteropHelper(n).EnsureHandle();
        Native.SetPosition(n, n._area.Right - 420, n._area.Bottom - 200);
        Shown.Add(n);
        n.Show();
        n.UpdateLayout();
        Stack();
        Motion.PlayEntrance(n.Root, 16);
        if (autoClose is { } after)
        {
            var timer = new DispatcherTimer { Interval = after };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (n.IsVisible) n.Close();
            };
            timer.Start();
        }
        Log.Info($"Notification: {title}");
        return n;
    }

    // Newest at the bottom, the older ones above it, per monitor.
    static void Stack()
    {
        foreach (var monitor in Shown.GroupBy(n => n._area))
        {
            var gap = (int)Math.Round(12 * VisualTreeScale(monitor.First()));
            var bottom = monitor.Key.Bottom - gap;
            foreach (var n in monitor.Reverse())
            {
                var b = Native.Bounds(n);
                Native.SetPosition(n, monitor.Key.Right - gap - b.Width, bottom - b.Height);
                bottom -= b.Height + gap;
            }
        }
    }

    static double VisualTreeScale(Window w) => System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
