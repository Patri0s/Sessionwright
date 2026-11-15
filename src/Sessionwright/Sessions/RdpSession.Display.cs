using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinForms = System.Windows.Forms;

namespace Sessionwright;

// The remote desktop's size: following the window, monitors, scaling. Part of RdpSession (see RdpSession.cs).
public sealed partial class RdpSession
{
    // ---------- Display size ----------

    /// <summary>
    /// Keeps the control at its current size while the window changes bounds and chrome (each resize of the ActiveX
    /// costs hundreds of ms on the UI thread); <see cref="ResumeLayout"/> applies the final size once.
    /// </summary>
    public void SuspendLayout()
    {
        _frozen = true;
        _resizeDebounce.Stop();
        _blackBefore = ShowsDesktop ? BlackFraction() : -1; // what "repainted" looks like for this desktop (see WaitForRepaintAsync)
    }

    double _blackBefore = -1;

    /// <summary>Lays the control out at the final size and sends it to the server right away.</summary>
    public void ResumeLayout()
    {
        _frozen = false;
        LayoutControl();
        _resizeDebounce.Stop();
        FitDisplay();
    }

    void LayoutControl()
    {
        if (_frozen || _disposed) return;
        var frame = _frame.ClientSize;
        if (frame.Width < 64 || frame.Height < 64) return; // minimized or not laid out yet: keep the last size
        System.Drawing.Size size;
        if (Dynamic)
        {
            size = new System.Drawing.Size(Even(frame.Width), Even(frame.Height)); // the server wants even sizes
        }
        else
        {
            // Fixed or all monitors: the desktop scaled down to fit (never up), keeping its aspect ratio.
            var desktop = !_desktop.IsEmpty ? _desktop
                : _options.Resolution == ResolutionMode.Fixed ? new System.Drawing.Size(_options.Width, _options.Height)
                : SpanBounds.Size;
            var fit = Math.Min(1, Math.Min((double)frame.Width / desktop.Width, (double)frame.Height / desktop.Height));
            size = new System.Drawing.Size((int)Math.Floor(desktop.Width * fit), (int)Math.Floor(desktop.Height * fit));
        }
        var bounds = new System.Drawing.Rectangle((frame.Width - size.Width) / 2, (frame.Height - size.Height) / 2, size.Width, size.Height);
        if (_rdp.Bounds == bounds) return;
        _rdp.Bounds = bounds;
        // WinForms sizes are physical, so this also covers a move to a monitor with another scale.
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    /// <summary>The control's size or scale differs from what the server has: <see cref="FitDisplay"/> would send an update.</summary>
    public bool DisplayOutdated => !_disposed && !_frozen && _loggedOn && Dynamic && Phase == SessionPhase.Ready
        && _rdp.Width >= 200 && _rdp.Height >= 200 && _requested != (_rdp.Size, DesktopScale);

    /// <summary>
    /// Sends the control's size (and the local scale) to the server once logged on (it ignores updates before).
    /// Returns whether the server still has to apply it (see <see cref="WaitForDisplayAsync"/>).
    /// </summary>
    public bool FitDisplay()
    {
        if (_disposed || _frozen || !_loggedOn || !Dynamic || Phase != SessionPhase.Ready) return false;
        var size = _rdp.Size;
        if (size.Width < 200 || size.Height < 200) return false;
        var scale = DesktopScale;
        if (_requested == (size, scale)) return _display is { Task.IsCompleted: false };
        try
        {
            // Dynamic resolution (RDP 8.1+). Older servers throw; the session then keeps its size (scaled).
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height, 0u, scale, DeviceScale(scale));
            Log.Info($"{Title}: display {size.Width}x{size.Height} at {scale} %");
            _requested = (size, scale);
            // A scale-only change is not reported back: nothing to wait for.
            _display = size == _desktop ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _display != null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Warn($"{Title}: display update refused: {ex.Message}");
            return false;
        }
    }

    /// <summary>Completes once the server shows the requested size (or after a timeout) and has repainted it.</summary>
    public async Task WaitForDisplayAsync()
    {
        if (_display is not { } pending) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = await Task.WhenAny(pending.Task, Task.Delay(Timings.DisplayTimeout)) == pending.Task;
        if (!done)
        {
            Log.Info($"{Title}: server did not confirm the display update within {Timings.DisplayTimeout} ms");
            return;
        }
        var applied = clock.ElapsedMilliseconds;
        if (ShowsDesktop) await WaitForRepaintAsync(); // a hidden control is waited for once it is shown
        Log.Info($"{Title}: server applied the display update in {applied} ms, repainted in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// After a resolution change the control clears its surface to black (all of it, or the part beyond the old size,
    /// or a few tiles of a scaled stale frame) and the server repaints it over the next 0.1-1 s, partly at first. No
    /// event says when it is done, so the control's pixels are sampled (PrintWindow also reads them under a cover):
    /// done once no more points are black than before the change (a desktop without wallpaper is black), for two
    /// samples in a row.
    /// </summary>
    async Task WaitForRepaintAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var allowed = Math.Max(0, _blackBefore) + 0.01;
        var sawCleared = false;
        var clean = 0;
        while (clock.ElapsedMilliseconds < Timings.RepaintTimeout && !_disposed && Phase == SessionPhase.Ready)
        {
            await Task.Delay(40);
            var black = BlackFraction();
            if (black < 0)
            {
                await Task.Delay(Timings.DisplaySettle); // cannot read the pixels: a fixed guess
                return;
            }
            if (black > allowed)
            {
                sawCleared = true;
                clean = 0;
                continue;
            }
            // Right after the event the old frame may still be presented: wait to see it cleared (or a while).
            if (!sawCleared && clock.ElapsedMilliseconds < 400) continue;
            if (++clean < 2) continue;
            await Task.Delay(150); // the last tiles of a partial repaint can lag the samples
            return;
        }
    }

    /// <summary>Shown again after being hidden: the control takes the server's frames again, black until they arrive.</summary>
    public async Task WaitForPaintAsync(int timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = -1.0;
        while (clock.ElapsedMilliseconds < timeout && !_disposed)
        {
            await Task.Delay(40);
            var black = BlackFraction();
            if (black < 0)
            {
                await Task.Delay(100);
                return;
            }
            // Not all black any more (a desktop without wallpaper still has icons and a taskbar), and settled.
            if (black < 0.97 && Math.Abs(black - last) < 0.01) return;
            last = black;
        }
    }

    System.Drawing.Bitmap? _probe;

    /// <summary>Share of pure black points on a grid over the control, or -1 if the window cannot be read.</summary>
    double BlackFraction() => Sample() is { } points ? points.Count(p => (p & 0xFF) <= 3 && ((p >> 8) & 0xFF) <= 3 && ((p >> 16) & 0xFF) <= 3) / (double)points.Length : -1;

    /// <summary>Share of grid points whose color changed clearly between two samples.</summary>
    static double Changed(int[] before, int[] after) =>
        before.Zip(after).Count(p => Math.Abs((p.First & 0xFF) - (p.Second & 0xFF)) + Math.Abs(((p.First >> 8) & 0xFF) - ((p.Second >> 8) & 0xFF))
                                     + Math.Abs(((p.First >> 16) & 0xFF) - ((p.Second >> 16) & 0xFF)) > 60) / (double)before.Length;

    /// <summary>
    /// The control's colors on a 32 x 18 grid, read through PrintWindow (which also works under a cover), or null if the
    /// window cannot be read.
    /// </summary>
    int[]? Sample()
    {
        if (_disposed || Window.GetWindow(Host) is not { } window || !window.IsVisible) return null;
        var hwnd = Native.Handle(window);
        var bounds = Native.Bounds(hwnd);
        var control = _rdp.RectangleToScreen(_rdp.ClientRectangle);
        control.Offset(-bounds.X, -bounds.Y);
        control.Intersect(new System.Drawing.Rectangle(System.Drawing.Point.Empty, bounds.Size));
        if (control.Width < 64 || control.Height < 64) return null;
        if (_probe == null || _probe.Size != bounds.Size)
        {
            _probe?.Dispose();
            _probe = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        }
        using (var g = System.Drawing.Graphics.FromImage(_probe))
        {
            var dc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
            }
            finally { g.ReleaseHdc(dc); }
        }
        var data = _probe.LockBits(new System.Drawing.Rectangle(System.Drawing.Point.Empty, _probe.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, _probe.PixelFormat);
        try
        {
            const int columns = 32, rows = 18;
            var points = new int[columns * rows];
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = control.X + 4 + (control.Width - 8) * column / (columns - 1);
                var y = control.Y + 4 + (control.Height - 8) * row / (rows - 1);
                points[row * columns + column] = Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4) & 0xFFFFFF;
            }
            return points;
        }
        finally { _probe.UnlockBits(data); }
    }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>Local display scale (100 = 100 %); the remote desktop renders at it, like mstsc, so text keeps its size.</summary>
    uint DesktopScale => (uint)Math.Clamp(Math.Round(VisualTreeHelper.GetDpi(Host).DpiScaleX * 100), 100, 500);

    // RDP only accepts these device scale factors.
    static uint DeviceScale(uint desktopScale) => desktopScale < 125 ? 100u : desktopScale < 165 ? 140u : 180u;

    static int Even(int v) => Math.Clamp(v, 200, 8192) & ~1;

    // IMsTscNonScriptable::ResetPassword, slot 12 (oVft 96 in the mstscax.dll type library). The control keeps the
    // ClearTextPassword of its previous connect otherwise.
    static readonly Guid NonScriptable = new("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Call(IntPtr self);

    // IMsRdpClientNonScriptable::SendKeys, slot 14 (oVft 112 in the mstscax.dll type library); IUnknown-only like the other
    // non-scriptable interfaces. At most 20 keys per call.
    static readonly Guid ClientNonScriptable = new("2f079c4c-87b2-4afd-97ab-20cdb43038ae");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SendKeysCall(IntPtr self, int count, short[] keyUp, int[] keyData);

    /// <summary>
    /// Keys typed while the user may be elsewhere (a sign-out goes on in the background): the control takes keys only
    /// with the keyboard focus, which brings its window forward, so a window the user went to meanwhile (another
    /// session) gets the foreground straight back. Before, every step took it back from them for good.
    /// </summary>
    bool SendKeysFocused(RemoteInput.Key[] keys)
    {
        var elsewhere = GetForegroundWindow();
        var mine = _rdp.IsHandleCreated ? GetAncestor(_rdp.Handle, 2 /* GA_ROOT */) : IntPtr.Zero;
        FocusRemote();
        var sent = SendKeys(keys);
        if (elsewhere != IntPtr.Zero && elsewhere != mine && GetWindow(elsewhere, 4 /* GW_OWNER */) != mine && IsWindowVisible(elsewhere))
            SetForegroundWindow(elsewhere);
        return sent;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>Sends keys into the session (see <see cref="RemoteInput"/>). False when the control refused them.</summary>
    bool SendKeys(RemoteInput.Key[] keys)
    {
        if (_disposed || _rdp.GetOcx() == null) return false;
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Ocx);
        try
        {
            var iid = ClientNonScriptable;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var itf));
            try
            {
                var send = Marshal.GetDelegateForFunctionPointer<SendKeysCall>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), 14 * IntPtr.Size));
                foreach (var chunk in keys.Chunk(20))
                {
                    var hr = send(itf, chunk.Length, [.. chunk.Select(k => k.Up ? (short)-1 /* VARIANT_TRUE */ : (short)0)], [.. chunk.Select(k => k.Code)]);
                    if (hr == 0) continue;
                    Log.Warn($"{Title}: the session refused the keys (0x{hr:X8}; keyboard focus elsewhere?)");
                    return false;
                }
                return true;
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unknown); }
    }

    void ResetPassword()
    {
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Ocx);
        try
        {
            var iid = NonScriptable;
            if (Marshal.QueryInterface(unknown, in iid, out var itf) != 0) return;
            try
            {
                var reset = Marshal.GetDelegateForFunctionPointer<Call>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), 12 * IntPtr.Size));
                if (reset(itf) != 0) Log.Warn($"{Title}: resetting the stored password failed");
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unknown); }
    }

    // IMsRdpClientNonScriptable5 is IUnknown-only (not reachable through `dynamic`) and deep in an inheritance chain, so
    // its setters are called by vtable slot (offset / 8, verified against the mstscax.dll type library).
    static readonly Guid NonScriptable5 = new("4f6996d5-d7b1-412c-b0ff-063718566907");
    const int UseMultimonSlot = 53, DisableConnectionBarSlot = 58; // put_UseMultimon 424, put_DisableConnectionBar 464
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PutBool(IntPtr self, short value);

    void PutNonScriptable5(int slot, bool on)
    {
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Ocx);
        try
        {
            var iid = NonScriptable5;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var itf));
            try
            {
                var put = Marshal.GetDelegateForFunctionPointer<PutBool>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), slot * IntPtr.Size));
                Marshal.ThrowExceptionForHR(put(itf, (short)(on ? -1 : 0))); // VARIANT_TRUE
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unknown); }
    }
}
