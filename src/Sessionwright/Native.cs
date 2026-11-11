using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>
/// Window placement in physical pixels. With per-monitor DPI a WPF window's Left/Top/Width/Height are scaled by the
/// DPI of the monitor it is currently on, so placing it on a monitor with another scale via DIPs lands wrong.
/// </summary>
static class Native
{
    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    public static Rectangle Bounds(Window w) => Bounds(Handle(w));

    public static Rectangle Bounds(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>
    /// Moves/sizes the window to exactly <paramref name="r"/>, also across monitors with another scale: Windows would
    /// otherwise answer the DPI change with a linearly scaled rect, which WPF applies after ours (see Subclass).
    /// </summary>
    public static void SetBounds(Window w, Rectangle r)
    {
        var hwnd = Handle(w);
        Exact[hwnd] = r;
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, NoZOrder | NoActivate);
        // WM_DPICHANGED is usually sent within SetWindowPos, but may also arrive right after it.
        w.Dispatcher.BeginInvoke(() => Exact.Remove(hwnd), DispatcherPriority.ApplicationIdle);
    }

    public static void SetPosition(Window w, int x, int y) =>
        SetWindowPos(Handle(w), IntPtr.Zero, x, y, 0, 0, NoZOrder | NoActivate | NoSize);

    static readonly Dictionary<IntPtr, Rectangle> Exact = [];

    /// <summary>Restored bounds (physical screen px) and whether the window is maximized.</summary>
    public static WindowPlacement GetPlacement(Window w)
    {
        var p = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        var hwnd = Handle(w);
        GetWindowPlacement(hwnd, ref p);
        var r = p.NormalPosition;
        // WINDOWPLACEMENT is in workspace coordinates: shifted by a taskbar docked at the top or left of the monitor.
        var offset = WorkspaceOffset(Screen.FromHandle(hwnd));
        (r.Left, r.Top, r.Right, r.Bottom) = (r.Left + offset.X, r.Top + offset.Y, r.Right + offset.X, r.Bottom + offset.Y);
        // Minimized: whether it comes back maximized.
        var maximized = p.ShowCmd == SwShowMaximized || (p.ShowCmd == SwShowMinimized && (p.Flags & WpfRestoreToMaximized) != 0);
        return new WindowPlacement { X = r.Left, Y = r.Top, Width = r.Right - r.Left, Height = r.Bottom - r.Top, Maximized = maximized };
    }

    /// <summary>
    /// Applies a placement from <see cref="GetPlacement"/>: one call that sets the restored bounds and the state, so a
    /// maximized window does not first appear at its restored size. Before the window is shown, only the bounds are
    /// set (it stays hidden; WPF's WindowState applies the maximized state when it is shown).
    /// </summary>
    public static void SetPlacement(Window w, WindowPlacement placement, bool show)
    {
        var offset = WorkspaceOffset(Screen.FromRectangle(new Rectangle(placement.X, placement.Y, placement.Width, placement.Height)));
        var (x, y) = (placement.X - offset.X, placement.Y - offset.Y);
        var p = new WINDOWPLACEMENT
        {
            Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            ShowCmd = !show ? SwHide : placement.Maximized ? SwShowMaximized : SwShowNormal,
            NormalPosition = new RECT { Left = x, Top = y, Right = x + placement.Width, Bottom = y + placement.Height },
        };
        var hwnd = Handle(w);
        Exact[hwnd] = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
        SetWindowPlacement(hwnd, ref p);
        w.Dispatcher.BeginInvoke(() => Exact.Remove(hwnd), DispatcherPriority.ApplicationIdle);
    }

    static System.Drawing.Point WorkspaceOffset(Screen s) => new(s.WorkingArea.X - s.Bounds.X, s.WorkingArea.Y - s.Bounds.Y);

    /// <summary>A monitor may have been unplugged since: only a placement whose title bar is on a monitor is used.</summary>
    public static bool IsOnScreen(WindowPlacement p) =>
        p.Width >= 200 && p.Height >= 100 &&
        Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, new Rectangle(p.X, p.Y, p.Width, 40)) is { Width: >= 100, Height: >= 20 });

    /// <summary>The same restored size on <paramref name="monitor"/>, at the same offset as on the monitor it came from.</summary>
    public static WindowPlacement MoveTo(WindowPlacement p, Screen monitor)
    {
        var from = Screen.FromRectangle(new Rectangle(p.X, p.Y, p.Width, p.Height)).WorkingArea;
        var to = monitor.WorkingArea;
        if (from == to) return p;
        var width = Math.Min(p.Width, to.Width);
        var height = Math.Min(p.Height, to.Height);
        return new WindowPlacement
        {
            X = Math.Clamp(to.X + p.X - from.X, to.Left, to.Right - width),
            Y = Math.Clamp(to.Y + p.Y - from.Y, to.Top, to.Bottom - height),
            Width = width, Height = height, Maximized = p.Maximized,
        };
    }

    /// <summary>
    /// Runs <paramref name="hook"/> before WPF and its chrome see a message (Win32 subclass). Also keeps a window placed
    /// by <see cref="SetBounds"/> or <see cref="SetPlacement"/> at exactly that rect across a DPI change.
    /// </summary>
    public static void Subclass(Window w, Func<IntPtr, uint, IntPtr, IntPtr, IntPtr?>? hook = null)
    {
        SubclassProc proc = (hwnd, msg, wParam, lParam, _, _) =>
        {
            if (msg == WmDpiChanged && Exact.TryGetValue(hwnd, out var r))
                Marshal.StructureToPtr(new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom }, lParam, false);
            return hook?.Invoke(hwnd, msg, wParam, lParam) ?? DefSubclassProc(hwnd, msg, wParam, lParam);
        };
        Procs.Add(w, proc); // the native side holds no reference
        SetWindowSubclass(Handle(w), proc, 1, IntPtr.Zero);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, SubclassProc> Procs = new();

    /// <summary>
    /// Keeps a window invisible until its first frame is rendered: a new window otherwise flashes white (and then black)
    /// before WPF draws it.
    /// </summary>
    public static void HideUntilRendered(Window w)
    {
        w.SourceInitialized += (_, _) => Cloak(w, true);
        w.ContentRendered += (_, _) => w.Dispatcher.BeginInvoke(() => Cloak(w, false), DispatcherPriority.Background);
    }

    public static void Cloak(Window w, bool on)
    {
        var value = on ? 1 : 0;
        DwmSetWindowAttribute(Handle(w), 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
    }

    /// <summary>No Windows 11 accent border: a full-screen window would show it along the monitor edges.</summary>
    public static void SetBorderVisible(Window w, bool visible)
    {
        var color = visible ? unchecked((int)0xFFFFFFFF) /* DWMWA_COLOR_DEFAULT */ : unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */;
        DwmSetWindowAttribute(Handle(w), 34 /* DWMWA_BORDER_COLOR */, ref color, sizeof(int));
    }

    /// <summary>The window, or one of the windows it owns (dialog, full-screen bar), has the keyboard.</summary>
    public static bool IsForeground(Window w)
    {
        var hwnd = Handle(w);
        for (var fg = GetForegroundWindow(); fg != IntPtr.Zero; fg = GetWindow(fg, 4 /* GW_OWNER */))
            if (fg == hwnd) return true;
        return false;
    }

    /// <summary>
    /// Ends "always on top" without raising the window: Topmost = false alone (HWND_NOTOPMOST) puts it above every
    /// normal window, also above the one that was just activated. Placed right below the foreground window instead.
    /// </summary>
    public static void DropBelowForeground(Window w)
    {
        var fg = GetForegroundWindow();
        // No foreground window yet (activation in progress), one of ours, or the desktop (inserting after it would send
        // the window to the bottom): WPF's plain HWND_NOTOPMOST is right then.
        if (fg == IntPtr.Zero || IsForeground(w) || fg == GetShellWindow() || ClassName(fg) is "WorkerW" or "Progman") return;
        SetWindowPos(Handle(w), fg, 0, 0, 0, 0, NoMove | NoSize | NoActivate); // the owned bar moves with its owner
    }

    static string ClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Flashes the window's taskbar button until it is brought forward (nothing if it already is).</summary>
    public static void FlashUntilActive(Window w)
    {
        if (w.IsActive || Handle(w) is var hwnd && hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO { Size = (uint)Marshal.SizeOf<FLASHWINFO>(), Hwnd = hwnd, Flags = 0x2 | 0xC /* TRAY | TIMERNOFG */, Count = uint.MaxValue };
        FlashWindowEx(ref info);
    }

    /// <summary>Restores a minimized window to its previous state (maximized stays maximized) and brings it forward.</summary>
    public static void Restore(Window w)
    {
        if (w.WindowState == WindowState.Minimized) ShowWindow(Handle(w), 9 /* SW_RESTORE */);
        w.Activate();
    }

    public static uint DpiOf(Window w) => GetDpiForWindow(Handle(w));

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint Size; public IntPtr Hwnd; public uint Flags, Count, Timeout; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public POINT MinPosition, MaxPosition;
        public RECT NormalPosition;
    }

    const int SwHide = 0, SwShowNormal = 1, SwShowMinimized = 2, SwShowMaximized = 3, WpfRestoreToMaximized = 2;
    const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010, WmDpiChanged = 0x02E0;

    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, IntPtr data);
    [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
}
