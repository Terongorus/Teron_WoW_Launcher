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
It doesn't need to live inside any WoW folder itself, and it can manage more than one installation
at once as separate **Profiles** — switch between them from the Home tab, and each one keeps its
own directory, realmlist, client identifier, login delay/WDB setting, account, tweaks, patches,
DLLs, and addons.

## Getting started

1. On the **Home** tab, pick (or add) a Profile and point it at a World of Warcraft folder — the
   one with `WoW.exe` in it, or an empty folder if you want the launcher to install one for you.
   The launcher itself can live anywhere on disk; it doesn't need to be inside that folder.
2. Set that profile's realmlist, and pick a **Client identifier** (Vanilla, TurtleWoW, OctoWoW, or
   your own) so the launcher knows which client baseline to expect — required before you can
   install or play. Optionally set an account and password if you want to log in automatically.
3. Hit the big button in the bottom-right corner. If there's no client yet, it reads **Install** —
   the launcher downloads and sets everything up for you. Once a client's in place, the same
   button reads **Play**; if a newer client archive shows up later, it reads **Update** instead.
   Whichever it says, that's the one thing you need to click.

That's the whole day-to-day workflow. Everything below is what each tab does, for when you want
to go further than the defaults.

## Using the launcher

### Home

Three columns: a live-rendered copy of this README and the changelog on the left, your **Profiles**
in the middle, and that profile's settings on the right. A Profile bundles everything specific to
one WoW installation — give it a name (falls back to the directory path, or "Default" for the
launcher's own folder, if left blank), point it at a directory, set its realmlist, pick a **Client
identifier**, and its login delay/WDB cleanup/account/password all live in the same panel. The
Realmlist field carries a live status dot next to it — green when the auth server accepts a
connection, red when a real host refuses or times out, gray when the address doesn't even resolve
— and remembers every realm you've actually connected to, offered back as a history dropdown.
Auto-login types your credentials directly (bypassing your keyboard layout, so a non-English
layout can't garble your password) and waits for the client to actually finish loading before it
types, instead of guessing a fixed delay. Click a different Profile in the list to switch to it —
everything else in the launcher (Tweaks, DLLs, MPQ Patches, Addons) follows the active profile.
Play/Install stays disabled, with a note explaining why, until a profile has a Client identifier
assigned.

### Tweaks

Client-side quality-of-life patches, applied by rebuilding `WoW.exe` from an untouched backup
every time you change something — never patching an already-patched file. Split into **Mandatory**
(Signature Removal, Large Address Aware, and a Config.WTF toggle that forces the video settings
some HD/visual MPQ patches need) — enable all three if you plan to run custom MPQ patches — and
**Optional**: widescreen field of view (shown in degrees, not raw radians), render/grass/nameplate
distance, sound channels, sound-while-alt-tabbed, auto-loot, and a camera rotation glitch fix, most
with a slider *and* a type-in box for the exact value you want. On TurtleWoW/OctoWoW profiles,
several of these (LAA, sound-in-background, auto-loot, the camera fix, FoV, nameplate distance)
ship already baked into the client — they still show up as real, togglable entries (checked by
default, so an existing install isn't silently reverted) rather than read-only notes, and
unchecking one genuinely reverts that byte range to Blizzard's original value. Every change applies
the moment you make it — there's no "Save" button to remember.

### DLLs

One list: DLLs the launcher injects on launch (in load order, top to bottom — drag them into place
with the move buttons) grouped above everything else sitting in your game folder that isn't
tracked yet, searchable by name. Each row shows its version, author, and description where the
file itself provides that information — and where it doesn't (most small hand-built mod DLLs
don't), an info button lets you fill it in by hand. Per-row buttons track/untrack a DLL directly,
or ignore a detected one permanently if it's not meant to be injected (a framework DLL, say) —
ignored files stay out of the detected list until you un-ignore them from Settings.

### MPQ Patches

Custom `patch-A.mpq` … `patch-Z.mpq` archives in your `Data\` folder, toggled on and off with
checkboxes (an unchecked patch is simply renamed with a leading underscore, so it's inert but not
deleted). Blizzard's own base-game archives are never shown or touched. Each patch shows its
title/author/description/version/website when the archive itself carries a `Patch.toc` or
readme-style file — an info button lets you fill in whatever wasn't found automatically — and the
list is searchable by name.

### Addons

Install addons the way that's actually convenient: paste a GitHub or GitLab repo link, a
Legacy-WoW or Warperia addon page link, a direct archive URL, or point at a `.zip`/`.rar`/`.7z`
you already downloaded — or switch to the **Browse** sub-tab to search Legacy-WoW's and
Warperia's full addon catalogs right inside the launcher, read each addon's own description, and
install it in one click without ever leaving the app. Addon names render with their real in-game color codes, and versions
come straight from each addon's own `.toc` file rather than a
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

Everything that isn't tied to one specific profile. **Client Identifiers** is the shared table
every profile's Client identifier picks from — name, download URL, and category (Vanilla or
Vanilla+), seeded with Kronos, TurtleWoW, and OctoWoW defaults, but freely editable or extendable
with your own. **Repair Game Files**/**Delete Game Files** apply to whichever profile is currently
selected on Home — Repair re-downloads and reinstalls the whole client from its assigned
identifier's URL, overwriting anything that differs locally, useful if something's gotten
corrupted and you don't want to track down which file. Minimizing the launcher the moment the game
starts (it always comes back to the foreground on its own once the game closes, whether or not
this is on) lives here too, along with ignored DLLs (see the DLLs tab above), managed individually
or all at once.

### Log

A running, live log of everything the launcher does — patching, injection, downloads, addon
installs — for when something doesn't go as expected and you want to see exactly what happened.
Entries are color-coded by severity, and Clear, Copy, and Open Folder actions are right there on
the tab.

## Staying up to date

The launcher checks GitHub on startup for a newer stable release. If one's available, confirming
the prompt downloads the installer, verifies it against the release's own published checksum, and
launches it, closing the launcher so the update can proceed. The first time you run a version
after updating, a "what's new" summary of that version's changes shows once automatically.

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

Version 2.0.0-beta.3. See [CHANGELOG.md](CHANGELOG.md) for the full history. Versions follow
`major.minor.hotfix`.

## License

[GNU General Public License v3.0](LICENSE.txt).
