# Teron WoW Launcher

A launcher for **vanilla World of Warcraft (1.12.1)** that installs the client, tweaks and patches it,
injects DLLs, manages addons and MPQ patches, and logs you in — designed to live in the game folder
and "just work" with one button.

Windows only: the 1.12.1 client is a 32-bit Windows binary, and the launcher is built 32-bit to match
it (so its in-process DLL injection is valid in the game).

## Features

- **One-click Play** — patches the executable, injects DLLs, launches, and auto-logs in, in a fixed,
  safe order.
- **Client install / update** — downloads the vanilla client from a configurable URL (resumable) and
  extracts it into a folder you choose; that folder becomes the game home.
- **Executable tweaks** — a pristine-backup + rebuild-from-clean pipeline applies patches only when
  your selection changes:
  - Signature Removal (lets custom MPQ patches and edited DBC files load)
  - VanillaTweaks: Large Address Aware, widescreen FoV, farclip, grass distance, nameplate distance,
    sound channels, sound-in-background, auto-loot, camera-skip fix, and an opt-in max camera
    distance — the value tweaks are adjustable in the UI.
- **DLL injection** — launches the game suspended and injects each DLL from `dlls.txt`, then resumes.
  No external injector is bundled or run.
- **Auto-login** — types your account and password into the login screen; the password is stored
  encrypted with the Windows Data Protection API (DPAPI).
- **MPQ patch tracking** — enable/disable custom `patch-A.mpq … patch-Z.mpq` in `Data\` (an underscore
  prefix disables a patch); base Blizzard patches are never touched.
- **Addon manager** — install addons from a GitHub repository, a direct archive URL, or a local
  `.zip` / `.rar` / `.7z`, into `Interface\AddOns` by `.toc`; tracked with add / remove / update.
- **Realmlist** — connect to any private vanilla server.
- **Logging** — per-day logs and crash logging under `%LocalAppData%\TeronWoWLauncher`.

## Requirements

- Windows, with the [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  (x86) — or build from source with the .NET 10 SDK.
- A vanilla 1.12.1 (build 5875) client. The launcher can download one for you.

## Getting started

1. Place the launcher in (or point it at) your World of Warcraft folder.
2. Set the game folder and realmlist, and optionally your account and password.
3. Press **Play**. If the client isn't installed yet, install it first from the Install / Update tab.

## Building from source

```sh
dotnet build TeronWoWLauncher.csproj -c Debug
```

A 32-bit (x86) WPF app targeting `net10.0-windows`. The only third-party dependency is
[SharpCompress](https://github.com/adamhathcock/sharpcompress), used to extract `.rar`/`.7z` addon
archives (the base class library only handles `.zip`).

## Prior art

The executable-patching and DLL-injection techniques were learned by studying open tools
(VanillaFixes, vanilla-tweaks, AutoLogin, and a signature-removal patcher) and reimplemented from
scratch here. No third-party tool binaries are bundled or executed — the launcher is entirely its own
code.

## Project status

Version 0.1.0. See [CHANGELOG.md](CHANGELOG.md) for the history. Versions follow `major.minor.hotfix`.

## License

[GNU General Public License v3.0](LICENSE.txt).
