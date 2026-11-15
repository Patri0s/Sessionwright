using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace Sessionwright;

/// <summary>Windows 11-style motion: smooth wheel scrolling and entrance (fade + slide) animations.</summary>
public static class Motion
{
    /// <summary>Honors "Animation effects" in Windows accessibility settings.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static IEasingFunction EaseOut { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    static IEasingFunction Frozen(CubicEase e)
    {
        e.Freeze();
        return e;
    }

    public static DoubleAnimation Animate(double? from, double to, int ms) =>
        new() { From = from, To = to, Duration = TimeSpan.FromMilliseconds(Enabled ? ms : 0), EasingFunction = EaseOut };

    public static DoubleAnimation To(double to, int ms) => Animate(null, to, ms);

    // ---------- Entrance: l:Motion.Entrance="True" plays fade + slide-up every time the element becomes visible ----------

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceChanged));

    public static bool GetEntrance(DependencyObject d) => (bool)d.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject d, bool value) => d.SetValue(EntranceProperty, value);

    static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement el && (bool)e.NewValue)
            el.IsVisibleChanged += (_, a) => { if ((bool)a.NewValue) PlayEntrance(el); };
    }

    public static void PlayEntrance(UIElement el, double offset = 12)
    {
        if (!Enabled) return;
        var slide = new TranslateTransform();
        el.RenderTransform = slide;
        el.BeginAnimation(UIElement.OpacityProperty, Animate(0, 1, 220));
        slide.BeginAnimation(TranslateTransform.YProperty, Animate(offset, 0, 320));
    }

    // ---------- Tree: l:Motion.AnimateExpand on TreeViewItem slides its children open and shut ----------

    public static readonly DependencyProperty AnimateExpandProperty = DependencyProperty.RegisterAttached(
        "AnimateExpand", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateExpandChanged));

    public static bool GetAnimateExpand(DependencyObject d) => (bool)d.GetValue(AnimateExpandProperty);
    public static void SetAnimateExpand(DependencyObject d, bool value) => d.SetValue(AnimateExpandProperty, value);

    // The latest animation per children host: an older one finishing must not clear a newer one (quick toggling).
    static readonly ConditionalWeakTable<FrameworkElement, AnimationTimeline> Expanding = new();

    static void OnAnimateExpandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item) return;
        item.Expanded -= OnExpandToggled;
        item.Collapsed -= OnExpandToggled;
        if (!(bool)e.NewValue) return;
        item.Expanded += OnExpandToggled;
        item.Collapsed += OnExpandToggled;
    }

    static void OnExpandToggled(object sender, RoutedEventArgs e)
    {
        var item = (TreeViewItem)sender;
        if (e.OriginalSource != item || !Enabled || !item.IsVisible || FindItemsHost(item) is not { } host) return;
        var expanding = item.IsExpanded;
        var shownHeight = host.ActualHeight; // before the template hides the children
        // The template shows / hides the children only after this event: start once it has, still before the next frame.
        item.Dispatcher.BeginInvoke(() => Slide(item, host, expanding, shownHeight), System.Windows.Threading.DispatcherPriority.Normal);
    }

    static void Slide(TreeViewItem item, ItemsPresenter host, bool expanding, double shownHeight)
    {
        if (item.IsExpanded != expanding) return; // toggled back meanwhile
        // On collapse the template hid the children at once; an animation (above the trigger) keeps them shown meanwhile.
        FrameworkElement? hidden = null;
        for (DependencyObject? d = host; d != null && d != item; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Visibility: not Visibility.Visible } f) hidden = f;
        double height;
        if (expanding)
        {
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.Measure(new Size(host.ActualWidth > 0 ? host.ActualWidth : item.ActualWidth, double.PositiveInfinity));
            height = host.DesiredSize.Height;
        }
        else height = Expanding.TryGetValue(host, out _) ? host.ActualHeight : shownHeight; // mid-animation: from where it is
        if (height <= 0) return;

        var ms = expanding ? 260 : 200;
        var slide = Animate(expanding ? 0 : height, expanding ? height : 0, ms);
        Expanding.AddOrUpdate(host, slide);
        slide.Completed += (_, _) =>
        {
            if (!Expanding.TryGetValue(host, out var latest) || latest != slide) return;
            Expanding.Remove(host);
            host.BeginAnimation(FrameworkElement.HeightProperty, null); // back to its natural height (children added later)
            host.BeginAnimation(UIElement.OpacityProperty, null);
            hidden?.BeginAnimation(UIElement.VisibilityProperty, null);
        };
        if (!expanding && hidden != null)
        {
            var keep = new ObjectAnimationUsingKeyFrames { Duration = slide.Duration };
            keep.KeyFrames.Add(new DiscreteObjectKeyFrame(Visibility.Visible, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            hidden.BeginAnimation(UIElement.VisibilityProperty, keep);
        }
        host.ClipToBounds = true;
        host.BeginAnimation(FrameworkElement.HeightProperty, slide);
        host.BeginAnimation(UIElement.OpacityProperty, Animate(expanding ? 0 : 1, expanding ? 1 : 0, ms));
    }

    /// <summary>
    /// Shows or hides <paramref name="el"/> by sliding its height open or shut, as the tree's groups do (at once when
    /// animations are off); <paramref name="done"/> runs once it has settled.
    /// </summary>
    public static void Fold(FrameworkElement el, bool show, Action? done = null)
    {
        if (!Enabled || el.Parent is not FrameworkElement { IsVisible: true } parent)
        {
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            done?.Invoke();
            return;
        }
        double from, to;
        if (show)
        {
            from = el.IsVisible && Expanding.TryGetValue(el, out _) ? el.ActualHeight : 0; // mid-fold: from where it is
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Visibility = Visibility.Visible;
            el.Measure(new Size(parent.ActualWidth, double.PositiveInfinity));
            to = el.DesiredSize.Height;
        }
        else (from, to) = (el.ActualHeight, 0);
        var ms = show ? 260 : 200;
        var slide = Animate(from, to, ms);
        Expanding.AddOrUpdate(el, slide);
        slide.Completed += (_, _) =>
        {
            if (!Expanding.TryGetValue(el, out var latest) || latest != slide) return;
            Expanding.Remove(el);
            if (!show) el.Visibility = Visibility.Collapsed;
            el.BeginAnimation(FrameworkElement.HeightProperty, null); // back to its natural height
            el.BeginAnimation(UIElement.OpacityProperty, null);
            done?.Invoke();
        };
        el.ClipToBounds = true;
        el.BeginAnimation(FrameworkElement.HeightProperty, slide);
        el.BeginAnimation(UIElement.OpacityProperty, Animate(show ? 0 : 1, show ? 1 : 0, ms));
    }

    /// <summary>The item's own children presenter: the first ItemsPresenter in its template (nested items' are deeper).</summary>
    static ItemsPresenter? FindItemsHost(TreeViewItem item)
    {
        var queue = new Queue<DependencyObject>([item]);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is ItemsPresenter p) return p;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    // ---------- Smooth scrolling: set on ScrollViewer (globally via implicit style in App.xaml) ----------
    // Driven from CompositionTarget.Rendering with the frame's own timestamp, once per rendered frame (WPF Timeline
    // animations run on a separate clock and skipped frames on a 165 Hz monitor). The position follows a critically
    // damped spring toward the target: every wheel notch only moves the target, so velocity stays continuous and
    // consecutive notches glide like Windows 11 lists instead of re-accelerating ("pulsing") per notch.

    public static readonly DependencyProperty SmoothScrollProperty = DependencyProperty.RegisterAttached(
        "SmoothScroll", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSmoothScrollChanged));

    public static bool GetSmoothScroll(DependencyObject d) => (bool)d.GetValue(SmoothScrollProperty);
    public static void SetSmoothScroll(DependencyObject d, bool value) => d.SetValue(SmoothScrollProperty, value);

    sealed class ScrollState
    {
        public double Current, Target, Velocity, SmoothTime;
        public TimeSpan LastFrame;
        public EventHandler? Frame;
        public UIElement? Cached;
    }

    static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();
    const double WheelSmoothTime = 0.085, PrecisionSmoothTime = 0.035; // seconds; ~3x this to settle
    const double Overshoot = 6; // px aimed past the target (never shown: motion stops at the target)

    static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnWheel;
    }

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // An open drop-down (it has the mouse) would follow the page: the page keeps still meanwhile, like in Windows,
        // and only the drop-down's own list scrolls.
        if (Mouse.Captured is ComboBox { IsDropDownOpen: true } && !HasScrollableInner(e.OriginalSource as DependencyObject, sv))
        {
            e.Handled = true;
            return;
        }
        // Item-based scrolling (virtualized lists) and Ctrl/Shift+wheel keep the default behavior.
        if (!Enabled || sv.CanContentScroll || sv.ScrollableHeight <= 0 || Keyboard.Modifiers != ModifierKeys.None) return;
        if (HasScrollableInner(e.OriginalSource as DependencyObject, sv)) return; // innermost viewer handles it
        e.Handled = true;

        var s = States.GetOrCreateValue(sv);
        if (s.Frame == null)
        {
            // Idle: start where the scrollbar / keyboard left the content.
            s.Current = s.Target = sv.VerticalOffset;
            s.Velocity = 0;
            s.LastFrame = TimeSpan.Zero;
            s.Frame = (_, a) => Step(sv, s, ((RenderingEventArgs)a).RenderingTime);
            CacheWhileScrolling(sv, s);
            CompositionTarget.Rendering += s.Frame;
        }
        s.Target = Math.Clamp(s.Target - e.Delta * 0.8, 0, sv.ScrollableHeight);
        // Touchpads / free-spin wheels send many small deltas: follow them more tightly.
        s.SmoothTime = e.Delta % Mouse.MouseWheelDeltaForOneLine == 0 ? WheelSmoothTime : PrecisionSmoothTime;
    }

    static void Step(ScrollViewer sv, ScrollState s, TimeSpan frameTime)
    {
        if (frameTime == s.LastFrame) return; // Rendering can fire more than once per frame
        var dt = s.LastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((frameTime - s.LastFrame).TotalSeconds, 0.001, 0.05);
        s.LastFrame = frameTime;
        // Aim a few pixels past the target and stop when crossing it: the motion ends at a small but non-zero speed
        // instead of a spring's exponential tail creeping 1 px every few frames (which reads as judder).
        var direction = Math.Sign(s.Target - s.Current);
        var next = SmoothDamp(s.Current, s.Target + direction * Overshoot, ref s.Velocity, s.SmoothTime, dt);
        var done = direction == 0 || Math.Sign(s.Target - next) != direction;
        s.Current = done ? s.Target : next;
        // Whole device pixels: sub-pixel offsets make text re-rasterize and shimmer.
        var scale = VisualTreeHelper.GetDpi(sv).DpiScaleY;
        sv.ScrollToVerticalOffset(Math.Round(s.Current * scale) / scale);
        if (!done) return;
        CompositionTarget.Rendering -= s.Frame;
        s.Frame = null;
        if (s.Cached != null) s.Cached.CacheMode = null; // back to live rendering (crisp text, hover effects)
        s.Cached = null;
    }

    // Measured in the Settings window at 165 Hz: rendering the live controls every frame dropped ~1 in 5 frames;
    // a bitmap cache turns each frame into moving one texture and roughly halves the drops. Only while scrolling:
    // cached text is grayscale-antialiased and hover changes would re-render the whole cache.
    static void CacheWhileScrolling(ScrollViewer sv, ScrollState s)
    {
        if (sv.Content is not UIElement content || content.CacheMode != null) return;
        var dpi = VisualTreeHelper.GetDpi(sv);
        if (content.RenderSize.Height * dpi.DpiScaleY > 8192) return; // very long content: texture too large
        content.CacheMode = new BitmapCache(dpi.DpiScaleX) { SnapsToDevicePixels = true };
        s.Cached = content;
    }

    /// <summary>Critically damped spring step (frame-rate independent; Game Programming Gems 4, 1.10).</summary>
    internal static double SmoothDamp(double current, double target, ref double velocity, double smoothTime, double dt)
    {
        var omega = 2 / smoothTime;
        var x = omega * dt;
        var decay = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
        var change = current - target;
        var temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * decay;
        return target + (change + temp) * decay;
    }

    static bool HasScrollableInner(DependencyObject? d, ScrollViewer outer)
    {
        for (; d != null && d != outer; d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
        return false;
    }
}
