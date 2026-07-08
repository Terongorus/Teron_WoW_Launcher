using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Converts a ScrollViewer's ScrollableHeight/ScrollableWidth to Visibility — used for a standalone
/// ScrollBar bound to a ScrollViewer whose own VerticalScrollBarVisibility is fixed to "Hidden" (so its
/// built-in bar never draws). ComputedVerticalScrollBarVisibility isn't usable for that case: it just
/// echoes the fixed "Hidden" value instead of reflecting whether content actually overflows, the way it
/// would if the ScrollViewer's own visibility were "Auto".
/// </summary>
public sealed class ScrollableToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double scrollable = value is double d ? d : 0;
        return scrollable > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
