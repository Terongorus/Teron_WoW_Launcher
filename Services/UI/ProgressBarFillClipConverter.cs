using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Builds a plain rectangular clip geometry [0,0,fillWidth,trackHeight] from [Value, Minimum,
/// Maximum, IsIndeterminate, track ActualWidth, track ActualHeight] - used to reveal only the filled
/// portion of AnimatedProgressBarStyle's stripe overlay, which is otherwise a constant, full-track-
/// width Rectangle whose tile pattern never has to re-anchor as progress updates (unlike sizing the
/// Rectangle itself to the fill width directly, which visibly seamed mid-bar - the tiled brush's
/// rasterization didn't stay consistent across the frequent per-tick resizes while the scroll
/// transform was also animating, confirmed via a screenshot showing a clear seam partway across the
/// bar, not just at the trailing edge). Clipping instead of resizing keeps the underlying pattern's
/// own coordinate space stable; only the visible window into it grows.
/// </summary>
public sealed class ProgressBarFillClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double value, double min, double max, bool isIndeterminate, double trackWidth, double trackHeight])
        {
            return Geometry.Empty;
        }

        double range = max - min;
        double fraction = range > 0 ? Math.Clamp((value - min) / range, 0.0, 1.0) : 0.0;
        double width = isIndeterminate ? trackWidth : trackWidth * fraction;
        return new RectangleGeometry(new Rect(0, 0, Math.Max(0, width), Math.Max(0, trackHeight)));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
