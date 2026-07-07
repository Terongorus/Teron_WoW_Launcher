using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Parses WoW's UI escape codes for colored text: <c>|cAARRGGBB</c> starts a color run, <c>|r</c>
/// resets it. Addon authors use these in their .toc "## Title:" line so the in-game addon list shows
/// a styled name (e.g. <c>|cffffcc00Shagu|cffffffffTweaks</c>); we render the same thing ourselves.
/// </summary>
public static class WowColorTextParser
{
    private static readonly Regex Token = new(@"\|c(?<argb>[0-9A-Fa-f]{8})|\|r", RegexOptions.Compiled);

    /// <summary>Splits text into (color-or-null, text) runs in order. A null color means "inherit".</summary>
    public static IReadOnlyList<(Color? Color, string Text)> Parse(string input)
    {
        var result = new List<(Color? Color, string Text)>();
        if (string.IsNullOrEmpty(input))
        {
            return result;
        }

        Color? current = null;
        int pos = 0;

        foreach (Match m in Token.Matches(input))
        {
            if (m.Index > pos)
            {
                result.Add((current, input[pos..m.Index]));
            }

            current = m.Groups["argb"].Success ? ParseArgb(m.Groups["argb"].Value) : null;
            pos = m.Index + m.Length;
        }

        if (pos < input.Length)
        {
            result.Add((current, input[pos..]));
        }

        return result;
    }

    /// <summary>Strips |c......|r escape codes, for plain-text contexts (logs, status bar, dialogs).</summary>
    public static string StripCodes(string input)
        => string.IsNullOrEmpty(input) ? input : Token.Replace(input, string.Empty);

    private static Color ParseArgb(string argb)
    {
        byte a = System.Convert.ToByte(argb[..2], 16);
        byte r = System.Convert.ToByte(argb.Substring(2, 2), 16);
        byte g = System.Convert.ToByte(argb.Substring(4, 2), 16);
        byte b = System.Convert.ToByte(argb.Substring(6, 2), 16);
        return Color.FromArgb(a, r, g, b);
    }
}
