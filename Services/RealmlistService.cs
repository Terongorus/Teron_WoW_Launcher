using System;
using System.IO;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Reads and writes the game's <c>realmlist.wtf</c>. Accepting any value here is what lets the
/// launcher connect to any private vanilla server, not a fixed list of realms.
/// </summary>
public sealed class RealmlistService
{
    private const string FileName = "realmlist.wtf";
    private const string Prefix = "set realmlist";

    private readonly Logger _log = Logger.Instance;

    public string RealmlistPath(string wowDir) => Path.Combine(wowDir, FileName);

    /// <summary>
    /// The current realm host from realmlist.wtf, or null if not set (or unreadable — best-effort,
    /// like every other small config-file read in this codebase; a locked file or transient
    /// permissions error degrades to "not set" rather than throwing).
    /// </summary>
    public string? Read(string wowDir)
    {
        try
        {
            string path = RealmlistPath(wowDir);
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[Prefix.Length..].Trim();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not read {FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Write realmlist.wtf as <c>set realmlist &lt;host&gt;</c>. Accepts either a bare host
    /// ("logon.example.com") or a full "set realmlist logon.example.com" line.
    ///
    /// Returns false (and logs a warning) if reading the file back afterward doesn't show what was
    /// just written — a safeguard against a silent write failure (disk oddity, antivirus, something
    /// else racing to touch the same file) that would otherwise only surface later as unexplained
    /// "wrong realm" confusion at the login screen, with nothing in the log pointing at the cause.
    /// </summary>
    public bool Write(string wowDir, string realmlist)
    {
        if (string.IsNullOrWhiteSpace(realmlist))
        {
            return false;
        }

        string value = realmlist.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..].Trim();
        }

        AtomicFile.WriteAllText(RealmlistPath(wowDir), $"set realmlist {value}\r\n");
        _log.Info($"Wrote {FileName}: set realmlist {value}");

        string? confirmed = Read(wowDir);
        if (!string.Equals(confirmed, value, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"{FileName} may not have been written correctly — expected realmlist " +
                      $"'{value}' but read back '{confirmed ?? "(none)"}'.");
            return false;
        }

        return true;
    }
}
