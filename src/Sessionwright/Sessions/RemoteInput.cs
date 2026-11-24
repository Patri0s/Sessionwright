using System.Runtime.InteropServices;

namespace Sessionwright;

/// <summary>
/// Keys for the remote session, as set-1 scan codes for <see cref="RdpSession"/>'s SendKeys: the RDP control passes them
/// straight into the session (IMsRdpClientNonScriptable::SendKeys), so they never reach another window, and Ctrl+Alt+Del
/// arrives as the real secure-attention sequence. Measured on DEV-12: the key data is the bare scan code with 0x100 for
/// an extended key (the documented lParam layout delivers nothing), and the control refuses (E_FAIL) without keyboard focus.
/// </summary>
static class RemoteInput
{
    public const int Extended = 0x100;
    public const int Ctrl = 0x1D, Shift = 0x2A, Alt = 0x38, Enter = 0x1C, R = 0x13, V = 0x2F;
    public const int WinKey = 0x5B | Extended, Delete = 0x53 | Extended;

    public readonly record struct Key(int Code, bool Up);

    /// <summary>Presses the keys in order and releases them in reverse.</summary>
    public static Key[] Chord(params int[] codes) =>
        [.. codes.Select(c => new Key(c, false)), .. codes.Reverse().Select(c => new Key(c, true))];

    /// <summary>
    /// Text as key presses. The session uses the client's keyboard layout, so each character is mapped to the physical key
    /// (and Shift / AltGr) of the current local layout: "logoff" or ':' sit on different keys per layout. Null for a
    /// character the layout cannot type (e.g. Latin letters on a Cyrillic layout).
    /// </summary>
    public static Key[]? Text(string text)
    {
        var layout = GetKeyboardLayout(0);
        var keys = new List<Key>();
        foreach (var ch in text)
        {
            var key = VkKeyScanEx(ch, layout);
            if (key == -1)
            {
                Log.Warn($"Remote input: '{ch}' cannot be typed on the current keyboard layout.");
                return null;
            }
            var modifiers = (key >> 8) & 0xFF;
            var chord = new List<int>();
            if ((modifiers & 2) != 0) chord.Add(Ctrl); // with Alt = AltGr
            if ((modifiers & 4) != 0) chord.Add(Alt);
            if ((modifiers & 1) != 0) chord.Add(Shift);
            chord.Add((int)MapVirtualKeyEx((uint)(key & 0xFF), 0 /* MAPVK_VK_TO_VSC */, layout));
            keys.AddRange(Chord([.. chord]));
        }
        return [.. keys];
    }

    /// <summary>The window of the ActiveX that receives keyboard input (the one a mouse click focuses).</summary>
    public static IntPtr InputWindow(IntPtr control)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(control, (h, _) =>
        {
            if (ClassName(h) != "IHWindowClass") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void Focus(IntPtr window) => SetFocus(window);

    /// <summary>
    /// A left click at a point of the screen (physical pixels), into the control's input window: the control sends it to
    /// the server as the user's own click there, scaled as the session is shown. False when the point is not on it.
    /// </summary>
    public static bool Click(IntPtr window, System.Drawing.Point screen)
    {
        var p = new POINT { X = screen.X, Y = screen.Y };
        if (window == IntPtr.Zero || !ScreenToClient(window, ref p) || !GetClientRect(window, out var r) || p.X < 0 || p.Y < 0 || p.X >= r.Right || p.Y >= r.Bottom)
            return false;
        var at = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        const int MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftButton = 1;
        SendMessage(window, MouseMove, IntPtr.Zero, at);
        SendMessage(window, LeftDown, (IntPtr)LeftButton, at);
        SendMessage(window, LeftUp, IntPtr.Zero, at);
        return true;
    }

    struct POINT { public int X, Y; }
    struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int message, IntPtr w, IntPtr l);

    static string ClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanEx(char ch, IntPtr layout);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
}
