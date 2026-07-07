using System.Windows;
using Microsoft.Win32;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Prompts for a GitHub/archive URL, or a local archive file, to install as an addon.</summary>
public partial class AddAddonDialog : Window
{
    public string? Input { get; private set; }

    public AddAddonDialog()
    {
        InitializeComponent();
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an addon archive",
            Filter = "Addon archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            UrlBox.Text = dialog.FileName;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            return;
        }

        Input = UrlBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
