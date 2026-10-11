<p align="center">
  <img src="docs/icon.png" width="96" alt="APMultiTracker icon">
</p>

<h1 align="center">APMultiTracker</h1>

<p align="center">
  Every game in your <b>Archipelago</b> multiworld, tracked in one window.<br>
  See what's in logic across all your games at once, or focus one and flip between them.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF-22C7E6">
</p>

<p align="center">
  <img src="docs/screenshots/overview.png" alt="All games: each game's in-logic checks side by side" width="900">
</p>

---

## Why

[Universal Tracker](https://github.com/FarisTheAncient/Archipelago/releases?q=Tracker) (UT) is great, but it tracks
one slot per window. Playing four games means four UT windows. APMultiTracker runs UT's logic for **all** your slots
in one place, with a faster, cleaner interface.

## Features

| | |
|---|---|
| 🧭 **All games at once** | Every slot's in-logic checks side by side, with live counts, optionally grouped by region. |
| 🎯 **Focus one game** | Big check list with search, filters (In logic, + Glitched, All remaining, Hinted, Hidden), collapsible region groups and received items. |
| ⌨️ **Fast switching** | Click a game tile, or Ctrl+1…9, Ctrl+0 for all games, Ctrl+Tab to step through. |
| 🔎 **Explain & logical path** | Why is something in or out of logic? UT's `/explain` and `/get_logical_path`, one double-click away. |
| 💡 **Hints** | Hinted checks are marked with the item and who it's for. |
| 🙈 **Hide checks** | Hide checks you've dealt with (saved per seed, like UT's `/ignore`). |
| 🖼️ **Box art** | Pulled from Twitch by game name with no login. Click the art to pick a different one (optional Twitch connect). |
| 💬 **Feed** | The room's messages as they happen, like the Text Client: items sent, hints, chat and goals, colour-coded and filterable. Chat or `!hint` from any of your slots. Show it on its own page or docked beside the Tracker. |
| 🌍 **Several multiworlds at once** | Save rooms with their server and slots (**Find slots in this room** lists everyone). Keep several connected and switch between them instantly. |
| ⚙️ **Zero fuss engine** | One click installs the engine into Archipelago; the app starts and stops it for you. |
| ⬆️ **Auto-updates** | Checks GitHub for new versions, downloads in the background and installs in one click. |

<p align="center">
  <img src="docs/screenshots/focused.png" alt="One game focused" width="900">
</p>

<p align="center">
  <img src="docs/screenshots/overview-grouped.png" alt="All games, grouped by region" width="900">
</p>

## Explain and logical path

Double-click a check (or right-click → **Explain** / **Show logical path**), or turn on **Explain** and type any
location, region or entrance (close spellings are fine). It runs Universal Tracker's own `/explain` and
`/get_logical_path`, so the results are identical, just easier to read. Drag the panel's top edge to resize it.

<p align="center">
  <img src="docs/screenshots/explain.png" alt="Logical path to a check" width="900">
</p>

## Feed

Everything happening in the room, like Archipelago's Text Client: items sent (coloured by importance),
hints, chat, goals and releases. Filter by Items, Hints, Chat or Activity, or turn on **Only my slots**.
Type in the box at the bottom to chat or run server commands such as `!hint Hookshot` as any of your slots.
Messages involving any of your games are highlighted in green. Each message shows once, even though every tracked
slot hears it, and the tracker's own connections are hidden.

<p align="center">
  <img src="docs/screenshots/feed.png" alt="The room's message feed" width="900">
</p>

### Feed and Tracker together

Click **Feed** in the Tracker's header to dock the feed next to your checks: on the **right** (default) or along the
**bottom**. Switch sides with the two buttons in the feed's title bar and drag the divider to resize it; each side
remembers its size. When a game is focused, the feed chats as that game's slot.

<p align="center">
  <img src="docs/screenshots/tracker-feed-right.png" alt="Feed docked on the right of the Tracker" width="440">
  <img src="docs/screenshots/tracker-feed-bottom.png" alt="Feed docked along the bottom of the Tracker" width="440">
</p>

## Several multiworlds at once

Every multiworld you **Activate** stays connected in the background. The sidebar, Tracker and Feed show the active
one; use **Switch multiworld** on the Tracker (or **Switch to** on Multiworlds) to jump between them instantly, with
no reloading. **Disconnect** stops tracking one. Connected multiworlds reconnect when you start the app.

## Updates

APMultiTracker checks GitHub for new releases a few seconds after it starts and every 6 hours. Updates download in
the background, are verified against GitHub's SHA-256 checksum, and install with **Update now** (the app restarts by
itself). Turn on **Update automatically on next launch** in Settings → Updates to have them installed for you.
Your multiworlds and settings are kept. The tracker engine inside Archipelago updates itself to match.

## Requirements

- Windows 10/11 x64
- [Archipelago](https://github.com/ArchipelagoMW/Archipelago/releases) 0.6.2 or newer, with the apworlds for your games installed
- [Universal Tracker](https://github.com/FarisTheAncient/Archipelago/releases?q=Tracker) (`tracker.apworld`) installed in Archipelago

## Getting started

1. Download **`APMultiTracker-vX.Y.Z.zip`** from [Releases](../../releases/latest), unzip, run `APMultiTracker.exe`.
2. Click **Install engine** when asked. It copies `apmultitracker.apworld` into Archipelago's `custom_worlds` folder.
   (Archipelago is found automatically; change it in Settings if yours is somewhere else.)
3. **Multiworlds → New multiworld**: paste the server (e.g. `archipelago.gg:38281`), enter one slot name and click
   **Find slots in this room** to add the rest. **Save & track**.

<p align="center">
  <img src="docs/screenshots/edit-multiworld.png" alt="Setting up a multiworld" width="900">
</p>

### archipelago.gg rooms

Hosted rooms work exactly like they do in UT (secure `wss://` connections are picked automatically). The server box
also accepts a `/connect archipelago.gg:12345` line or an `archipelago://Slot:password@archipelago.gg:12345` link.
Rooms on archipelago.gg go to sleep when nobody is connected and can come back on a **different port**: if a game
won't connect, open the room page to wake it, and update the port in the multiworld if it changed.

### YAMLs

Like UT, some games can rebuild their logic straight from the server and others need the player's YAML.
For those, set the multiworld's **Player YAMLs** folder to a folder holding that room's YAMLs (or leave it empty to use
UT's Players folder). After adding YAMLs, press the ⟳ button to rebuild.

<p align="center">
  <img src="docs/screenshots/multiworlds.png" alt="Saved multiworlds" width="440">
  <img src="docs/screenshots/settings.png" alt="Settings" width="440">
</p>

## How it works

```
APMultiTracker.exe  ◄── WebSocket (127.0.0.1) ──►  APMultiTracker Engine (inside Archipelago)
   WPF interface                                       one headless "Tracker" client per slot
                                                        + Universal Tracker's TrackerCore logic
```

The engine (`engine/apmultitracker`) is a small apworld. The app launches it with
`ArchipelagoLauncher.exe "APMultiTracker Engine"`, so it runs with your Archipelago version and every apworld you
have installed. Each slot connects with the `Tracker` tag (it never sends checks) and all logic runs on one worker
thread. The engine exits when the app closes.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
build.cmd          → dist\APMultiTracker.exe (needs the .NET 10 Desktop Runtime)
build.cmd full     → dist-standalone\APMultiTracker.exe
```

The build zips `engine/apmultitracker` into `apmultitracker.apworld` and embeds it in the exe.
`APMultiTracker.exe --selftest` opens every page with sample data (used by CI).

For engine development, point Settings → Archipelago folder at an Archipelago source checkout: the app runs
`Launcher.py` with its `.venv` Python.

## Privacy

Settings live in `%APPDATA%\APMultiTracker`. Room passwords and the optional Twitch token are encrypted for your
Windows user (DPAPI). The engine only listens on 127.0.0.1 and requires a per-session token.

## Credits

Logic by [Universal Tracker](https://github.com/FarisTheAncient/Archipelago) (FarisTheAncient and contributors).
Built on [Archipelago](https://archipelago.gg). Interface style shared with
[AutoSwitcher](https://github.com/Flippage/TwitchAutoSwitcher).
