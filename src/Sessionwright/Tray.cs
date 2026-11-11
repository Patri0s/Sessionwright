using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Sessionwright;

/// <summary>
/// Sessionwright in the notification area, started with Windows (an option in Settings): closing the main window then keeps
/// it running, so Azure VMs start on time in the morning and the quick-connect key works.
/// </summary>
static class Tray
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "Sessionwright";
    /// <summary>Started by Windows at sign-in: straight to the tray, no window.</summary>
    public const string BackgroundArgument = "--background";
    static WinForms.NotifyIcon? _icon;
    static bool _hinted;

    public static bool Enabled => App.Store.Data.Settings.RunInTray;

    /// <summary>Icon and the Windows start entry as the setting says.</summary>
    public static void Apply(MainWindow main)
    {
        SetStartWithWindows(Enabled);
        if (Enabled && _icon == null)
        {
            _icon = new WinForms.NotifyIcon
            {
                Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null,
                Text = "Sessionwright",
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) main.ShowFromTray();
                else if (e.Button == WinForms.MouseButtons.Right) ShowMenu(main);
            };
        }
        else if (!Enabled && _icon != null)
        {
            Dispose();
            if (!main.IsVisible) main.ShowFromTray(); // no other way back to a hidden window
        }
    }

    /// <summary>Once per run, the first time the window goes to the tray: where Sessionwright went.</summary>
    public static void HintOnce()
    {
        if (_hinted || _icon == null) return;
        _hinted = true;
        _icon.ShowBalloonTip(5000, "Sessionwright", Loc.Get("TrayHint"), WinForms.ToolTipIcon.None);
    }

    /// <summary>
    /// A native menu like those of Windows' own tray icons (rounded, system font), in the light / dark mode Sessionwright uses:
    /// App.SyncNativeMenus sets the process's menu mode. A WinForms menu looked like neither.
    /// </summary>
    static void ShowMenu(MainWindow main)
    {
        const uint Open = 1, Exit = 2, FirstFavorite = 100;
        var owner = Native.Handle(main);
        var menu = CreatePopupMenu();
        try
        {
            // The favorites, to connect without opening the window first (the first ten, in their order).
            var favorites = App.Store.Favorites().Take(10).ToList();
            for (var i = 0; i < favorites.Count; i++)
                AppendMenu(menu, 0, FirstFavorite + (uint)i, favorites[i].Title.Replace("&", "&&"));
            if (favorites.Count > 0) AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
            AppendMenu(menu, 0 /* MF_STRING */, Open, Loc.Get("TrayOpen"));
            AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
            AppendMenu(menu, 0, Exit, Loc.Get("TrayExit"));
            SetMenuDefaultItem(menu, Open, 0); // bold, like a click on the icon
            GetCursorPos(out var at);
            // Without the foreground the menu would not close on a click elsewhere (documented for TrackPopupMenu).
            SetForegroundWindow(owner);
            var chosen = TrackPopupMenuEx(menu, 0x0100 /* RETURNCMD */ | 0x0080 /* NONOTIFY */ | 0x0002 /* RIGHTBUTTON */ | 0x0020 /* BOTTOMALIGN */,
                at.X, at.Y, owner, IntPtr.Zero);
            PostMessage(owner, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            if (chosen == Open) main.ShowFromTray();
            else if (chosen == Exit) main.ExitFromTray();
            else if (chosen >= FirstFavorite && chosen - FirstFavorite < favorites.Count) main.ConnectById(favorites[(int)(chosen - FirstFavorite)].Id);
        }
        finally { DestroyMenu(menu); }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    static void SetStartWithWindows(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on && Environment.ProcessPath is { } exe) key.SetValue(RunValue, $"\"{exe}\" {BackgroundArgument}");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Error(ex, "Start with Windows");
        }
    }
}
