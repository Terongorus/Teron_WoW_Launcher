using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TeronWoWLauncher.Dialogs;

/// <summary>
/// Lets the user rename one of an addon's installed folders. Picking which folder only matters for
/// a multi-folder addon (the picker is hidden for the common single-folder case) - the actual
/// rename/collision handling happens in <c>AddonLibrary.RenameFolder</c>, this dialog just collects
/// the input.
/// </summary>
public partial class RenameAddonFolderDialog : Window
{
    public string SelectedFolder => (string)FolderPicker.SelectedItem!;
    public string NewName => NewNameBox.Text.Trim();

    public RenameAddonFolderDialog(IReadOnlyList<string> folders)
    {
        InitializeComponent();

        FolderPicker.ItemsSource = folders;
        FolderPicker.SelectedIndex = 0;
        FolderPicker.Visibility = folders.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        NewNameBox.Text = folders[0];
        Loaded += (_, _) =>
        {
            NewNameBox.Focus();
            NewNameBox.SelectAll();
        };
    }

    private void OnFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderPicker.SelectedItem is string folder)
        {
            NewNameBox.Text = folder;
            NewNameBox.SelectAll();
        }
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        string trimmed = NewNameBox.Text.Trim();
        if (trimmed.Length == 0 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorText.Text = "Enter a valid folder name.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
