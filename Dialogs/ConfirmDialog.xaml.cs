using System.Windows;
using System.Windows.Media;

namespace TeronWoWLauncher.Dialogs;

/// <summary>
/// Generic Yes/No-style confirmation, styled to match the rest of the app instead of falling back to
/// the OS's own MessageBox chrome.
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <param name="confirmBrush">
    /// ConfirmButton's color. Defaults to the shared "positive action" green (AddActionBrush) - most
    /// callers use confirmText for the primary/proceed action. Pass RemoveActionBrush explicitly for
    /// dialogs where confirmText is itself the destructive choice (e.g. "Cancel Download").
    /// </param>
    /// <param name="cancelBrush">CancelButton's color. Null keeps its plain default app-wide button style.</param>
    public ConfirmDialog(string title, string message, string confirmText = "Yes", string cancelText = "No",
        Brush? confirmBrush = null, Brush? cancelBrush = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        ConfirmButton.Background = confirmBrush ?? (Brush)FindResource("AddActionBrush");
        if (cancelBrush is not null)
        {
            CancelButton.Background = cancelBrush;
            CancelButton.BorderThickness = new Thickness(0);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
