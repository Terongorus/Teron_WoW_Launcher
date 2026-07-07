# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow major.minor.hotfix (e.g. 1.2.3).

## [1.0.0] - 2026-07-07

First official release.

### Added

- Installer packaging: an Inno Setup script wired into `dotnet publish -p:PublishProfile=win-x86`, producing a ready-to-distribute `TeronWoWLauncherSetup-x86.exe` in `bin\InstallerPackage` with no extra manual steps. x86-only, matching the app itself. The setup wizard's destination page is worded for this app's specific install model — you point it at your existing WoW folder, not a `Program Files` location.
- A small version label in the header's top-left corner (mirroring the window controls' position on the right), so the main title stays just the app name.

## [0.10.0] - 2026-07-07

### Added

- Custom window chrome: the native Windows title bar/icon/Minimize/Maximize/Close are replaced with the launcher's own — merged into the same row as the app title and navigation, so there's no empty strip above it. The three buttons have no background fill (matching the nav labels' style), just a color change on hover, with Close getting its own soft-red hover.
- The window's size, position, and normal/maximized/minimized state are now remembered across restarts. A saved position is only honored if it still falls on a currently-connected monitor, so a since-removed second monitor can't strand the window off-screen.

## [0.9.0] - 2026-07-07

### Added

- Icon-only toolbar and per-row buttons (Segoe Fluent Icons) with tooltips, replacing text labels on the common, self-explanatory actions (add/remove/refresh/move/select-all/browse/etc.). A few buttons whose action isn't obvious from an icon (Repair Game Files, Un-ignore Selected) kept their text, as did the main Play/Install/Update button and every dialog's Cancel/Confirm buttons.
- Every button now has its own custom hover/press/keyboard-focus look (a subtle lightening overlay plus a gold border) instead of the OS's default highlight, which didn't match the app's dark theme.
- The Home tab's Readme/Changelog previews now fill the available space in their column instead of using a fixed height, with no internal scrollbar — clicking either one opens it full-size in its own window for easier reading.
- Clicking the realmlist label on the Home tab briefly flashes the realmlist field on the Settings tab, so it's obvious which control to edit.

### Changed

- The MPQ Patches list now matches the DLL/Addon lists' visual style (previously it stood out with no border/background), and its Add/Refresh/Select All/Deselect All buttons moved above the list.

## [0.8.0] - 2026-07-07

### Added

- Addon names render with their in-game color codes (`|cAARRGGBB...|r`), matching how they'd appear in the client's own addon list.
- Addon versions are read from each addon's own `.toc` "## Version:" field uniformly across every source (GitHub, archive, manually-adopted) — an addon with no version in its `.toc` simply shows none, instead of a GitHub release tag being shown as if it were the addon's own version.
- Addon list rows have their own Delete and Update buttons; Update only appears when a tracked GitHub/archive addon actually has a newer version available (checked via a lightweight remote-signature comparison, no download needed).
- "Refresh" on the Addons tab now does everything at once: reloads from disk, re-reads each addon's `.toc`, silently adopts untracked local `Interface\AddOns` folders, and checks every remote-tracked addon for updates — a dialog only appears if a local folder's name collides with an already-tracked addon.
- Adding an addon moved from inline URL/file controls into its own "Add Addon" dialog.

## [0.7.0] - 2026-07-07

### Added

- Home tab split into two panels: a "How to use" panel rendering this project's README.md and CHANGELOG.md — fetched live from GitHub and rendered as real Markdown (via Markdig.Wpf) — alongside the login fields, which moved to their own panel on the right.

## [0.6.0] - 2026-07-07

### Added

- Ignoring DLLs: "Ignore Selected" on the DLLs tab's detected list hides files that aren't actually meant for injection (e.g. framework DLLs); manageable from Settings.
- MPQ Patches: "Select All"/"Deselect All" buttons.
- Modern flat scrollbar theme and a general font-size bump across the UI.

### Changed

- Auto-login/account fields and launcher-level settings now save automatically on change (debounced) instead of requiring a "Save" button; a malformed client download URL is rejected rather than persisted.

## [0.5.1] - 2026-07-07

### Fixed

- Header navigation labels weren't centered across the full header width, and their font was too small.
- The DLLs tab's two lists (tracked and detected) had uneven heights.
- The realmlist label on the Home tab was docked to the wrong side.
- The Home tab didn't load until a nav button was clicked, instead of showing on startup.

## [0.5.0] - 2026-07-07

### Added

- Play button now reflects client state — **Install** (no client found; prompts for a target folder, defaulting to the launcher's own folder), **Update** (a different remote archive is available; shows a confirmation dialog before re-downloading), or **Play** (client up to date) — replacing the separate Install/Update tab.
- Settings tab consolidating launcher-level configuration: installation directory (single source of truth, replacing the separate Home-tab and Install-tab folder fields), realmlist, client download URL, and Repair Game Files (re-downloads and reinstalls the full client).
- Header navigation: tab headers are hidden in favor of clickable labels in the title bar.
- DLL auto-detection: a "Detected in game folder" list on the DLLs tab, for adding DLLs already sitting in the game folder without a file picker.
- Addon manual-detection: finds `Interface\AddOns` folders installed outside the launcher and offers to adopt them into tracking via a review dialog.
- Login & Game moved to the first (Home) tab; "Patches" renamed to "Tweaks".

## [0.4.1] - 2026-07-07

### Fixed

- Toggling a tweak while the game was running, or two patch rebuilds overlapping, could throw an `IOException` on `WoW.exe`/a leftover temp file. Executable-patch rebuilds are now serialized, skip cleanly (with a status message) while the game process is detected running, and use a collision-proof temp filename with automatic cleanup of any stale leftovers.

## [0.4.0] - 2026-07-07

### Added

- Addon manager: install from a GitHub repository, a direct archive URL, or a local archive (`.zip`, `.rar`, `.7z`); installs into `Interface\AddOns` by `.toc`, tracked in `addons.json`, with add/remove/update.

## [0.3.0] - 2026-07-07

### Added

- Game client download/install: configurable client ZIP URL, resumable download with progress, extraction into a chosen folder, and a client version check.

## [0.2.0] - 2026-07-07

### Added

- VanillaTweaks patch table: Large Address Aware, widescreen FoV, farclip, grass distance, nameplate distance, sound channels, sound-in-background, auto-loot, camera-skip fix, and an opt-in max camera distance — the value tweaks are adjustable in the UI.
- Executable patches now apply only when the selection actually changes, instead of rebuilding `WoW.exe` on every launch.
- Custom MPQ patch tracking (`patch-A.mpq` … `patch-Z.mpq`, underscore prefix to disable), ignoring base game patches.
- Realmlist support for any private vanilla server, written to `realmlist.wtf`.

## [0.1.0] - 2026-07-06

### Added

- WPF launcher (.NET 10, built 32-bit to match the WoW 1.12.1 client) designed to live in the game folder.
- One-click Play running the full fixed-order flow: executable patches → DLL injection → launch → auto-login.
- In-process DLL injection (launch suspended, `CreateRemoteThread` + `LoadLibraryW` per `dlls.txt` entry, then resume) — no external injector.
- `dlls.txt` management (add/remove/reorder) with a change-detection cache.
- Auto-login via synthetic input, shift-aware so passwords containing symbols type correctly; password stored with Windows DPAPI.
- Executable patch pipeline using a pristine `WoW.exe` backup with rebuild-from-clean in fixed order:
  - Signature Removal (lets custom MPQ patches and edited DBC files load).
- Logging to `%LocalAppData%\TeronWoWLauncher\Logs`, crash logging to `error.log`, and a single-instance guard.
