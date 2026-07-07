using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeronWoWLauncher.Services;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Shows a Markdown string full-size, with normal scrolling — opened from a compact preview.</summary>
public partial class MarkdownPreviewDialog : Window
{
    // FlowDocumentScrollViewer's own default wheel handling scrolls a small fixed amount per notch —
    // noticeably slower than every other scrollable list/panel in the app. Scrolling its internal
    // ScrollViewer directly, by a larger amount, matches the rest of the app's feel.
    private const double PixelsPerWheelNotch = 120;

    private ScrollViewer? _viewerScroll;

    public MarkdownPreviewDialog(string title, string markdown)
    {
        InitializeComponent();
        Title = title;
        Viewer.Markdown = markdown;
        WindowChromeHelper.FixMaximizedBounds(this);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _viewerScroll ??= FindVisualChild<ScrollViewer>(Viewer);
        if (_viewerScroll is null)
        {
            return;
        }

        _viewerScroll.ScrollToVerticalOffset(
            _viewerScroll.VerticalOffset - e.Delta / 120.0 * PixelsPerWheelNotch);
        e.Handled = true;
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

    private void OnHyperlink(object sender, ExecutedRoutedEventArgs e)
    {
        string? url = e.Parameter?.ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; nothing sensible to do if the shell can't handle the link.
        }
    }
}
