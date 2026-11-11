using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// A color of the user's own, in the app's look: a field of saturation and brightness, the hues under it, the code to
/// type or paste, and the colors picked before. Mouse or keyboard (arrows; Shift for bigger steps).
/// </summary>
public partial class ColorPickerWindow : FluentWindow
{
    const int RecentCount = 12;
    double _h, _s = 1, _v = 1;
    bool _typing; // the hex box is being typed in: it is not rewritten under the cursor

    ColorPickerWindow(string? initial)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Title = Bar.Title = Loc.Get("ColorPickerTitle");
        var start = ColorMath.FromHex(initial) ?? Color.FromRgb(0x3B, 0x82, 0xF6);
        OldPreview.Background = new SolidColorBrush(start);
        (_h, _s, _v) = ColorMath.ToHsv(start);
        BuildRecent();
        Loaded += (_, _) =>
        {
            Render();
            Field.Focus();
        };
    }

    /// <summary>The color picked as "#RRGGBB", or null when cancelled; a pick goes first in the recent colors.</summary>
    public static string? Pick(Window owner, string? initial)
    {
        var window = new ColorPickerWindow(initial) { Owner = owner };
        if (window.ShowDialog() != true) return null;
        var hex = window.Hex;
        var recent = App.Store.Data.Settings.RecentColors;
        recent.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, hex);
        if (recent.Count > RecentCount) recent.RemoveRange(RecentCount, recent.Count - RecentCount);
        return hex;
    }

    string Hex => ColorMath.ToHex(ColorMath.FromHsv(_h, _s, _v));

    void BuildRecent()
    {
        var recent = App.Store.Data.Settings.RecentColors.Select(ColorMath.FromHex).OfType<Color>().ToList();
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var color in recent)
        {
            var hex = ColorMath.ToHex(color);
            var button = new System.Windows.Controls.Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), ToolTip = hex,
                Template = SwatchTemplate(), Background = new SolidColorBrush(color),
            };
            AutomationProperties.SetName(button, hex);
            button.Click += (_, _) =>
            {
                (_h, _s, _v) = ColorMath.ToHsv(color);
                Render();
            };
            RecentList.Children.Add(button);
        }
    }

    // A round swatch with a hairline edge (lighter on hover), as the palette's.
    static ControlTemplate SwatchTemplate()
    {
        var fill = new FrameworkElementFactory(typeof(Ellipse), "Fill");
        fill.SetValue(Shape.FillProperty, new TemplateBindingExtension(BackgroundProperty));
        fill.SetResourceReference(Shape.StrokeProperty, "CardStrokeColorDefaultBrush");
        var template = new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = fill };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Shape.StrokeProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"), "Fill"));
        template.Triggers.Add(hover);
        return template;
    }

    /// <summary>Everything from the current hue, saturation and brightness.</summary>
    void Render()
    {
        var color = ColorMath.FromHsv(_h, _s, _v);
        HueFill.Fill = new SolidColorBrush(ColorMath.FromHsv(_h, 1, 1));
        NewPreview.Background = new SolidColorBrush(color);
        FieldThumb.Fill = new SolidColorBrush(color);
        HueThumb.Fill = new SolidColorBrush(ColorMath.FromHsv(_h, 1, 1));
        Place(FieldThumb, _s * Field.ActualWidth, (1 - _v) * Field.ActualHeight);
        Place(FieldThumbShade, _s * Field.ActualWidth, (1 - _v) * Field.ActualHeight);
        Canvas.SetLeft(HueThumb, _h / 360 * HueStrip.ActualWidth - HueThumb.Width / 2);
        if (!_typing) HexBox.Text = ColorMath.ToHex(color);
        AutomationProperties.SetItemStatus(Field, ColorMath.ToHex(color));
    }

    static void Place(FrameworkElement thumb, double x, double y)
    {
        Canvas.SetLeft(thumb, x - thumb.Width / 2);
        Canvas.SetTop(thumb, y - thumb.Height / 2);
    }

    void Area_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FieldClip.Rect = new Rect(0, 0, Field.ActualWidth, Field.ActualHeight);
        Render();
    }

    // ---------- Mouse: press and drag ----------

    void Field_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Field.Focus();
        Field.CaptureMouse();
        FieldAt(e.GetPosition(Field));
    }

    void Field_MouseMove(object sender, MouseEventArgs e)
    {
        if (Field.IsMouseCaptured) FieldAt(e.GetPosition(Field));
    }

    void FieldAt(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, Field.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, Field.ActualHeight), 0, 1);
        Render();
    }

    void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueStrip.Focus();
        HueStrip.CaptureMouse();
        HueAt(e.GetPosition(HueStrip));
    }

    void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (HueStrip.IsMouseCaptured) HueAt(e.GetPosition(HueStrip));
    }

    void HueAt(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueStrip.ActualWidth), 0, 1) * 359.9;
        Render();
    }

    void Area_MouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    // ---------- Keyboard: arrows, Shift for bigger steps ----------

    static double Step => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.02;

    void Field_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: _s -= Step; break;
            case Key.Right: _s += Step; break;
            case Key.Up: _v += Step; break;
            case Key.Down: _v -= Step; break;
            default: return;
        }
        (_s, _v) = (Math.Clamp(_s, 0, 1), Math.Clamp(_v, 0, 1));
        Render();
        e.Handled = true;
    }

    void Hue_KeyDown(object sender, KeyEventArgs e)
    {
        var step = Step * 180; // 3.6° or 18°
        switch (e.Key)
        {
            case Key.Left or Key.Down: _h = Math.Max(0, _h - step); break;
            case Key.Right or Key.Up: _h = Math.Min(359.9, _h + step); break;
            default: return;
        }
        Render();
        e.Handled = true;
    }

    // The thumb of the area with the keyboard: drawn in the accent color, as a focus ring.
    void Area_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        foreach (var (area, thumb) in new (UIElement, Shape)[] { (Field, FieldThumb), (HueStrip, HueThumb) })
            if (area.IsKeyboardFocused) thumb.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
            else thumb.Stroke = Brushes.White;
    }

    // ---------- The code ----------

    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!HexBox.IsKeyboardFocused || ColorMath.FromHex(HexBox.Text) is not { } color) return;
        _typing = true;
        (_h, _s, _v) = ColorMath.ToHsv(color);
        Render();
        _typing = false;
    }

    void HexBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Render(); // back to the full "#RRGGBB"

    void Choose_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
