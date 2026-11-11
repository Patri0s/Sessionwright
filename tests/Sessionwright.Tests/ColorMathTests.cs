using System.Windows.Media;
using Sessionwright;
using Xunit;

public class ColorMathTests
{
    // The picker goes through hue / saturation / value and back: every color comes back as it was.
    [Theory]
    [InlineData("#E5534B")]
    [InlineData("#2EA043")]
    [InlineData("#3B82F6")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#8B949E")]
    [InlineData("#C04CFD")]
    public void RoundTripsThroughHsv(string hex)
    {
        var color = ColorMath.FromHex(hex)!.Value;
        var (h, s, v) = ColorMath.ToHsv(color);
        Assert.Equal(hex, ColorMath.ToHex(ColorMath.FromHsv(h, s, v)));
    }

    [Fact]
    public void KnowsThePrimaries()
    {
        Assert.Equal(Colors.Red, ColorMath.FromHsv(0, 1, 1));
        Assert.Equal(Color.FromRgb(0, 255, 0), ColorMath.FromHsv(120, 1, 1));
        Assert.Equal(Colors.Blue, ColorMath.FromHsv(240, 1, 1));
        Assert.Equal(Colors.Red, ColorMath.FromHsv(360, 1, 1)); // the hue wraps around
    }

    [Theory]
    [InlineData("#c04cfd", "#C04CFD")]
    [InlineData("C04CFD", "#C04CFD")]
    [InlineData(" #f0a ", "#FF00AA")]
    [InlineData("#12345", null)]
    [InlineData("zielony", null)]
    [InlineData("", null)]
    public void ReadsHexAsTyped(string text, string? expected) =>
        Assert.Equal(expected, ColorMath.FromHex(text) is { } c ? ColorMath.ToHex(c) : null);
}
