using System;
using System.Globalization;
using System.Windows.Data;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Scales the narrow-form page card family's (PageCardStyle/PageCardTopStyle/PageCardBottomStyle)
/// MaxWidth off the window's own ActualWidth, instead of a single fixed 720px cap. Below ~1160px
/// window width this clamps to the same 720 the cards always used, so the default 980-wide window
/// and anything near MinWidth look unchanged; above that it grows linearly so a maximized window on
/// a large/ultrawide monitor doesn't strand the card in a sea of empty side margins, capping at 1400
/// so a single column of checkboxes/labels never stretches wide enough to hurt readability.
/// </summary>
public sealed class ResponsiveCardWidthConverter : IValueConverter
{
    private const double Multiplier = 0.62;
    private const double MinCardWidth = 720;
    private const double MaxCardWidth = 1400;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double windowWidth = value is double d ? d : MinCardWidth;
        return Math.Clamp(windowWidth * Multiplier, MinCardWidth, MaxCardWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
