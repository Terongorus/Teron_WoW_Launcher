using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Dlls;

/// <summary>
/// Our own reading/writing of the game's <c>dlls.txt</c> and its <c>dlls.txt.cache</c> companion.
///
/// Reimplements the behaviour of the VanillaFixes loader from scratch:
///   * lines beginning with '#' are comments; blank lines are ignored,
///   * a relative entry is resolved against the WoW directory (not the current directory),
///   * only entries that resolve to an existing file are handed to the injector,
///   * the resolved list is cached in dlls.txt.cache so the UI can warn when it changes.
///
/// <c>dlls.txt</c> lives in the WoW game directory (alongside WoW.exe), so every method takes the
/// WoW directory explicitly rather than assuming a location.
/// </summary>
public sealed class DllListService
{
    public const string DllsFileName = "dlls.txt";
    public const string CacheFileName = "dlls.txt.cache";

    // Blizzard's own runtime DLLs that ship with the base 1.12.1 client — never offered as
    // trackable mods, since tracking/injecting them would be meaningless or harmful.
    private static readonly HashSet<string> BaseGameDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbghelp.dll", "DivxDecoder.dll", "fmod.dll", "ijl15.dll", "unicows.dll", "Scan.dll",
    };

    // The launcher's own binaries, in case it lives in the game folder alongside WoW.exe.
    private static readonly HashSet<string> LauncherOwnDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "TeronWoWLauncher.dll", "SharpCompress.dll",
    };

    private readonly Logger _log = Logger.Instance;

    public string DllsFilePath(string wowDir) => Path.Combine(wowDir, DllsFileName);
    public string CacheFilePath(string wowDir) => Path.Combine(wowDir, CacheFileName);

    /// <summary>Resolves a dlls.txt entry (relative or absolute) to an absolute path, without checking existence.</summary>
    public string ResolvePath(string wowDir, string name) => NormalizePath(wowDir, name);

    /// <summary>
    /// The active DLL entries exactly as written (comments and blank lines removed, each trimmed),
    /// in file order. These are raw names/paths, not yet resolved or existence-checked.
    /// </summary>
    public List<string> ReadActiveNames(string wowDir)
    {
        var result = new List<string>();
        string path = DllsFilePath(wowDir);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                result.Add(line);
            }
        }
        catch (Exception ex)
        {
            // Best-effort, like every other small config-file read here — this runs from
            // MainWindow's own constructor (via RefreshDllList) with no enclosing try/catch, so a
            // locked file or transient permissions error must degrade to "no entries" rather than
            // throwing and taking down the whole app before the window even shows.
            _log.Warn($"Could not read {DllsFileName}: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Resolve raw entries to absolute paths and keep only those that point at an existing file.
    /// Relative entries are resolved against <paramref name="wowDir"/>; missing entries are logged
    /// and skipped (matching the loader, which silently drops anything it cannot find).
    /// </summary>
    public List<string> ResolveForInjection(string wowDir, IEnumerable<string> names)
    {
        var resolved = new List<string>();
        foreach (string name in names)
        {
            string abs = NormalizePath(wowDir, name);
            if (File.Exists(abs))
            {
                resolved.Add(abs);
            }
            else
            {
                _log.Warn($"{DllsFileName}: skipping '{name}' — not found at {abs}");
            }
        }

        return resolved;
    }

    /// <summary>The resolved, existence-checked injection list read straight from disk.</summary>
    public List<string> GetInjectionList(string wowDir)
        => ResolveForInjection(wowDir, ReadActiveNames(wowDir));

    /// <summary>
    /// DLLs sitting directly in the game folder that aren't already tracked in dlls.txt, excluding
    /// Blizzard's own base-game runtime DLLs, the launcher's own binaries, and anything the user has
    /// explicitly ignored (e.g. files that aren't actually meant for injection). Lets the UI offer a
    /// "Refresh" scan instead of requiring every DLL to be added manually via file picker.
    /// </summary>
    public List<string> ScanForUntrackedDlls(string wowDir, IEnumerable<string>? ignored = null)
    {
        var result = new List<string>();
        if (!Directory.Exists(wowDir))
        {
            return result;
        }

        var tracked = new HashSet<string>(ReadActiveNames(wowDir), StringComparer.OrdinalIgnoreCase);
        var ignoredSet = new HashSet<string>(ignored ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(wowDir, "*.dll", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not scan {wowDir} for DLLs: {ex.Message}");
            return result;
        }

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            if (BaseGameDlls.Contains(name) || LauncherOwnDlls.Contains(name) ||
                tracked.Contains(name) || ignoredSet.Contains(name))
            {
                continue;
            }

            result.Add(name);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Persist the active DLL names to dlls.txt. Any leading comment block is preserved so the
    /// user's explanatory header survives edits; entries are written below it in the given order.
    /// </summary>
    public void WriteActiveNames(string wowDir, IReadOnlyList<string> names)
    {
        string path = DllsFilePath(wowDir);
        var lines = new List<string>();

        // Preserve a leading run of comment/blank lines from the existing file as a header.
        if (File.Exists(path))
        {
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                    {
                        lines.Add(raw);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Best-effort — losing the header comment on a transient read failure is harmless;
                // the entries below are what actually matters and still get written.
                _log.Warn($"Could not read the existing {DllsFileName} header: {ex.Message}");
            }
        }
        else
        {
            lines.Add("# Managed by Teron WoW Launcher. One DLL per line; '#' begins a comment.");
        }

        if (lines.Count > 0 && lines[^1].Trim().Length != 0)
        {
            lines.Add(string.Empty);
        }

        lines.AddRange(names);
        AtomicFile.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        _log.Info($"Wrote {names.Count} DLL entr{(names.Count == 1 ? "y" : "ies")} to {DllsFileName}.");
    }

    // --- Change detection via dlls.txt.cache (mirrors the loader's reminder behaviour) ---

    /// <summary>The previously-accepted resolved list, one absolute path per non-blank line.</summary>
    public List<string> ReadCache(string wowDir)
    {
        var result = new List<string>();
        string path = CacheFilePath(wowDir);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length != 0)
                {
                    result.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read {CacheFileName}: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// True when the resolved list differs from the cached list (order-sensitive, case-insensitive
    /// like the Windows file system), or when there is no cache yet but DLLs would be loaded.
    /// </summary>
    public bool HasListChanged(string wowDir, IReadOnlyList<string> resolved)
    {
        List<string> cached = ReadCache(wowDir);
        if (cached.Count == 0)
        {
            return resolved.Count > 0;
        }

        if (cached.Count != resolved.Count)
        {
            return true;
        }

        for (int i = 0; i < resolved.Count; i++)
        {
            if (!string.Equals(resolved[i], cached[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Record the accepted list so the next launch can detect changes. Empty clears it.</summary>
    public void UpdateCache(string wowDir, IReadOnlyList<string> resolved)
    {
        string path = CacheFilePath(wowDir);
        try
        {
            if (resolved.Count == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                AtomicFile.WriteAllText(path, string.Join("\r\n", resolved) + "\r\n");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to update {CacheFileName}: {ex.Message}");
        }
    }

    private static string NormalizePath(string baseDir, string path)
    {
        string p = path.Replace('/', '\\');
        if (!Path.IsPathRooted(p))
        {
            p = Path.Combine(baseDir, p);
        }

        return Path.GetFullPath(p);
    }
}
