using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sessionwright;

/// <summary>A favorite's number for Ctrl+1…9 from its place in the list (the first nine have one).</summary>
public static class FavoriteNumber
{
    public static readonly IValueConverter Text = new Converter(i => i < 9 ? $"{i + 1}" : "");
    public static readonly IValueConverter Shown = new Converter(i => i < 9 ? Visibility.Visible : Visibility.Collapsed);

    sealed class Converter(Func<int, object> convert) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => convert(value is int i ? i : int.MaxValue);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
