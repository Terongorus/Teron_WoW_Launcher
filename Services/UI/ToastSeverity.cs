namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Drives a toast's color and dismiss behavior - Info/Success auto-dismiss after a few seconds;
/// Warning/Error require the user to close them manually, since those are more important not to
/// miss (issue #13). Colors reuse the shared action-color brushes from App.xaml (issue #14) rather
/// than introducing a separate palette: Info=InstallActionBrush (blue), Success=AddActionBrush
/// (green), Warning=UpdateActionBrush (gold), Error=RemoveActionBrush (red).
/// </summary>
public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error,
}
