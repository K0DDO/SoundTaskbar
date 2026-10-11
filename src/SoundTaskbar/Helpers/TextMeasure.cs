using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SoundTaskbar.Helpers;

internal static class TextMeasure
{
    public static double Width(string? text, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Segoe UI"),
                FontStyles.Normal,
                weight,
                FontStretches.Normal),
            fontSize,
            Brushes.White,
            pixelsPerDip: 1.25);

        return ft.WidthIncludingTrailingWhitespace;
    }
}
