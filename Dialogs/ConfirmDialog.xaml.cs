using System.Windows;

namespace TeronWoWLauncher.Dialogs;

/// <summary>
/// Generic Yes/No-style confirmation, styled to match the rest of the app instead of falling back to
/// the OS's own MessageBox chrome.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText = "Yes", string cancelText = "No")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
