# Changelog

## v0.5.0

- **Feed and Tracker together**: a new **Feed** button on the Tracker shows the feed right next to your checks,
  docked on the **right** (default) or along the **bottom**. Switch sides with the buttons in the feed's title bar,
  drag the divider to resize it (remembered for each side), and close it with ×. It's the full feed: filters,
  "Only my slots", highlights and the chat box.
- **Chat as the game you're looking at**: opening the Feed with a game focused makes that game's slot the sender;
  the docked feed follows as you switch games.

## v0.4.1

- Fix: switching to All games after the app started on a focused game showed no checks until the window was
  resized. The columns were sized before the view had ever been laid out (0 px tall). The self-test now starts on
  a focused game and checks this.

## v0.4.0

- **All games, grouped**: the All games view has its own "Group by region" option, with the same collapsible
  regions (click a header, Ctrl+click for all).
- **Box art collage**: with All games selected, the sidebar shows every game's box art in a small grid.
- **Explain panel**: no more flicker (the last answer stays until the new one replaces it), the button for the
  answer you're looking at is highlighted, and you can drag the panel's top edge to resize it.
- **Smaller windows**: the view toggles moved into the game's header card, so search and filters stay on one line;
  on shorter windows the header goes compact and the Explain panel never takes more than about half the space.
- **Bigger default window**: wide enough for four full game columns without a scrollbar.
- The search box no longer stretches tall when the filters wrap.

## v0.3.1

- **Collapsible regions**: click a region header in the check list to collapse or expand it; Ctrl+click does every
  region. Remembered per game.
- **Steadier Explain panel**: it keeps a fixed height and shows the previous answer (dimmed) while the next one
  loads, so the list no longer jumps. New answers fade in with a timestamp, so it's clear they updated.
- **Updates**: when GitHub can't show the app its releases (for example while the repository is private), Settings
  now says "Can't check for updates" instead of "Update failed".

## v0.3.0

- **Built-in updates**, like AutoSwitcher: checks GitHub for new versions, downloads in the background (verified
  against GitHub's SHA-256 checksum) and installs with one click, or automatically on the next launch.
  An "Update" badge and a toast let you know. See Settings → Updates.
- **Feed highlights your games**: any message involving one of the active multiworld's slots is highlighted green.
- **Feed is now the first item** in the sidebar (Feed, Tracker, Multiworlds, Settings).
- **App-styled menus**: right-click and dropdown menus match the rest of the app (no more white side bars),
  and always open to the right.
- **Explain / logical path fixes**: checks whose names end in brackets (e.g. "(Huge)") are now looked up by their
  real name, and any error from Universal Tracker or a world is shown instead of nothing happening.

## v0.2.0

- **Feed**: a new page with the room's messages as they happen, like the Text Client. Items sent (coloured by
  importance), hints, chat, goals and releases, with filters and "Only my slots". Chat or run commands like
  `!hint` as any of your slots. Each message shows once, and the tracker's own connections are hidden.
- **Several multiworlds at once**: activated multiworlds stay connected in the background, so switching is instant.
  The sidebar shows the active one. Switch from the Tracker's "Switch multiworld" menu or the Multiworlds page.
- Each multiworld remembers which game you had focused.
- The engine updates itself automatically when the app has a newer one.

## v0.1.0

First version.

- Track every slot in a multiworld from one window, using Universal Tracker's logic.
- All-games overview with each game's in-logic checks side by side, or focus one game with search, filters
  (In logic / + Glitched / All remaining / Hinted / Hidden), grouping by region and received items.
- Switch games with the tiles, Ctrl+1…9, Ctrl+0 (all games) or Ctrl+Tab.
- Explain and logical path (UT's `/explain` and `/get_logical_path`): double-click a check, right-click it, or type a name.
- Hints show the item and who it's for. Hide checks you've done (saved per seed).
- Box art from Twitch by game name, no login needed. Optional Twitch connect to search for art.
- Saved multiworlds with "Find slots in this room".
- One-click install of the tracker engine into Archipelago; the app starts and stops it for you.
