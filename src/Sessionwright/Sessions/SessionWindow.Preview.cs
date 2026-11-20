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

// What the taskbar shows of the window while a cover hides the session. Part of SessionWindow (see SessionWindow.xaml.cs).
public partial class SessionWindow
{
    // ---------- Taskbar preview while covered ----------
    // A cover is a window of its own, so the taskbar preview (and Peek) of this window showed what the cover hides: the
    // typing of a sign-out, a stretched frame. Meanwhile Windows gets a picture of the cover instead (an iconic preview).

    void SetCoveredPreview(bool on)
    {
        var hwnd = Native.Handle(this);
        if (hwnd == IntPtr.Zero) return;
        var value = on ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 7 /* DWMWA_FORCE_ICONIC_REPRESENTATION */, ref value, sizeof(int));
        DwmSetWindowAttribute(hwnd, 10 /* DWMWA_HAS_ICONIC_BITMAP */, ref value, sizeof(int));
        if (!on) return;
        DwmInvalidateIconicBitmaps(hwnd);
        // Given at once too: during a sign-out of a few seconds Windows did not ask at all (its placeholder showed). It
        // takes a thumbnail it did not ask for up to 200 x 108 (larger ones fail with E_INVALIDARG; measured), and draws
        // it unscaled: anything smaller leaves the preview's frame partly empty.
        SetThumbnail(hwnd, 200, 108);
        SetLivePreview(hwnd);
    }

    // Signing out: what the cover says; Ctrl+Alt+Del (a moment): just the session.
    (string Title, string? Detail) CoverText() =>
        _signingOut ? (Loc.Get("SigningOut"), Session.Title)
        : _openingLink is { } site ? (Loc.Get("OpeningLink"), site)
        : _askingSignOut ? (Session.Title, Loc.Get("SignOutQuestion"))
        : (Session.Title, null);

    /// <summary>The largest picture in the window's proportions within the size given.</summary>
    void SetThumbnail(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        var bounds = Native.Bounds(this);
        var scale = Math.Min(maxWidth / (double)Math.Max(1, bounds.Width), maxHeight / (double)Math.Max(1, bounds.Height));
        var (title, detail) = CoverText();
        using var picture = Cover.Picture((int)(bounds.Width * scale), (int)(bounds.Height * scale), title, detail, ring: _signingOut);
        var bitmap = Cover.Dib(picture);
        try
        {
            if (DwmSetIconicThumbnail(hwnd, bitmap, 0) is var hr and not 0)
                Log.Info($"{Session.Title}: taskbar preview {picture.Width}x{picture.Height} (within {maxWidth}x{maxHeight}) refused: 0x{hr:X8}");
        }
        finally { DeleteObject(bitmap); }
    }

    /// <summary>What Peek (hovering the thumbnail) shows: the cover at the window's size.</summary>
    void SetLivePreview(IntPtr hwnd)
    {
        var bounds = Native.Bounds(this);
        var (title, detail) = CoverText();
        using var picture = Cover.Picture(bounds.Width, bounds.Height, title, detail, ring: _signingOut);
        var bitmap = Cover.Dib(picture);
        try { if (DwmSetIconicLivePreviewBitmap(hwnd, bitmap, IntPtr.Zero, 0) is var hr and not 0) Log.Info($"{Session.Title}: Peek preview: 0x{hr:X8}"); }
        finally { DeleteObject(bitmap); }
    }

    (int Width, int Height) _askedPreview;

    IntPtr? SendCoveredPreview(IntPtr hwnd, uint msg, IntPtr lParam)
    {
        if (msg == 0x0323 /* WM_DWMSENDICONICTHUMBNAIL: the size it can take, width high, height low */)
        {
            var (width, height) = ((int)((long)lParam >> 16) & 0xFFFF, (int)(long)lParam & 0xFFFF);
            // Said once per size: how large Windows shows the preview on this PC tells whether it is drawn 1:1.
            if (_askedPreview != (width, height)) Log.Info($"{Session.Title}: Windows asks for a taskbar preview within {width}x{height}");
            _askedPreview = (width, height);
            SetThumbnail(hwnd, width, height);
        }
        else SetLivePreview(hwnd);
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr bitmap, int flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr bitmap, IntPtr client, int flags);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr gdiObject);

    IntPtr? Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0323 /* WM_DWMSENDICONICTHUMBNAIL */ or 0x0326 /* WM_DWMSENDICONICLIVEPREVIEWBITMAP */:
                if (!Session.IsBusy && !_askingSignOut) return null;
                try { return SendCoveredPreview(hwnd, msg, lParam); }
                catch (Exception ex) // never out of a window procedure: Windows keeps its placeholder picture then
                {
                    Log.Error(ex, $"{Session.Title}: taskbar preview");
                    return null;
                }
            // In full screen every point of the window is client area: WPF-UI's chrome keeps its resize border whatever
            // ResizeMode says, so wherever the RDP control does not cover the edge (status screen) the window could be resized.
            case 0x84 /* WM_NCHITTEST */ when IsFullScreen:
                return 1; // HTCLIENT
            // Maximize / restore from the system menu, a title bar double-click or Win+arrows: through the cover too.
            case 0x0112 /* WM_SYSCOMMAND */ when ((int)wParam & 0xFFF0) is var cmd && !IsFullScreen
                                               && (cmd == 0xF030 /* SC_MAXIMIZE */ && WindowState == WindowState.Normal
                                                   || cmd == 0xF120 /* SC_RESTORE */ && WindowState == WindowState.Maximized):
                Dispatcher.BeginInvoke(() => SetWindowStateAsync(cmd == 0xF030 ? WindowState.Maximized : WindowState.Normal));
                return 0;
            default:
                return null;
        }
    }

    /// <summary>
    /// Maximize / restore behind a cover, like full screen: Windows' own animation would show the remote desktop
    /// stretched, then jumping to its new resolution once the server repainted it.
    /// </summary>
    Task SetWindowStateAsync(WindowState state) => Enqueue(async () =>
    {
        if (IsFullScreen || WindowState == state || WindowState == WindowState.Minimized) return;
        if (!Session.ShowsDesktop || !IsVisible)
        {
            WindowState = state; // the status screen has nothing to hide
            return;
        }
        var before = Native.Bounds(this);
        var normal = Native.GetPlacement(this);
        var after = state == WindowState.Maximized
            ? Screen.FromRectangle(before).WorkingArea
            : new Rectangle(normal.X, normal.Y, normal.Width, normal.Height);
        using var _ = Log.Time($"{Session.Title}: {(state == WindowState.Maximized ? "maximize" : "restore")}");
        await CoverWhileAsync(Rectangle.Union(before, after), async () =>
        {
            Session.SuspendLayout();
            WindowState = state;
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            Session.ResumeLayout();
            await Session.WaitForDisplayAsync();
        }, after);
        Session.FocusRemote();
    });

    void ShowBar()
    {
        if (!IsFullScreen || _closed || _closing || WindowState == WindowState.Minimized) return;
        if (_bar == null)
        {
            _bar = new FullScreenBar { Owner = this };
            _bar.MinimizeClicked += () => WindowState = WindowState.Minimized;
            _bar.RestoreClicked += () => RequestFullScreen(false);
            _bar.SignOutClicked += () => _ = SignOutAsync();
            _bar.CtrlAltDelClicked += () => _ = CtrlAltDelAsync();
            _bar.CloseClicked += RequestClose;
            _bar.SessionChosen += s => App.Sessions.Show(s);
            _bar.Dropped += monitor => _ = MoveToMonitorAsync(monitor);
            _bar.FocusReturned += () => Session.FocusRemote();
            _bar.Activated += (_, _) => UpdateTopmost();
            _bar.Deactivated += (_, _) => Dispatcher.BeginInvoke(UpdateTopmost);
        }
        _bar.Update(Session);
        _bar.Dock(_monitor, center: true);
    }

    void HideBar() => _bar?.Hide();

    /// <summary>The bar and a running cover go before the window: owned windows would otherwise outlive it on screen.</summary>
    void HideOverlays()
    {
        HideBar();
        _cover?.Close();
        _cover = null;
    }
}
