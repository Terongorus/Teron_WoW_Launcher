using System;
using System.Globalization;
using System.Windows.Data;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Computes the animated progress indicator's pixel width from [Value, Minimum, Maximum,
/// IsIndeterminate, track ActualWidth]. Width-based (not a RenderTransform ScaleTransform) so the
/// diagonal-stripe overlay tiled inside the indicator stays a fixed visual size regardless of fill
/// percentage — scaling the whole indicator would squash the stripe pattern thinner as the fill
/// percentage dropped, which reads wrong. When IsIndeterminate is true, returns the full track width
/// (a fully-filled, animated bar signals "working, no known duration" rather than any specific %).
/// </summary>
public sealed class ProgressBarFillWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double value, double min, double max, bool isIndeterminate, double trackWidth])
        {
            return 0.0;
        }

        if (isIndeterminate)
        {
            return trackWidth;
        }

        double range = max - min;
        double fraction = range > 0 ? Math.Clamp((value - min) / range, 0.0, 1.0) : 0.0;
        return trackWidth * fraction;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
