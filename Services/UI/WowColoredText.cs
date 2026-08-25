using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Attached property that renders a WoW-color-coded string (see <see cref="WowColorTextParser"/>) into
/// a <see cref="TextBlock"/>'s <see cref="TextBlock.Inlines"/> as colored <see cref="Run"/>s, so addon
/// names with embedded <c>|cAARRGGBB...|r</c> codes display the same way they would in-game.
/// </summary>
public static class WowColoredText
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(WowColoredText), new PropertyMetadata(null, OnTextChanged));

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(TextBlock element) => (string?)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        string text = e.NewValue as string ?? string.Empty;

        foreach ((Color? color, string segment) in WowColorTextParser.Parse(text))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var run = new Run(segment);
            if (color is Color c)
            {
                run.Foreground = new SolidColorBrush(c);
            }

            textBlock.Inlines.Add(run);
        }
    }
}
