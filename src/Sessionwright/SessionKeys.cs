using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Sessionwright;

/// <summary>
/// Ctrl+Alt+Left / Right in a session window switch to the previous / next open session. While the RDP control has the
/// keyboard it takes every key with a low-level hook of its own and sends it to the server (also these two: its
/// "focus release" event did not come for physical keys on DEV-12/13). Low-level hooks run newest first, and the
/// control sets its hook when it gets the keyboard focus, so ours is set again right after that: on every focus change
/// into the control (a WinEvent) and on every activation of a session window.
/// </summary>
static class SessionKeys
{
    const int WhKeyboardLl = 13, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmKeyUp = 0x0101, WmSysKeyUp = 0x0105, LlkhfInjected = 0x10;
    const int VkLeft = 0x25, VkRight = 0x27, VkDown = 0x28, VkReturn = 0x0D, VkCancel = 0x03, VkPause = 0x13, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    const uint EventObjectFocus = 0x8005, WinEventOutOfContext = 0;

    static IntPtr _hook, _focusHook;
    static HookProc? _proc; // referenced: the native side holds none
    static WinEventProc? _focusProc;
    static bool _logged, _returnHeld;

    /// <summary>-1 = previous, +1 = next; raised on the UI thread with the session window that had the keyboard.</summary>
    public static event Action<SessionWindow, int>? Switch;

    /// <summary>Ctrl+Alt+Down in a session window: the switcher with all open sessions.</summary>
    public static event Action<SessionWindow>? Overview;

    /// <summary>Watches focus changes in this process (once) and sets the keyboard hook again.</summary>
    public static void Start()
    {
        if (_focusHook == IntPtr.Zero)
        {
            _focusProc = OnFocus;
            _focusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, IntPtr.Zero, _focusProc, (uint)Environment.ProcessId, 0, WinEventOutOfContext);
            // Another program in front: no hook of ours in its typing (see Reinstall).
            Application.Current.Deactivated += (_, _) => Reinstall(false);
        }
        Reinstall(true);
    }

    // The control handles the focus message first and sets its hook then: ours goes after it, once that is done.
    static void OnFocus(IntPtr hook, uint e, IntPtr hwnd, int obj, int child, uint thread, uint time) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Reinstall(false), System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>
    /// Set only while a session window is in front (<paramref name="force"/>: one is just being activated). A low-level
    /// hook runs on this (UI) thread for every key in every program, so while the thread is busy (the RDP control
    /// resizing takes hundreds of ms) typing anywhere would stall, and Windows drops a hook that answers late.
    /// </summary>
    static void Reinstall(bool force)
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        if (!force && ForegroundSession() == null) return;
        _proc ??= Proc;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero && !_logged)
        {
            _logged = true;
            Log.Warn($"Session switch keys unavailable (error {Marshal.GetLastWin32Error()})");
        }
    }

    // ---------- Quick connect: a global hotkey (RegisterHotKey); the hook below catches it while a session has the keyboard ----------

    const int HotkeyId = 0x5248, WmHotkey = 0x0312;
    static KeyGesture? _quickConnect;
    static (Window Window, string? Text)? _registered;

    /// <summary>The quick-connect key was pressed (anywhere): Sessionwright forward with the search ready.</summary>
    public static event Action? QuickConnectPressed;

    /// <summary>Registers <paramref name="text"/> ("Ctrl+Alt+F"; empty = none) for <paramref name="window"/>; false when another program has it.</summary>
    public static bool SetQuickConnect(Window window, string? text)
    {
        _registered = (window, text);
        var hwnd = Native.Handle(window);
        UnregisterHotKey(hwnd, HotkeyId);
        _quickConnect = ParseKey(text);
        if (_quickConnect is not { } key) return true;
        var mods = (key.Modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (key.Modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0)
                   | (key.Modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (key.Modifiers.HasFlag(ModifierKeys.Windows) ? 8u : 0) | 0x4000 /* NOREPEAT */;
        if (RegisterHotKey(hwnd, HotkeyId, mods, (uint)KeyInterop.VirtualKeyFromKey(key.Key))) return true;
        Log.Warn($"Quick connect key {text} is taken by another program (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    /// <summary>
    /// Off while a combination is being typed into the settings: otherwise pressing the current key there would bring
    /// up the search instead of setting it. On again (as it was saved) afterwards.
    /// </summary>
    public static void SuspendQuickConnect(bool suspend)
    {
        if (_registered is not { } r) return;
        if (!suspend)
        {
            SetQuickConnect(r.Window, r.Text);
            return;
        }
        UnregisterHotKey(Native.Handle(r.Window), HotkeyId);
        _quickConnect = null;
    }

    public static bool IsQuickConnectMessage(uint msg, IntPtr wParam) => msg == WmHotkey && wParam == HotkeyId;

    public static KeyGesture? ParseKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return new KeyGestureConverter().ConvertFromInvariantString(text) as KeyGesture; }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return null; }
    }

    public static string FormatKey(KeyGesture key) => key.GetDisplayStringForCulture(CultureInfo.InvariantCulture);

    static ModifierKeys ModifiersDown() =>
        (Down(VkControl) ? ModifierKeys.Control : 0) | (Down(VkMenu) ? ModifierKeys.Alt : 0) | (Down(VkShift) ? ModifierKeys.Shift : 0)
        | (Down(0x5B) || Down(0x5C) ? ModifierKeys.Windows : 0);

    // Runs on the UI thread (the one that set it), within the system's hook time limit: no waiting here.
    static IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        // The RDP control's own hook would send the quick-connect key to the server, before the hotkey sees it.
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown) && _quickConnect is { } quick
            && Marshal.ReadInt32(lParam) == KeyInterop.VirtualKeyFromKey(quick.Key) && ModifiersDown() == quick.Modifiers && ForegroundSession() != null)
        {
            Log.Info($"Quick connect key in a session");
            Application.Current.Dispatcher.BeginInvoke(() => QuickConnectPressed?.Invoke());
            return 1;
        }
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown) && Marshal.ReadInt32(lParam) == VkDown
            && Down(VkControl) && Down(VkMenu) && !Down(VkShift) && ForegroundSession() is { } current)
        {
            Application.Current.Dispatcher.BeginInvoke(() => Overview?.Invoke(current));
            return 1; // not to the session
        }
        // Ctrl+Alt+Enter (as in VM consoles) instead of the control's own Ctrl+Alt+Break: most keyboards have no Break,
        // so that one is taken away (Ctrl turns Break into Cancel), one key to know. Held down it repeats: one toggle per press.
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown) && Marshal.ReadInt32(lParam) is VkCancel or VkPause
            && Down(VkControl) && Down(VkMenu) && ForegroundSession() != null)
            return 1;
        if (code >= 0 && (wParam == WmKeyUp || wParam == WmSysKeyUp) && Marshal.ReadInt32(lParam) == VkReturn) _returnHeld = false;
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown) && Marshal.ReadInt32(lParam) == VkReturn
            && Down(VkControl) && Down(VkMenu) && !Down(VkShift) && ForegroundSession() is { } full)
        {
            if (!_returnHeld) Application.Current.Dispatcher.BeginInvoke(full.ToggleFullScreen);
            _returnHeld = true;
            return 1; // not to the session
        }
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown) && Marshal.ReadInt32(lParam) is var vk && vk is VkLeft or VkRight
            && Down(VkControl) && Down(VkMenu) && !Down(VkShift) && ForegroundSession() is { } window)
        {
            var physical = (Marshal.ReadInt32(lParam, 8) & LlkhfInjected) == 0;
            Log.Info($"{window.Session.Title}: Ctrl+Alt+{(vk == VkLeft ? "Left" : "Right")} ({(physical ? "keyboard" : "injected")})");
            Application.Current.Dispatcher.BeginInvoke(() => Switch?.Invoke(window, vk == VkLeft ? -1 : 1));
            return 1; // not to the session
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>The session window in the foreground (also when its full-screen bar has the keyboard), or null.</summary>
    // No foreground window at all is an ordinary moment (one window losing the activation before the next has it), and
    // HwndSource.FromHwnd throws on a zero handle: asked on every focus change, that was an error dialog many times a day.
    static SessionWindow? ForegroundSession() => GetForegroundWindow() is var foreground && foreground == IntPtr.Zero ? null : HwndSource.FromHwnd(foreground)?.RootVisual switch
    {
        SessionWindow w => w,
        FullScreenBar { Owner: SessionWindow w } => w,
        _ => null,
    };

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint e, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
