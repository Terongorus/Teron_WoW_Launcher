# Teron WoW Launcher

Playing vanilla WoW (1.12.1) with mods usually means juggling a handful of separate tools: a
patcher exe to inject DLLs, a shady third-party auto-login program, hand-editing `dlls.txt` and
renaming MPQ files to enable/disable patches, and hunting down addons one at a time from
scattered sites and GitHub repos. **Teron WoW Launcher folds all of that into one app and one
button**, with every piece of it written from scratch — nothing shady bundled in, nothing that
runs behind your back.

Point it at your WoW folder (or let it install the client for you), hit **Play**, and it patches
the executable, injects your DLLs, launches the game, and logs you in — in that order, every
time, automatically skipping anything that hasn't changed since last time.

## What you can do with it

- **One button that adapts to what you need.** The main button reads **Install** when there's no
  client yet, **Update** when a newer client archive is available, or **Play** once everything's
  ready — no separate install wizard to find first.
- **Tweak the client without touching a hex editor.** Toggle Large Address Aware, widescreen FoV,
  render distance, grass distance, nameplate range, sound channels, sound-while-alt-tabbed,
  auto-loot, and a camera-glitch fix — most with a slider for the exact value you want. Every
  change applies immediately; nothing waits for a "Save" button.
- **Load whatever DLLs you use.** Add them from a picker or from a "detected in your game folder"
  list, reorder load order, and hide DLLs you don't want suggested again.
- **Manage custom MPQ patches** (`patch-A.mpq` … `patch-Z.mpq`) with simple enable/disable
  checkboxes, Select All / Deselect All, and no risk of touching Blizzard's own base-game
  archives.
- **Add addons the way that's actually convenient**: paste a GitHub repo link, a direct archive
  URL, or point at a `.zip`/`.rar`/`.7z` you already downloaded. Addon names show their real
  in-game colors, versions come straight from each addon's own `.toc`, and one Refresh both
  checks for updates and picks up anything you installed by hand.
- **Log in automatically**, with your password encrypted on your own PC (Windows DPAPI) — never
  stored in plain text.
- **Connect to any private server**, not a fixed list — set your realmlist once from Settings.
- **See what changed, without digging through folders.** A live log tab, and a Home tab that
  shows this project's own README and changelog right inside the app.

## Requirements

- Windows, with the [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  (x86) — or build from source with the .NET 10 SDK.
- A vanilla 1.12.1 (build 5875) client. The launcher can download one for you if you don't have
  one yet.

## Getting started

1. Place the launcher in (or point it at, from the Settings tab) your World of Warcraft folder.
2. Set your realmlist, and optionally your account and password for auto-login.
3. Hit the big button. If there's no client yet, it'll say **Install** — pick a folder and it
   downloads and sets everything up. Otherwise, it says **Play**.

## Building from source

```sh
dotnet build TeronWoWLauncher.csproj -c Debug
```

A 32-bit (x86) WPF app targeting `net10.0-windows`, matching the vanilla client's own
architecture (required for in-process DLL injection to work). Third-party dependencies:
[SharpCompress](https://github.com/adamhathcock/sharpcompress) (extracts `.rar`/`.7z` addon
archives, which the base class library can't) and
[Markdig.Wpf](https://github.com/Kryptos-FR/markdig.wpf) (renders this README and the changelog
as Markdown on the Home tab).

## Prior art

The executable-patching and DLL-injection techniques were learned by studying open tools
(VanillaFixes, vanilla-tweaks, AutoLogin, and a signature-removal patcher) and reimplemented from
scratch here. No third-party tool binaries are bundled or executed — the launcher is entirely its
own code.

## Project status

Version 1.0.0. See [CHANGELOG.md](CHANGELOG.md) for the history. Versions follow
`major.minor.hotfix`.

## License

[GNU General Public License v3.0](LICENSE.txt).
