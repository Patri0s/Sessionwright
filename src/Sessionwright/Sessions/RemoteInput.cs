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
    public const int Ctrl = 0x1D, Shift = 0x2A, Alt = 0x38, Enter = 0x1C, R = 0x13;
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
