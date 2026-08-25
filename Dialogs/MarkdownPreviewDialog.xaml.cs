using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using TeronWoWLauncher.Services.Addons;
using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Dlls;
using TeronWoWLauncher.Services.Launch;
using TeronWoWLauncher.Services.Patching;
using TeronWoWLauncher.Services.UI;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Shows a Markdown string full-size, with normal scrolling — opened from a compact preview.</summary>
public partial class MarkdownPreviewDialog : Window
{
    public MarkdownPreviewDialog(string title, string markdown)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        Viewer.Markdown = markdown;
        WindowChromeHelper.FixMaximizedBounds(this);
        MarkdownScrollHelper.AttachFastScroll(Viewer);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

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
