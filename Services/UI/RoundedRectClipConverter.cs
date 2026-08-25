using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Builds a rounded-rectangle clip geometry from [ActualWidth, ActualHeight] - Border does NOT
/// automatically clip its Child to its own CornerRadius (only ClipToBounds, which clips to the plain
/// rectangular bounding box), so a rounded container showing square-cornered child content
/// (e.g. AnimatedProgressBarStyle's indicator/stripe overlay visibly poking past the track's rounded
/// corners) needs an explicit Clip geometry like this instead. ConverterParameter is the corner
/// radius (defaults to 0, a plain rectangle, if omitted or unparsable).
/// </summary>
public sealed class RoundedRectClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double width, double height] || width <= 0 || height <= 0)
        {
            return Geometry.Empty;
        }

        double radius = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : 0;
        return new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
