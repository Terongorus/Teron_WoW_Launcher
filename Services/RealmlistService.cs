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

    /// <summary>The current realm host from realmlist.wtf, or null if not set.</summary>
    public string? Read(string wowDir)
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

    /// <summary>
    /// Write realmlist.wtf as <c>set realmlist &lt;host&gt;</c>. Accepts either a bare host
    /// ("logon.example.com") or a full "set realmlist logon.example.com" line.
    /// </summary>
    public void Write(string wowDir, string realmlist)
    {
        if (string.IsNullOrWhiteSpace(realmlist))
        {
            return;
        }

        string value = realmlist.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..].Trim();
        }

        File.WriteAllText(RealmlistPath(wowDir), $"set realmlist {value}\r\n");
        _log.Info($"Wrote {FileName}: set realmlist {value}");
    }
}
