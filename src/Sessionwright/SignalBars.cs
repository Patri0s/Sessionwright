using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Sessionwright;

/// <summary>Connection quality as four bars (Level 1-4 lit, 0 = not measured yet: all dim), in the current Foreground.</summary>
public sealed class SignalBars : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(int), typeof(SignalBars), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
        typeof(SignalBars), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(18, 14);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(SignalBars owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(SignalBars);
        // The tooltip carries the numbers (latency, bandwidth).
        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : ((SignalBars)Owner).ToolTip as string ?? "";
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the tooltip shows over the gaps too
        var dim = Foreground.Clone();
        dim.Opacity = 0.3;
        for (var i = 1; i <= 4; i++)
        {
            var height = 2 + i * 3;
            dc.DrawRoundedRectangle(i <= Level ? Foreground : dim, null, new Rect((i - 1) * 4.5 + 1, 14 - height, 3, height), 1, 1);
        }
    }
}
