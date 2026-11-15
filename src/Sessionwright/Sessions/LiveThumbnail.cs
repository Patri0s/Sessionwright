using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sessionwright;

/// <summary>
/// A session window's live picture in this element's place, drawn by DWM straight from the window (like Alt+Tab): kept in
/// proportion and centered, and cut to the scrolled area around it, as DWM draws over the window and knows nothing of
/// WPF's clipping. Its own content shows where DWM has nothing to draw (a minimized window).
/// </summary>
public sealed class LiveThumbnail : Border
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(RdpSession), typeof(LiveThumbnail), new PropertyMetadata(null, (d, _) => ((LiveThumbnail)d).Place()));

    public RdpSession? Session
    {
        get => (RdpSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    IntPtr _thumbnail, _source;
    DWM_THUMBNAIL_PROPERTIES _last;
    // The session window resized or moved to another scale: nothing on this side has a layout pass to tell.
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public LiveThumbnail()
    {
        _timer.Tick += (_, _) => Place();
        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayoutUpdated;
            _timer.Start();
            Place();
        };
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            _timer.Stop();
            Detach();
        };
        IsVisibleChanged += (_, _) => Place();
    }

    void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    void Detach()
    {
        if (_thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(_thumbnail);
        (_thumbnail, _source, _last) = (IntPtr.Zero, IntPtr.Zero, default);
    }

    void Place()
    {
        var window = Session is { } s ? App.Sessions.WindowOf(s) : null;
        var source = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        if (source != _source) Detach();
        if (source == IntPtr.Zero || !IsLoaded || PresentationSource.FromVisual(this) is not HwndSource { RootVisual: { } root } host) return;
        if (_thumbnail == IntPtr.Zero)
        {
            if (DwmRegisterThumbnail(host.Handle, source, out var thumbnail) != 0) return;
            (_thumbnail, _source) = (thumbnail, source);
        }

        var visible = IsVisible && RenderSize is { Width: > 0, Height: > 0 };
        var props = new DWM_THUMBNAIL_PROPERTIES { Flags = 0x8 /* VISIBLE */ };
        if (visible && DwmQueryThumbnailSourceSize(_thumbnail, out var size) == 0 && size.X > 0 && size.Y > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Rect Device(Rect r) => new(r.X * dpi.DpiScaleX, r.Y * dpi.DpiScaleY, r.Width * dpi.DpiScaleX, r.Height * dpi.DpiScaleY);
            var box = Device(TransformToAncestor(root).TransformBounds(new Rect(RenderSize)));
            var scale = Math.Min(box.Width / size.X, box.Height / size.Y);
            var picture = new Rect(box.X + (box.Width - size.X * scale) / 2, box.Y + (box.Height - size.Y * scale) / 2, size.X * scale, size.Y * scale);
            var shown = picture;
            for (DependencyObject? d = VisualTreeHelper.GetParent(this); d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ScrollViewer viewer) shown.Intersect(Device(viewer.TransformToAncestor(root).TransformBounds(new Rect(viewer.RenderSize))));
            if (!shown.IsEmpty && shown.Width >= 1 && shown.Height >= 1)
            {
                // The part of the window that lands in the shown part of the picture.
                var from = new Rect((shown.X - picture.X) / scale, (shown.Y - picture.Y) / scale, shown.Width / scale, shown.Height / scale);
                props = new DWM_THUMBNAIL_PROPERTIES
                {
                    Flags = 0x1 /* RECTDESTINATION */ | 0x2 /* RECTSOURCE */ | 0x4 /* OPACITY */ | 0x8 /* VISIBLE */ | 0x10 /* SOURCECLIENTAREAONLY */,
                    Destination = RECT.From(shown), Source = RECT.From(from), Opacity = 255, Visible = true, SourceClientAreaOnly = false,
                };
            }
        }
        if (props.Equals(_last)) return; // LayoutUpdated comes for every layout pass in the window
        _last = props;
        DwmUpdateThumbnailProperties(_thumbnail, ref props);
    }

    [StructLayout(LayoutKind.Sequential)]
    record struct RECT(int Left, int Top, int Right, int Bottom)
    {
        public static RECT From(Rect r) => new((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    record struct DWM_THUMBNAIL_PROPERTIES
    {
        public int Flags;
        public RECT Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);
}
