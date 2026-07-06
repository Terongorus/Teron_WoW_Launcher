# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow major.minor.hotfix (e.g. 1.2.3).

## [0.1.0] - 2026-07-06

### Added
- WPF launcher (.NET 10, built 32-bit to match the WoW 1.12.1 client) designed to live in the game folder.
- One-click Play running the full fixed-order flow: executable patches → DLL injection → launch → auto-login.
- In-process DLL injection (launch suspended, `CreateRemoteThread` + `LoadLibraryW` per `dlls.txt` entry, then resume) — no external injector.
- `dlls.txt` management (add/remove/reorder) with a change-detection cache.
- Auto-login via synthetic input, shift-aware so passwords containing symbols type correctly; password stored with Windows DPAPI.
- Executable patch pipeline using a pristine `WoW.exe` backup with rebuild-from-clean in fixed order, applied when the selection changes (not every launch):
  - Signature Removal (lets custom MPQ patches and edited DBC files load).
  - VanillaTweaks: Large Address Aware, widescreen FoV, farclip, grass distance, nameplate distance, sound channels, sound-in-background, auto-loot, camera-skip fix, and an opt-in max camera distance — the value tweaks are adjustable from the UI.
- Custom MPQ patch tracking (`patch-A.mpq` … `patch-Z.mpq`, underscore prefix to disable), ignoring base game patches.
- Realmlist support for any private vanilla server, written to `realmlist.wtf` on save.
- Game client download/install: configurable client ZIP URL, resumable download with progress, extraction into a chosen folder, and a client version check.
- Addon manager: install from a GitHub repository, a direct archive URL, or a local archive (`.zip`, `.rar`, `.7z`); installs into `Interface\AddOns` by `.toc`, tracked in `addons.json`, with add/remove/update.
- Logging to `%LocalAppData%\TeronWoWLauncher\Logs`, crash logging to `error.log`, and a single-instance guard.
