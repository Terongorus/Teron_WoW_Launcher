using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Views/edits a DLL's Version/Author/Description. Each field is read-only (with a red
/// notice explaining why, right below its own label) whenever the DLL's own Win32 version resource
/// already defines it — authoritative data baked into the file shouldn't silently diverge from a
/// local edit. A field only becomes user-editable once the live read found nothing for it. Unlike
/// MpqPatchMetadataDialog, "locked" here is decided fresh from the live-extracted DllInfo passed in
/// (not a persisted *IsExtracted flag) — see DllMetadataService's doc comment for why that's safe:
/// re-reading a DLL's version resource is cheap enough to just redo every time.</summary>
public partial class DllMetadataDialog : Window
{
    public string? Version { get; private set; }
    public string? Author { get; private set; }
    public string? Description { get; private set; }

    /// <summary>False whenever a field was read-only (the DLL's own version resource defines it) -
    /// MainWindow's SaveManualEdit call uses these to leave that field completely untouched in that
    /// case, regardless of whatever the disabled box displayed.</summary>
    public bool VersionIsUserEditable { get; }

    public bool AuthorIsUserEditable { get; }

    public bool DescriptionIsUserEditable { get; }

    public DllMetadataDialog(DllInfo extracted, DllMetadata? manual)
    {
        InitializeComponent();

        // Window.Title isn't visible here - this dialog uses locked, caption-less WindowChrome
        // (CaptionHeight="0", same as MpqPatchMetadataDialog), so the file name has to be a real
        // element instead.
        Title = $"DLL Info — {extracted.Name}";
        HeadingText.Text = $"DLL info — {extracted.Name}";

        VersionIsUserEditable = SetUpField(
            VersionBox, VersionNoticeText, extracted.Version, manual?.Version,
            lockedHint: "This DLL's own file info defines its version, which can't be changed here.");

        AuthorIsUserEditable = SetUpField(
            AuthorBox, AuthorNoticeText, extracted.Author, manual?.Author,
            lockedHint: "This DLL's own file info defines its author, which can't be changed here.");

        DescriptionIsUserEditable = SetUpField(
            DescriptionBox, DescriptionNoticeText, extracted.Description, manual?.Description,
            lockedHint: "This DLL's own file info defines its description, which can't be changed here.");
    }

    /// <summary>Populates one field's box (and, if locked, its notice line), returning whether the
    /// field ended up user-editable. <paramref name="extractedValue"/> non-blank means the DLL's own
    /// version resource already supplies this field, so it's shown read-only; otherwise the box shows
    /// whatever manual value is on file (if any) and stays editable.</summary>
    private bool SetUpField(TextBox box, TextBlock notice, string? extractedValue, string? manualValue, string lockedHint)
    {
        bool isLocked = !string.IsNullOrWhiteSpace(extractedValue);
        box.Text = isLocked ? extractedValue : (manualValue ?? string.Empty);

        if (isLocked)
        {
            notice.Text = lockedHint;
            notice.Foreground = (Brush)FindResource("RemoveActionBrush");
            notice.Visibility = Visibility.Visible;
            box.IsReadOnly = true;
            box.IsEnabled = false;
        }

        return !isLocked;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Version = VersionBox.Text.Trim();
        Author = AuthorBox.Text.Trim();
        Description = DescriptionBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
