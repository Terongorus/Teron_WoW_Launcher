using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// A downloaded addon archive ready to be installed, plus metadata for tracking.
/// <paramref name="RemoteVersionSignature"/> is an opaque signature of the source at download time
/// (release tag, commit sha, or URL ETag/size) used only to detect future updates — the addon's
/// displayed version comes from its own .toc instead.
/// </summary>
public sealed record AddonDownload(
    string ArchivePath,
    string? SuggestedName,
    string? RemoteVersionSignature,
    AddonSourceKind Kind,
    string SourceRef);

/// <summary>Resolves a user-supplied reference (URL or file) into a downloaded addon archive.</summary>
public interface IAddonSource
{
    bool CanHandle(string input);

    Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct);

    /// <summary>
    /// Cheaply check the latest available signature at the source without downloading the addon.
    /// Returns null when there's nothing remote to check (e.g. a local file path).
    /// </summary>
    Task<string?> GetLatestVersionSignatureAsync(string input, HttpClient http, CancellationToken ct);
}

/// <summary>Picks the right <see cref="IAddonSource"/> for a given input; archive is the fallback.</summary>
public sealed class AddonSourceResolver
{
    private readonly IAddonSource[] _sources =
    {
        new GitHubAddonSource(),
        new DirectArchiveAddonSource(), // fallback, must stay last
    };

    public IAddonSource Resolve(string input) => _sources.First(s => s.CanHandle(input));
}
