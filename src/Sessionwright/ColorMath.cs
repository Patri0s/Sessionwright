using System.Globalization;
using System.Windows.Media;

namespace Sessionwright;

/// <summary>Hue / saturation / value for the color picker, and the "#RRGGBB" the data keeps.</summary>
public static class ColorMath
{
    /// <summary><paramref name="h"/> in degrees (0–360), <paramref name="s"/> and <paramref name="v"/> 0–1.</summary>
    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        var m = v - c;
        static byte B(double f) => (byte)Math.Round(Math.Clamp(f, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    public static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>"#RRGGBB", "RRGGBB" or "#RGB" as typed; null when it is none of them.</summary>
    public static Color? FromHex(string? text)
    {
        var hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : null;
    }
}
