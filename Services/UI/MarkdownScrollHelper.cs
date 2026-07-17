using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Markdig.Wpf;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// FlowDocumentScrollViewer's own default mouse-wheel handling scrolls a small fixed amount per
/// notch — noticeably slower than every other scrollable list/panel in the app. This finds the
/// MarkdownViewer's internal ScrollViewer and scrolls it directly, by a larger amount, matching the
/// rest of the app's feel. Shared by every dialog that shows a full-size MarkdownViewer (Markdown
/// preview, addon details), instead of each one reimplementing the same wheel handler.
/// </summary>
public static class MarkdownScrollHelper
{
    private const double PixelsPerWheelNotch = 120;

    public static void AttachFastScroll(MarkdownViewer viewer)
    {
        ScrollViewer? scrollViewer = null;
        viewer.PreviewMouseWheel += (_, e) =>
        {
            scrollViewer ??= FindVisualChild<ScrollViewer>(viewer);
            if (scrollViewer is null)
            {
                return;
            }

            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 120.0 * PixelsPerWheelNotch);
            e.Handled = true;
        };
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }

            if (FindVisualChild<T>(child) is T found)
            {
                return found;
            }
        }

        return null;
    }
}
