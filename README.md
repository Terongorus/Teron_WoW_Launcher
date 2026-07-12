# Teron WoW Launcher

Playing vanilla WoW (1.12.1) with mods usually means juggling a handful of separate tools: a
patcher exe to inject DLLs, a shady third-party auto-login program, hand-editing `dlls.txt` and
renaming MPQ files to enable/disable patches, and hunting down addons one at a time from
scattered sites and GitHub repos. **Teron WoW Launcher folds all of that into one app and one
button**, with every piece of it written from scratch — nothing shady bundled in, nothing that
runs behind your back.

Point it at your WoW folder (or let it install the client for you), hit **Play**, and it patches
the executable, injects your DLLs, launches the game, and logs you in — in that order, every
time, automatically skipping anything that hasn't changed since last time. The launcher steps back
out of your way once the game is up, and comes back to the front on its own the moment you close it.

## Getting started

1. Place the launcher in (or point it at, from the **Settings** tab) your World of Warcraft
   folder — the one with `WoW.exe` in it.
2. On the **Home** tab, set your realmlist (top-right link, or via Settings), and optionally your
   account and password if you want to log in automatically.
3. Hit the big button in the bottom-right corner. If there's no client yet, it reads **Install** —
   pick a destination folder and the launcher downloads and sets everything up for you. Once a
   client's in place, the same button reads **Play**; if a newer client archive shows up later,
   it reads **Update** instead. Whichever it says, that's the one thing you need to click.

That's the whole day-to-day workflow. Everything below is what each tab does, for when you want
to go further than the defaults.

## Using the launcher

### Home

Your account/password fields and auto-login toggle live here, along with a live-rendered copy of
this README and the changelog — so you can check what changed without leaving the app or digging
through folders. Auto-login types your credentials directly (bypassing your keyboard layout, so a
non-English layout can't garble your password) and waits for the client to actually finish
loading before it types, instead of guessing a fixed delay. The Realmlist label carries a live
status dot — green when the auth server accepts a connection, red when a real host refuses or
times out, gray when the address doesn't even resolve — and the field itself remembers every
realm you've actually connected to, offered back as a history dropdown.

### Tweaks

Client-side quality-of-life patches, applied by rebuilding `WoW.exe` from an untouched backup
every time you change something — never patching an already-patched file. Large Address Aware,
widescreen field of view (shown in degrees, not raw radians), render/grass/nameplate distance,
sound channels, sound-while-alt-tabbed, auto-loot, and a camera rotation glitch fix are all here,
most with a slider *and* a type-in box for the exact value you want. Every change applies the
moment you make it — there's no "Save" button to remember.

### DLLs

Two lists, side by side: the DLLs the launcher actually injects on launch (in load order, top to
bottom — drag them into place with the move buttons), and DLLs sitting in your game folder that
aren't tracked yet. Each entry shows its version, author, and description where the file itself
provides that information, so you're not guessing what `d3d9.dll` actually is from the name alone.
Add a detected DLL to start tracking it, or ignore one permanently if it's not meant to be
injected (a framework DLL, say) — ignored files stay out of the detected list until you
un-ignore them from Settings.

### MPQ Patches

Custom `patch-A.mpq` … `patch-Z.mpq` archives in your `Data\` folder, toggled on and off with
checkboxes (an unchecked patch is simply renamed with a leading underscore, so it's inert but not
deleted). Blizzard's own base-game archives are never shown or touched.

### Addons

Install addons the way that's actually convenient: paste a GitHub repo link, a direct archive
URL, or point at a `.zip`/`.rar`/`.7z` you already downloaded. Addon names render with their real
in-game color codes, and versions come straight from each addon's own `.toc` file rather than a
GitHub release tag. Hitting **Refresh** does everything at once — checks every tracked addon for
updates, and picks up any folder you dropped into `Interface\AddOns` by hand, prompting you before
adopting anything that might conflict with what's already tracked. This same check also runs once
automatically every time the launcher starts, alongside the DLL and MPQ patch scans, so you don't
have to remember to click it. The search box filters the list
live by name as your addon count grows, and each row's info button shows that addon's own
README — from its GitHub repo, a local README file, or its `.toc` as a last resort — without
leaving the app. If several addons have updates queued up, **Update All** installs every one of
them in a single click instead of working through the list one row at a time; and if you'd rather
an addon stayed on its current version even when a newer one is published, its Details dialog has
an "Ignore updates" checkbox that permanently stops it from being flagged.

### Settings

The one place for launcher-level configuration: your install directory, realmlist, client
download URL (only needed if you're not using the default), and the auto-login delay fallback.
**Repair Game Files** re-downloads and reinstalls the whole client from the source URL, overwriting
anything that differs locally — useful if something's gotten corrupted and you don't want to track
down which file. Two optional toggles live here too: cleaning up the WDB client cache before every
launch (safe — the client rebuilds it from the server — and useful on private servers where stale
cached data shows wrong item/quest names, tooltips, or icons), and minimizing the launcher the
moment the game starts (it always comes back to the foreground on its own once the game closes,
whether or not this is on). Ignored DLLs (see the DLLs tab above) are managed here too,
individually or all at once.

### Log

A running, live log of everything the launcher does — patching, injection, downloads, addon
installs — for when something doesn't go as expected and you want to see exactly what happened.
Entries are color-coded by severity, and Clear, Copy, and Open Folder actions are right there on
the tab.

## Requirements

- Windows, with the [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  (x86) — or build from source with the .NET 10 SDK.
- A vanilla 1.12.1 (build 5875) client. The launcher can download one for you if you don't have
  one yet.

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

Version 1.11.0. See [CHANGELOG.md](CHANGELOG.md) for the full history. Versions follow
`major.minor.hotfix`.

## License

[GNU General Public License v3.0](LICENSE.txt).
