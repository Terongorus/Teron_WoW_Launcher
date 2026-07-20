using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Computes a left Margin from [Value, Minimum, Maximum, IsIndeterminate, track ActualWidth] that
/// centers a fixed-width "spark"/glow element on the fill's current leading edge. ConverterParameter
/// is the spark element's own Width (must match its XAML Width so the centering math is correct).
/// </summary>
public sealed class ProgressBarSparkMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double value, double min, double max, bool isIndeterminate, double trackWidth])
        {
            return new Thickness(0);
        }

        double range = max - min;
        double fraction = range > 0 ? Math.Clamp((value - min) / range, 0.0, 1.0) : 0.0;
        double fillWidth = isIndeterminate ? trackWidth : trackWidth * fraction;
        double sparkWidth = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : 14;

        return new Thickness(Math.Max(0, fillWidth - sparkWidth / 2), 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
