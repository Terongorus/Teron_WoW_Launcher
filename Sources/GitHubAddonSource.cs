using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services.Addons;
using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Dlls;
using TeronWoWLauncher.Services.Launch;
using TeronWoWLauncher.Services.Patching;
using TeronWoWLauncher.Services.UI;

namespace TeronWoWLauncher.Sources;

/// <summary>
/// Installs an addon from a GitHub repository URL via a real git clone — the primary source for
/// 1.12.1 addons, since the ecosystem is GitHub-centric with no central API. Each tracked repo gets
/// a persistent local clone under <see cref="AddonPaths.AddonRepoCacheDir"/>, kept across app runs
/// so update checks and pulls are incremental instead of a fresh download every time.
///
/// This deliberately avoids the api.github.com REST API entirely: it has an unauthenticated 60/hour
/// rate limit that a handful of tracked addons can exhaust in one Refresh, and previously caused
/// both false "no update" (a stale release tag hiding real commits pushed straight to the branch)
/// and false "update available" results (a rate-limited resolution silently corrupting the stored
/// baseline). Git's own smart-HTTP protocol — what `git clone`/`git ls-remote` use — isn't subject
/// to that quota, and a commit sha is an unambiguous, single-format signature with no release-vs-
/// branch mode-flapping possible.
/// </summary>
public sealed class GitHubAddonSource : IAddonSource
{
    private static readonly Regex RepoRegex =
        new(@"github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Logger _log = Logger.Instance;

    public bool CanHandle(string input)
        => input.Contains("github.com", StringComparison.OrdinalIgnoreCase) && RepoRegex.IsMatch(input);

    public async Task<AddonDownload> DownloadAsync(string input, HttpClient http, CancellationToken ct)
    {
        (string owner, string repo) = ParseOwnerRepo(input);
        string cloneUrl = $"https://github.com/{owner}/{repo}.git";
        string cacheDir = AddonPaths.AddonRepoCacheDir(owner, repo);
        string contentDir = Path.Combine(Path.GetTempPath(), $"teronwow_addon_{Guid.NewGuid():N}");

        // Off the calling thread deliberately - both the clone/fetch and the working-tree copy are
        // synchronous LibGit2Sharp/file I/O with no await in them, and DownloadAsync is always
        // awaited directly from the UI thread (AddonLibrary.AddAsync). The clone alone was already
        // backgrounded, but the copy step wasn't, which could still noticeably freeze the window for
        // a large repo.
        string headSha = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using Repository repo2 = EnsureUpToDateClone(cloneUrl, cacheDir);
            string sha = repo2.Head.Tip.Sha;
            CopyWorkingTree(cacheDir, contentDir);
            return sha;
        }, ct);

        return new AddonDownload(contentDir, repo, headSha, AddonSourceKind.GitHub, $"https://github.com/{owner}/{repo}");
    }

    public async Task<string?> GetLatestVersionSignatureAsync(string input, HttpClient http, CancellationToken ct)
    {
        if (!RepoRegex.IsMatch(input))
        {
            return null;
        }

        (string owner, string repo) = ParseOwnerRepo(input);
        string cloneUrl = $"https://github.com/{owner}/{repo}.git";

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return ResolveRemoteHeadSha(cloneUrl);
            }, ct);
        }
        catch (Exception ex)
        {
            _log.Debug($"Git remote HEAD check failed for {owner}/{repo}: {ex.Message}");
            return null;
        }
    }

    private static (string Owner, string Repo) ParseOwnerRepo(string input)
    {
        Match m = RepoRegex.Match(input);
        string owner = m.Groups["owner"].Value;
        string repo = m.Groups["repo"].Value.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        return (owner, repo);
    }

    /// <summary>
    /// The equivalent of `git ls-remote &lt;url&gt; HEAD` — resolves the default branch's current commit
    /// sha over the network only, without touching (or needing) any local clone.
    /// </summary>
    private static string? ResolveRemoteHeadSha(string cloneUrl)
    {
        List<Reference> refs = Repository.ListRemoteReferences(cloneUrl).ToList();
        Reference? head = refs.FirstOrDefault(r => r.CanonicalName == "HEAD");
        if (head is null)
        {
            return null;
        }

        // HEAD is a symbolic ref (e.g. "refs/heads/main"); resolve it to the direct ref carrying the sha.
        Reference? target = refs.FirstOrDefault(r => r.CanonicalName == head.TargetIdentifier);
        return target?.TargetIdentifier;
    }

    /// <summary>
    /// Clones into <paramref name="cacheDir"/> if it isn't already a valid repo there, otherwise
    /// fetches and hard-resets the checked-out branch to the remote's current tip (this cache is a
    /// read-only mirror the launcher owns exclusively, so discarding any local drift is always safe
    /// and correct — equivalent to `git reset --hard origin/&lt;branch&gt;`).
    /// </summary>
    private Repository EnsureUpToDateClone(string cloneUrl, string cacheDir)
    {
        if (Directory.Exists(cacheDir) && !Repository.IsValid(cacheDir))
        {
            _log.Warn($"Addon repo cache at {cacheDir} is corrupt; re-cloning from scratch.");
            DirectoryHelper.DeleteRecursive(cacheDir);
        }

        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
            Repository.Clone(cloneUrl, cacheDir);
            return new Repository(cacheDir);
        }

        var repo = new Repository(cacheDir);
        try
        {
            string branchName = repo.Head.FriendlyName;
            Remote origin = repo.Network.Remotes["origin"];
            Commands.Fetch(repo, origin.Name, Array.Empty<string>(), null, "addon update check");

            Branch? remoteBranch = repo.Branches[$"origin/{branchName}"];
            if (remoteBranch is not null)
            {
                repo.Reset(ResetMode.Hard, remoteBranch.Tip);
            }

            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>Copies a git working tree into a fresh directory, leaving the ".git" folder behind.</summary>
    private static void CopyWorkingTree(string source, string dest)
    {
        string destFull = Path.GetFullPath(dest);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar))
        {
            destFull += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsUnderGitDir(source, dir))
            {
                continue;
            }

            Directory.CreateDirectory(ResolveDestPath(source, dir, destFull));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string? fileDir = Path.GetDirectoryName(file);
            if (fileDir is not null && IsUnderGitDir(source, fileDir))
            {
                continue;
            }

            File.Copy(file, ResolveDestPath(source, file, destFull), overwrite: true);
        }
    }

    /// <summary>
    /// Resolves an entry under source to its counterpart under dest via its relative path (not a
    /// plain entryPath.Replace(source, dest), which could behave surprisingly if source ever happened
    /// to appear as a substring elsewhere in a nested path - the same fix already applied to
    /// AddonInstaller.CopyDirectory), then verifies the result actually lands inside dest.
    /// </summary>
    private static string ResolveDestPath(string source, string entryPath, string destFullWithSep)
    {
        string relative = Path.GetRelativePath(source, entryPath);
        string resolved = Path.GetFullPath(Path.Combine(destFullWithSep, relative));
        if (!resolved.StartsWith(destFullWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to copy '{entryPath}' — its resolved destination falls outside the destination folder.");
        }

        return resolved;
    }

    private static bool IsUnderGitDir(string root, string dir)
    {
        string relative = Path.GetRelativePath(root, dir);
        return relative == ".git" || relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
