using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Combines "is this sub-tab the active one" (bool) with "is there anything to scroll" (double,
/// e.g. a ScrollViewer's ScrollableHeight or a synced standalone ScrollBar's own Maximum) into a
/// single Visibility. Needed because a page with an Installed/Browse-style sub-tab toggle has more
/// than one candidate ScrollBar sharing the same Grid cell - a ScrollBar gated on scrollable-content
/// alone (via <see cref="ScrollableToVisibilityConverter"/>) stays visible even while its own
/// sub-tab isn't the one currently shown, since switching sub-tabs doesn't change that ScrollBar's
/// own ScrollViewer/content at all.
/// </summary>
public sealed class TabScrollBarVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isActiveSubTab = values.Length > 0 && values[0] is true;
        double scrollable = values.Length > 1 && values[1] is double d ? d : 0;
        return isActiveSubTab && scrollable > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
