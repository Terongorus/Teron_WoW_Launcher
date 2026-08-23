# Phase 0 - Compliance Gap Remediation

A standing-conventions audit of `Teron_WoW_Launcher` (currently `2.0.0-beta.2`, pre-1.0) found
several gaps against Kaloyan's cross-portfolio conventions. This entry records what was closed
and, for the one item that wasn't, why.

## LangVersion

`TeronWoWLauncher.csproj` had `Nullable`/`ImplicitUsings` enabled but no explicit
`<LangVersion>`. Added `<LangVersion>latest</LangVersion>` to the same top `PropertyGroup`, next
to those two.

## Directory.Build.props (BUILDER)

No `Directory.Build.props` existed, so build/obj output was scattered in the default per-project
`bin\`/`obj\` next to the `.csproj`. Added the standard BUILDER block at the repo root (flat
`$(MSBuildProjectName)` form - this repo has a single project directly at the root, no nested
project folders):

- `Build\Debug\TeronWoWLauncher\` / `Build\Release\TeronWoWLauncher\` for `dotnet build`.
- `Build\obj\TeronWoWLauncher\` for intermediates.
- `Build\Publish\TeronWoWLauncher\$(RuntimeIdentifier)\` as BUILDER's own publish-output default.

Added `/Build/` to `.gitignore`, folded in alongside an already-pending, unrelated
`.gitignore` change (a `.vscode/` entry) that was already in the working tree before this pass
started - both are now one edit, not two competing ones.

### Reconciling with DISSEMINATE (the installer pipeline)

This app already has an installer pipeline: `Properties/PublishProfiles/win-x86.pubxml` (this
app is win-x86-only by design - `WoW.exe` is a 32-bit binary and the launcher's DLL injection
requires matching bitness, per the `PlatformTarget` comment in the `.csproj` - there is no
`win-x64.pubxml` to reconcile). BUILDER's own `PublishDir` formula uses `$(RuntimeIdentifier)`,
which isn't populated yet at `Directory.Build.props`'s early import time and silently collapses
to an empty segment (a known, previously-observed failure mode on other apps in this portfolio).
Set `win-x86.pubxml`'s `<PublishDir>` explicitly instead, so it wins (a `.pubxml`'s
`PropertyGroup` imports later than `Directory.Build.props`):

```
<PublishDir>Build\Publish\TeronWoWLauncher\win-x86\</PublishDir>
```

This replaced the pre-existing `bin\Publish\TeronWoWLauncher_Win_x86\` value (itself part of an
already-modified, uncommitted `win-x86.pubxml` from prior work - that work's other changes,
`Platform` → `Any CPU` and `PublishTrimmed` → `true`, were left untouched; only `PublishDir` was
changed here).

Also updated `Installer/TeronWoWLauncher.iss`'s `MyPublishDir` default from
`..\bin\Publish\TeronWoWLauncher_Win_x86` to `..\Build\Publish\TeronWoWLauncher\win-x86`, so the
Inno Setup script's manual-run fallback still points at where publish output actually lands now.
Nothing else in the installer script changed (`OutputDir` for the packaged installer `.exe`
itself, `bin\InstallerPackage\`, is a separate concern from where the published *app* files land,
and was out of scope here).

**WPF gotcha to remember for next time**: switching `-c Debug` / `-c Release` back-to-back
without deleting `Build\` first can throw bogus duplicate-member errors from stale
`Build\obj\` - delete `Build\` and rebuild clean if that's ever hit on this project.

## CODEX (auto-incrementing build number) - deferred, not applied

CODEX's standard mechanism replaces a hand-typed `<Version>` with
`<MajorMinorPatchVersion>X.Y.Z</MajorMinorPatchVersion>` plus a `BuildNumber.txt`-backed target
that computes `<Version>$(MajorMinorPatchVersion).$(BuildNumber)</Version>` - a plain 4-part
numeric version. This app's current `<Version>` is `2.0.0-beta.2`: a pre-release suffix the
mechanism has no room for.

**Decision: deferred, not applied.** Two things pointed the same way:

1. This app is genuinely still pre-1.0/beta - the mechanism is designed for a stable release
   line, and forcing a purely numeric `2.0.0.<N>` now would read as a finished "2.0.0" to anyone
   who doesn't already know it's a beta, which contradicts what the version string exists to
   communicate.
2. More concretely, the `-beta.N` suffix is not just cosmetic here - it's already load-bearing
   application logic. `Services/Launch/LauncherUpdateService.cs`'s `IsNewer(...)` has an explicit
   comment and a `StripSuffix` helper specifically written to handle `AppInfo.Version` carrying a
   `-beta.N`-style suffix (GitHub's `/releases/latest` never returns a prerelease, so the
   *current* version is the one that needs the strip). That's a deliberate design decision this
   app's own code was already built around, not an oversight to silently paper over by switching
   to a numeric-only scheme.

Left `<Version>2.0.0-beta.2</Version>` exactly as it was. No `BuildNumber.txt` was created, and
`Installer/TeronWoWLauncher.iss`'s `MyAppVersion` fallback / `CHANGELOG.md` / `README.md` version
mentions were left untouched, since no version bump actually happened. Revisit this once the app
ships a stable, non-beta `2.0.0` - at that point the standard CODEX mechanism applies cleanly
with no suffix to reconcile.

## Verification

`dotnet build TeronWoWLauncher.csproj -c Debug` and `-c Release` both succeeded, with output
confirmed under `Build\Debug\TeronWoWLauncher\` and `Build\Release\TeronWoWLauncher\`
respectively. `dotnet publish` and the installer build were deliberately not run as part of this
pass.
