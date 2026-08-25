using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Colors a Log tab row by severity: muted for Debug (de-emphasized detail), normal body color for
/// Info (the common case — reads as unremarkable), amber for Warn, red for Error — so problems are
/// scannable at a glance instead of every line reading identically.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly Brush DebugBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x72));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0x92, 0x2B));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB3, 0x3A, 0x3A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => DebugBrush,
                LogLevel.Warn => WarnBrush,
                LogLevel.Error => ErrorBrush,
                _ => InfoBrush,
            }
            : InfoBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
