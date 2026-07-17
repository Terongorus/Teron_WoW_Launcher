using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Prompts for the folder to install the vanilla client into, defaulting to whatever game
/// folder (if any) is already configured — never the launcher's own folder, which has no relation to
/// any WoW installation.</summary>
public partial class InstallDialog : Window
{
    public string SelectedFolder { get; private set; } = string.Empty;

    public InstallDialog(string defaultFolder)
    {
        InitializeComponent();
        FolderBox.Text = defaultFolder;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the install folder" };
        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            return;
        }

        SelectedFolder = FolderBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
