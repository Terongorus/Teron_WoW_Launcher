using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Views/edits an MPQ patch's metadata. Each of Title/Version/Author/Description is
/// read-only (with a red notice explaining why, right below its own label) whenever the patch's own
/// Patch.toc (or a bundled readme-style file, for Author/Description) already defines it -
/// authoritative data baked into the patch shouldn't silently diverge from a local edit. A field only
/// becomes user-editable once nothing was extracted for it. Website (when present) is shown
/// read-only above everything else as a clickable link - see MpqPatchMetadata's doc comment for why
/// that one's never exposed as an editable field at all.</summary>
public partial class MpqPatchMetadataDialog : Window
{
    public string? PatchTitle { get; private set; }
    public string? Version { get; private set; }
    public string? Author { get; private set; }
    public string? Description { get; private set; }

    /// <summary>False whenever a field was read-only (the patch defines its own) - MainWindow's
    /// SaveManualEdit call uses these to leave that field/its provenance flag completely untouched in
    /// that case, regardless of whatever the disabled box displayed.</summary>
    public bool TitleIsUserEditable { get; }

    public bool VersionIsUserEditable { get; }

    public bool AuthorIsUserEditable { get; }

    public bool DescriptionIsUserEditable { get; }

    public MpqPatchMetadataDialog(string fileName, MpqPatchMetadata? existing)
    {
        InitializeComponent();

        // Window.Title isn't visible here - this dialog uses locked, caption-less WindowChrome
        // (CaptionHeight="0"), so the file name has to be a real element instead. Distinct from the
        // patch's own Title field below (that's a user-facing friendly name; this is always the raw
        // slot filename, e.g. "patch-A.mpq", matching what the row list shows in parentheses).
        HeadingText.Text = $"Patch info — {fileName}";

        TitleIsUserEditable = SetUpField(
            TitleBox, TitleNoticeText, existing?.Title, existing?.TitleIsExtracted == true,
            editableHint: $"Shown as \"{fileName}\" when no title is set below.",
            lockedHint: "This patch defines its own title, which can't be changed here.");

        VersionIsUserEditable = SetUpField(
            VersionBox, VersionNoticeText, existing?.Version, existing?.VersionIsExtracted == true,
            editableHint: null,
            lockedHint: "This patch defines its own version, which can't be changed here.");

        AuthorIsUserEditable = SetUpField(
            AuthorBox, AuthorNoticeText, existing?.Author, existing?.AuthorIsExtracted == true,
            editableHint: null,
            lockedHint: "This patch defines its own author, which can't be changed here.");

        DescriptionIsUserEditable = SetUpField(
            DescriptionBox, DescriptionNoticeText, existing?.Description, existing?.DescriptionIsExtracted == true,
            editableHint: null,
            lockedHint: "This patch defines its own description, which can't be changed here.");

        BuildExtractedInfo(existing);
    }

    /// <summary>Populates one field's box and (if applicable) its notice line, returning whether the
    /// field ended up user-editable.</summary>
    private bool SetUpField(TextBox box, TextBlock notice, string? value, bool isExtracted, string? editableHint, string lockedHint)
    {
        box.Text = value ?? string.Empty;

        bool isLocked = isExtracted && !string.IsNullOrWhiteSpace(value);
        if (isLocked)
        {
            notice.Text = lockedHint;
            notice.Foreground = (Brush)FindResource("RemoveActionBrush");
            notice.Visibility = Visibility.Visible;
            box.IsReadOnly = true;
            box.IsEnabled = false;
        }
        else if (editableHint is not null)
        {
            notice.Text = editableHint;
            notice.Visibility = Visibility.Visible;
        }

        return !isLocked;
    }

    /// <summary>Website only now - Version moved into its own field/row above.</summary>
    private void BuildExtractedInfo(MpqPatchMetadata? existing)
    {
        if (string.IsNullOrWhiteSpace(existing?.Website))
        {
            return;
        }

        ExtractedInfoText.Inlines.Clear();
        if (Uri.TryCreate(existing.Website, UriKind.Absolute, out Uri? uri))
        {
            var link = new Hyperlink(new Run(existing.Website))
            {
                NavigateUri = uri,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x67)),
            };
            link.RequestNavigate += OnWebsiteNavigate;
            ExtractedInfoText.Inlines.Add(link);
        }
        else
        {
            ExtractedInfoText.Inlines.Add(new Run(existing.Website));
        }

        WebsiteLabel.Visibility = Visibility.Visible;
        ExtractedInfoText.Visibility = Visibility.Visible;
    }

    private void OnWebsiteNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; nothing sensible to do if the shell can't handle the link.
        }

        e.Handled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        PatchTitle = TitleBox.Text.Trim();
        Version = VersionBox.Text.Trim();
        Author = AuthorBox.Text.Trim();
        Description = DescriptionBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
