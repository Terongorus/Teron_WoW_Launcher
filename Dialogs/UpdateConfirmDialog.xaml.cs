using System.Windows;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Confirms updating the installed client, showing what's known to have changed remotely.</summary>
public partial class UpdateConfirmDialog : Window
{
    public UpdateConfirmDialog(string detail)
    {
        InitializeComponent();
        DetailText.Text = detail;
    }

    private void OnUpdate(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
