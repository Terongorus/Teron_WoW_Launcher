using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using TeronWoWLauncher.Services;

namespace TeronWoWLauncher.Dialogs;

/// <summary>
/// Shows an addon's README/.toc details full-size, with a checkbox to opt that addon out of update
/// notifications. The checkbox's final state is only read by the caller after the dialog closes
/// (via <see cref="IgnoreUpdates"/>) — there's no separate Save/OK button, matching every other
/// close-only dialog in the app.
/// </summary>
public partial class AddonDetailsDialog : Window
{
    public AddonDetailsDialog(string title, string markdown, bool ignoreUpdates, string? repoUrl = null)
    {
        InitializeComponent();
        Title = title;
        Viewer.Markdown = markdown;
        IgnoreUpdatesCheck.IsChecked = ignoreUpdates;

        if (repoUrl is not null && Uri.TryCreate(repoUrl, UriKind.Absolute, out Uri? uri))
        {
            RepoLink.NavigateUri = uri;
            RepoLinkText.Visibility = Visibility.Visible;
        }

        WindowChromeHelper.FixMaximizedBounds(this);
        MarkdownScrollHelper.AttachFastScroll(Viewer);
    }

    public bool IgnoreUpdates => IgnoreUpdatesCheck.IsChecked == true;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnHyperlink(object sender, ExecutedRoutedEventArgs e)
    {
        string? url = e.Parameter?.ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        OpenUrl(url);
    }

    private void OnRepoLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string url)
    {
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
