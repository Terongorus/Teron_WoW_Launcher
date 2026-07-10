using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Tracks and toggles custom MPQ patches in the game's Data folder.
///
/// A custom patch is a file named <c>patch-&lt;A-Z&gt;.mpq</c>. It is enabled when present under that
/// name and disabled by prefixing an underscore (<c>_patch-&lt;A-Z&gt;.mpq</c>), which the client skips.
/// Base Blizzard archives (patch.mpq, patch-2.mpq, locale patches, dbc/model/… .mpq) never match this
/// pattern and are therefore never touched or listed.
/// </summary>
public sealed class MpqPatchService
{
    private const string DataFolderName = "Data";

    // Matches custom patches only: an optional leading underscore (disabled), then patch-<letter>.mpq.
    private static readonly Regex CustomPatchRegex =
        new(@"^(?<disabled>_)?patch-(?<letter>[A-Za-z])\.mpq$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Logger _log = Logger.Instance;

    public string DataDirectory(string wowDir) => Path.Combine(wowDir, DataFolderName);

    /// <summary>All custom MPQ patches in the Data folder, sorted by slot letter. Best-effort: a
    /// transient scan failure (locked folder, permissions hiccup) degrades to "none found" rather
    /// than throwing, since this is reachable from settings-save/refresh paths with no enclosing
    /// handler.</summary>
    public List<MpqPatch> Scan(string wowDir)
    {
        var result = new List<MpqPatch>();
        string data = DataDirectory(wowDir);
        if (!Directory.Exists(data))
        {
            return result;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(data, "*.mpq").ToList();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not scan {data} for MPQ patches: {ex.Message}");
            return result;
        }

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            Match m = CustomPatchRegex.Match(name);
            if (!m.Success)
            {
                continue;
            }

            result.Add(new MpqPatch
            {
                Letter = m.Groups["letter"].Value.ToUpperInvariant(),
                FileName = name,
                Enabled = !m.Groups["disabled"].Success,
            });
        }

        return result.OrderBy(p => p.Letter, System.StringComparer.Ordinal).ToList();
    }

    /// <summary>Enable or disable a patch by renaming between patch-X.mpq and _patch-X.mpq.</summary>
    public void SetEnabled(string wowDir, MpqPatch patch, bool enabled)
    {
        string data = DataDirectory(wowDir);
        string current = Path.Combine(data, patch.FileName);
        string target = Path.Combine(data, EnabledName(patch.Letter, enabled));

        if (string.Equals(current, target, System.StringComparison.OrdinalIgnoreCase) || !File.Exists(current))
        {
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(current, target);
        _log.Info($"MPQ patch {patch.Letter} {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>Copy an external .mpq into the Data folder using the next free slot letter (A–Z).</summary>
    public MpqPatch? Add(string wowDir, string sourceMpqPath)
    {
        string data = DataDirectory(wowDir);
        Directory.CreateDirectory(data);

        var used = new HashSet<string>(Scan(wowDir).Select(p => p.Letter));
        char? free = null;
        for (char c = 'A'; c <= 'Z'; c++)
        {
            if (!used.Contains(c.ToString()))
            {
                free = c;
                break;
            }
        }

        if (free is null)
        {
            _log.Error("Cannot add MPQ patch: all custom slots A–Z are in use.");
            return null;
        }

        string destName = EnabledName(free.Value.ToString(), enabled: true);
        File.Copy(sourceMpqPath, Path.Combine(data, destName));
        _log.Info($"Added custom MPQ patch '{Path.GetFileName(sourceMpqPath)}' as {destName}.");

        return new MpqPatch { Letter = free.Value.ToString(), FileName = destName, Enabled = true };
    }

    /// <summary>Delete a custom patch file from the Data folder.</summary>
    public void Remove(string wowDir, MpqPatch patch)
    {
        string path = Path.Combine(DataDirectory(wowDir), patch.FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _log.Info($"Removed MPQ patch {patch.Letter} ({patch.FileName}).");
        }
    }

    private static string EnabledName(string letter, bool enabled)
        => (enabled ? string.Empty : "_") + $"patch-{letter}.mpq";
}
