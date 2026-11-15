using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace Sessionwright;

/// <summary>
/// Opaque, stage-colored cover over a session window (or the monitors it spans) that hides what must not be seen:
/// window bounds and chrome changing, the remote desktop changing resolution, sign-out typing into the session.
/// Optionally shows a spinner and a message, centered on <see cref="ContentArea"/>.
/// </summary>
/// <remarks>
/// A WinForms form, not a WPF window: WPF drops WS_EX_LAYERED from a window without AllowsTransparency and renders
/// AllowsTransparency windows per pixel in software (far too slow at full-screen size). A layered form with one
/// window-wide alpha is faded by DWM for free. Bounds are physical pixels and are never rescaled on a DPI change.
/// </remarks>
sealed class Cover : WinForms.Form
{
    // The session stage (status screen, letterbox, covers) in the app's theme: near the Mica tint of the window around it.
    static bool Light => Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Light;
    public static Color Stage => Light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x1C, 0x1C, 0x1C);
    static Color Track => Light ? Color.FromArgb(0xD6, 0xD6, 0xD6) : Color.FromArgb(0x3A, 0x3A, 0x3A);
    static Color TitleText => Light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
    static Color DetailText => Light ? Color.FromArgb(0x5C, 0x5C, 0x5C) : Color.FromArgb(0xC5, 0xC5, 0xC5);

    // Never fully opaque: a session window that is completely covered counts as occluded, and the RDP client then
    // stops taking the server's frames (the repaint after a resolution change would only start once the cover lifts).
    // At 254/255 the session shows through by 0.4 %, which is invisible.
    const double MaxAlpha = 254;

    readonly IntPtr _owner;
    string? _title, _detail;
    WinForms.Timer? _spin;
    float _angle;
    bool _closed;

    Cover(Rectangle bounds, IntPtr owner)
    {
        _owner = owner;
        FormBorderStyle = WinForms.FormBorderStyle.None;
        BackColor = Stage;
        ShowInTaskbar = false;
        StartPosition = WinForms.FormStartPosition.Manual;
        Bounds = bounds;
        Opacity = 0; // created layered, invisible
        DoubleBuffered = true;
        SetStyle(WinForms.ControlStyles.AllPaintingInWmPaint | WinForms.ControlStyles.UserPaint | WinForms.ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Physical screen rect to center the message on (default: the whole cover).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle? ContentArea { get; set; }

    protected override bool ShowWithoutActivation => true;

    // Never takes focus: the session below keeps the keyboard. Clicks are swallowed, so nothing changes mid-transition.
    protected override WinForms.CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */;
            return p;
        }
    }

    protected override void WndProc(ref WinForms.Message m)
    {
        switch (m.Msg)
        {
            case 0x02E0: return; // WM_DPICHANGED: keep the exact physical bounds, WinForms would rescale them
            case 0x0021: m.Result = 3; return; // WM_MOUSEACTIVATE: MA_NOACTIVATE
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Shows a cover over <paramref name="bounds"/> (physical px) above <paramref name="owner"/> and completes once it
    /// is fully opaque on screen.
    /// </summary>
    public static async Task<Cover> ShowAsync(System.Windows.Window owner, Rectangle bounds, string? title = null, string? detail = null, int fadeMs = 90, Rectangle? content = null)
    {
        var ownerHwnd = Native.Handle(owner);
        var cover = new Cover(bounds, ownerHwnd) { ContentArea = content }; // before the first paint
        cover.SetMessage(title, detail);
        cover.Show(new OwnerHandle(ownerHwnd)); // owned: always above the owner, also when the owner becomes topmost
        // Directly above the owner only: when it is behind another app, the cover must not show over that app.
        var after = Native.IsForeground(owner) ? IntPtr.Zero /* HWND_TOP */ : GetWindow(ownerHwnd, 3 /* GW_HWNDPREV */);
        SetWindowPos(cover.Handle, after, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate);
        await cover.FadeAsync(0, 1, Motion.Enabled ? fadeMs : 0);
        return cover;
    }

    public void SetBounds(Rectangle bounds)
    {
        if (!_closed && IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | NoZOrder);
    }

    /// <summary>A title shows a spinner with it; null for a plain cover.</summary>
    public void SetMessage(string? title, string? detail = null)
    {
        (_title, _detail) = (title, detail);
        if (_closed) return;
        if (title != null && _spin == null)
        {
            _spin = new WinForms.Timer { Interval = 16 };
            _spin.Tick += (_, _) =>
            {
                if (_closed) return;
                _angle = (_angle + 6) % 360;
                Invalidate(SpinnerRect());
            };
            _spin.Start();
        }
        else if (title == null)
        {
            _spin?.Dispose();
            _spin = null;
        }
        if (IsHandleCreated) Invalidate();
    }

    /// <summary>Puts the cover back above its owner's other windows (e.g. a full-screen bar shown meanwhile).</summary>
    public void Raise()
    {
        if (!_closed && IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, NoActivate | NoMove | NoSize);
    }

    /// <summary>Fades out and closes.</summary>
    public async Task HideAsync(int fadeMs = 180)
    {
        if (_closed) return;
        await FadeAsync(1, 0, Motion.Enabled ? fadeMs : 0);
        if (!_closed) Close();
    }

    // Also when Windows destroys it together with its owner (a session window closing): no timer may touch it after.
    protected override void OnHandleDestroyed(EventArgs e)
    {
        _closed = true;
        _spin?.Dispose();
        _spin = null;
        base.OnHandleDestroyed(e);
    }

    float Dpi => DeviceDpi / 96f;

    Point ContentCenter()
    {
        var area = ContentArea is { } a ? RectangleToClient(a) : ClientRectangle;
        return new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
    }

    Rectangle SpinnerRect()
    {
        var c = ContentCenter();
        var size = (int)(44 * Dpi);
        return new Rectangle(c.X - size / 2, c.Y - size - (int)(16 * Dpi), size, size);
    }

    protected override void OnPaint(WinForms.PaintEventArgs e)
    {
        if (_title == null) return; // the background is already the stage color
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var spinner = SpinnerRect();
        spinner.Inflate(-(int)(3 * Dpi), -(int)(3 * Dpi));
        using (var track = new Pen(Track, 4 * Dpi))
            g.DrawEllipse(track, spinner);
        using (var arc = new Pen(Accent(), 4 * Dpi) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, spinner, _angle, 100);

        var c = ContentCenter();
        using var titleFont = new Font("Segoe UI Semibold", 20 * Dpi, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 14 * Dpi, GraphicsUnit.Pixel);
        var width = Math.Min(ClientSize.Width, (int)(560 * Dpi));
        DrawText(g, _title, titleFont, TitleText, new Rectangle(c.X - width / 2, c.Y + (int)(4 * Dpi), width, (int)(40 * Dpi)));
        if (_detail != null)
            DrawText(g, _detail, detailFont, DetailText, new Rectangle(c.X - width / 2, c.Y + (int)(38 * Dpi), width, (int)(60 * Dpi)));
    }

    /// <summary>The cover as a picture: what the taskbar preview of a covered window shows (it would show what is covered).</summary>
    public static Bitmap Picture(int width, int height, string? title, string? detail)
    {
        var picture = new Bitmap(Math.Max(1, width), Math.Max(1, height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(picture);
        g.Clear(Stage); // opaque: DWM takes premultiplied alpha, which opaque pixels are
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        // Readable in a taskbar thumbnail (about 160 x 90) too, where the window's own proportions would make it tiny.
        var scale = Math.Max(0.85f, Math.Min(width, height) / 300f);
        using var titleFont = new Font("Segoe UI Semibold", 16 * scale, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var titleBrush = new SolidBrush(TitleText);
        using var detailBrush = new SolidBrush(DetailText);
        var mid = height / 2f;
        if (title != null) g.DrawString(title, titleFont, titleBrush, new RectangleF(8, mid - 26 * scale, width - 16, 24 * scale), center);
        if (detail != null) g.DrawString(detail, detailFont, detailBrush, new RectangleF(8, mid + 2 * scale, width - 16, 20 * scale), center);
        return picture;
    }

    /// <summary>
    /// The picture as a 32-bit top-down DIB section, the only kind DWM takes for taskbar previews (Bitmap.GetHbitmap
    /// gives another). The caller deletes it (DeleteObject).
    /// </summary>
    public static IntPtr Dib(Bitmap picture)
    {
        var info = new BITMAPINFOHEADER
        {
            Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = picture.Width, Height = -picture.Height /* top-down */,
            Planes = 1, BitCount = 32,
        };
        var dib = CreateDIBSection(IntPtr.Zero, ref info, 0 /* DIB_RGB_COLORS */, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var data = picture.LockBits(new Rectangle(0, 0, picture.Width, picture.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[picture.Width * 4];
            for (var y = 0; y < picture.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, bits + y * row.Length, row.Length);
            }
        }
        finally { picture.UnlockBits(data); }
        return dib;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    // GDI ClearType on the opaque stage color, like the rest of Windows: GDI+ grayscale antialiasing looked blurry.
    static void DrawText(Graphics g, string text, Font font, Color color, Rectangle rect) =>
        WinForms.TextRenderer.DrawText(g, text, font, rect, color, Stage,
            WinForms.TextFormatFlags.HorizontalCenter | WinForms.TextFormatFlags.WordBreak | WinForms.TextFormatFlags.NoPadding);

    static Color Accent()
    {
        var a = Wpf.Ui.Appearance.ApplicationAccentColorManager.PrimaryAccent;
        return Color.FromArgb(a.R, a.G, a.B);
    }

    /// <summary>
    /// Ease-out alpha ramp on a worker thread: the UI thread may be busy with the session repainting at its new size,
    /// and the window alpha is applied by DWM, so the fade stays smooth regardless. Completes once DWM has composed
    /// the final alpha.
    /// </summary>
    Task FadeAsync(double from, double to, int ms)
    {
        if (_closed) return Task.CompletedTask;
        var hwnd = Handle;
        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            for (double t = ms <= 0 ? 1 : 0; ; await Task.Delay(8))
            {
                t = ms <= 0 ? 1 : Math.Min(1, clock.ElapsedMilliseconds / (double)ms);
                var opacity = from + (to - from) * (1 - Math.Pow(1 - t, 3));
                SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(opacity * MaxAlpha), 0x2 /* LWA_ALPHA */);
                if (t >= 1) break;
            }
            Native.DwmFlush();
        });
    }

    sealed record OwnerHandle(IntPtr Value) : WinForms.IWin32Window
    {
        IntPtr WinForms.IWin32Window.Handle => Value;
    }

    const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010;
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
}
