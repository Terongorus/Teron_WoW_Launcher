using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Scales the Home tab's three-column Margin off the window's own ActualWidth, instead of a fixed
/// 50,40,50,40 on every column regardless of window size. That fixed margin was a hard floor eating
/// into content width right at the enforced MinWidth (900) — each column only got ~200px of real
/// content there. This ramps the margin from 20/16 at MinWidth up to the original 50/40 by 1100px
/// wide, then holds there, so only windows resized down near the minimum get the extra room; the
/// default 980-wide window and anything larger keeps the original look.
/// </summary>
public sealed class ResponsiveColumnMarginConverter : IValueConverter
{
    private const double MinWidth = 900;
    private const double PlateauWidth = 1100;
    private const double MinHorizontal = 20;
    private const double MaxHorizontal = 50;
    private const double VerticalRatio = 0.8;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double windowWidth = value is double d ? d : MinWidth;
        double t = Math.Clamp((windowWidth - MinWidth) / (PlateauWidth - MinWidth), 0.0, 1.0);
        double horizontal = MinHorizontal + t * (MaxHorizontal - MinHorizontal);
        double vertical = horizontal * VerticalRatio;
        return new Thickness(horizontal, vertical, horizontal, vertical);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
