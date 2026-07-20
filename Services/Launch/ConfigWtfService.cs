using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Launch;

/// <summary>
/// Surgically edits specific <c>WTF\Config.WTF</c> keys — a line-level upsert/remove, never a blind
/// append or whole-file rewrite. This matters because two of the required keys
/// (<c>gxColorBits</c>/<c>gxDepthBits</c>) are also driven by the game's own in-game Video Settings
/// "Color/Depth/Multisample" dropdown (one control, three CVars): a user who played before ever
/// enabling our toggle may already have those two set, at whatever multisample level they chose.
/// Every edit here preserves every other line (comments, unrelated keys, the multisample CVar,
/// ordering) byte-for-byte, touching only the specific key(s) requested.
/// </summary>
public sealed class ConfigWtfService
{
    private const string RelativePath = "WTF\\Config.WTF";

    /// <summary>
    /// The SET lines some HD/visual MPQ patches (e.g. Project Reforged "Kronos") require to render.
    /// Defined once so the Tweaks-tab description and the actual write can't drift apart.
    /// </summary>
    public static readonly (string Key, string Value)[] RequiredSettings =
    {
        ("gxApi", "d3d9"),
        ("M2UseShaders", "1"),
        ("M2UsePixelShaders", "1"),
        ("M2UseThreaded", "1"),
        ("gxColorBits", "24"),
        ("gxDepthBits", "24"),
        ("M2Faster", "3"),
    };

    // Key is bare; value is quoted - e.g. SET gxApi "d3d9". Blizzard's own writer casing on the key
    // isn't guaranteed, so matching is case-insensitive.
    private static readonly Regex SetLine = new(
        @"^\s*SET\s+(\S+)\s+""([^""]*)""\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Logger _log = Logger.Instance;

    public string ConfigWtfPath(string wowDir) => Path.Combine(wowDir, RelativePath);

    /// <summary>
    /// The current value of a single key, or null if absent (or the file/folder doesn't exist yet -
    /// e.g. a client that has never been launched). Best-effort like every other small config-file
    /// read in this codebase: a locked file or transient permissions error degrades to null rather
    /// than throwing.
    /// </summary>
    public string? ReadKey(string wowDir, string key)
    {
        try
        {
            string path = ConfigWtfPath(wowDir);
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                Match m = SetLine.Match(line);
                if (m.Success && string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase))
                {
                    return m.Groups[2].Value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not read Config.WTF key '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Snapshot of several keys' current values at once, for capturing a pre-toggle restore point.</summary>
    public Dictionary<string, string?> ReadAll(string wowDir, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            result[key] = ReadKey(wowDir, key);
        }

        return result;
    }

    /// <summary>
    /// Force every key in <see cref="RequiredSettings"/> to its required value. Idempotent - calling
    /// this again with nothing else having changed the file produces byte-identical content, which is
    /// what lets this be safely re-run at the start of every Play session (the client itself may have
    /// legitimately rewritten gxColorBits/gxDepthBits between sessions via the video dropdown).
    /// </summary>
    public void ApplyRequiredSettings(string wowDir)
        => Upsert(wowDir, RequiredSettings.Select(s => (s.Key, (string?)s.Value)));

    /// <summary>
    /// Restore each key to the value captured before the toggle last forced it. A null value means the
    /// key didn't exist before we touched it, so it's removed entirely rather than left behind.
    /// </summary>
    public void RestoreValues(string wowDir, IReadOnlyDictionary<string, string?> originalValues)
        => Upsert(wowDir, originalValues.Select(kv => (kv.Key, kv.Value)));

    private void Upsert(string wowDir, IEnumerable<(string Key, string? Value)> edits)
    {
        string path = ConfigWtfPath(wowDir);
        List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

        foreach ((string key, string? value) in edits)
        {
            int index = lines.FindIndex(line =>
            {
                Match m = SetLine.Match(line);
                return m.Success && string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase);
            });

            if (value is null)
            {
                if (index >= 0)
                {
                    lines.RemoveAt(index);
                }

                continue;
            }

            string newLine = $"SET {key} \"{value}\"";
            if (index >= 0)
            {
                lines[index] = newLine;
            }
            else
            {
                lines.Add(newLine);
            }
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string content = lines.Count > 0 ? string.Join("\r\n", lines) + "\r\n" : string.Empty;
        AtomicFile.WriteAllText(path, content);
    }
}
