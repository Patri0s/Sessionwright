using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// The middle of a list with nothing to show yet, the same in every window: a ring while something is on its way, else an
/// icon; a sentence under it; whatever the window adds (a button) below that. As the empty connection tree looks.
/// </summary>
public sealed class StatusView : StackPanel
{
    readonly ProgressRing _ring = new() { IsIndeterminate = true, Width = 36, Height = 36 };
    readonly SymbolIcon _icon = new() { FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center };
    readonly System.Windows.Controls.TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
    };

    public StatusView()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        MaxWidth = 440;
        Margin = new Thickness(16);
        _icon.SetResourceReference(Control.ForegroundProperty, "TextFillColorTertiaryBrush");
        _text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    // The window's own parts (a button) follow the ring, icon and text.
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Children.Insert(0, _text);
        Children.Insert(0, _icon);
        Children.Insert(0, _ring);
    }

    /// <summary>Shown with <paramref name="text"/>: a ring while <paramref name="busy"/>, else <paramref name="icon"/>.</summary>
    public void Show(string text, bool busy, SymbolRegular icon)
    {
        Visibility = Visibility.Visible;
        _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _icon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _icon.Symbol = icon;
        _text.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(this, text);
    }
}
