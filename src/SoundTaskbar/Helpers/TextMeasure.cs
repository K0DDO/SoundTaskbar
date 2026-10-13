using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SoundTaskbar.Helpers;

internal static class TextMeasure
{
    private static readonly FontFamily Segoe = new("Segoe UI");
    private static readonly Typeface SemiBold = new(Segoe, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Normal = new(Segoe, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static double Width(string? text, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // Layout widths are in DIPs; pixelsPerDip=1 keeps measure DPI-independent for clamps.
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            weight == FontWeights.SemiBold ? SemiBold : Normal,
            fontSize,
            Brushes.White,
            pixelsPerDip: 1.0);

        return ft.WidthIncludingTrailingWhitespace;
    }
}
