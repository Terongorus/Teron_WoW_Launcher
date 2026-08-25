using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using TeronWoWLauncher.Services.Addons;
using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Dlls;
using TeronWoWLauncher.Services.Launch;
using TeronWoWLauncher.Services.Patching;
using TeronWoWLauncher.Services.UI;

namespace TeronWoWLauncher.Dialogs;

/// <summary>
/// Shows an addon's README/.toc details full-size, with a checkbox to opt that addon out of update
/// notifications. The checkbox's final state is only read by the caller after the dialog closes
/// (via <see cref="IgnoreUpdates"/>) — there's no separate Save/OK button, matching every other
/// close-only dialog in the app.
/// </summary>
public partial class AddonDetailsDialog : Window
{
    public AddonDetailsDialog(string title, string markdown, bool ignoreUpdates, string? repoUrl = null, bool showIgnoreUpdates = true, string repoLinkLabel = "View on GitHub")
    {
        InitializeComponent();
        Title = title;
        Viewer.Markdown = markdown;
        IgnoreUpdatesCheck.IsChecked = ignoreUpdates;
        // Browse results aren't tracked addons yet - "ignore updates" has nothing to apply to until
        // one is actually installed, so that checkbox is hidden rather than shown-but-meaningless.
        IgnoreUpdatesCheck.Visibility = showIgnoreUpdates ? Visibility.Visible : Visibility.Collapsed;

        if (repoUrl is not null && Uri.TryCreate(repoUrl, UriKind.Absolute, out Uri? uri))
        {
            RepoLink.NavigateUri = uri;
            RepoLinkRun.Text = repoLinkLabel;
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
