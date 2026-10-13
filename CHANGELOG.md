# Changelog

## v0.8.0

- **GO MODE badge**: like Universal Tracker's "Go mode", a game whose goal can be reached with what's in logic now
  shows **GO MODE** on its game card, its All games column and the focused view (yellow, "GO MODE (GLITCHED)", if
  it needs glitches). Nothing is shown until then. The feed also notes when one of your games goes into go mode.

## v0.7.0

- **Follows archipelago.gg rooms to their new port**: a multiworld now has a **Room link** field (the room's page,
  `https://archipelago.gg/room/…`). Paste it and the server address is filled in for you. When a game can't connect,
  the app opens the room page to wake it, reads the room's current port and, if it changed, updates the multiworld
  and reconnects. The feed notes the move. Ports are also checked at startup. Pasting a room link into the Server box
  works too. With a room link, **Find slots in this room** lists every slot straight from the room page.
- **Opens the way you left it**: the window's size, position and maximised state are remembered (if that screen is
  still connected), along with the page you were on and the Tracker's Items and Explain toggles.

## v0.6.0

- **Stays up to date when left running** (engine v0.4.0): a connection that dies silently (PC asleep, Wi-Fi drop,
  router or host dropping an idle connection) used to leave the tracker frozen on old numbers. Each slot now notices
  when its server goes quiet, checks the connection, and reconnects by itself, then catches up. Retries are capped at
  about a minute apart (they used to back off to hours), and a room that's down when the app starts (such as a
  sleeping archipelago.gg room) is retried every minute until it's back.
- **Slimmer game cards**: the big green number stays; the "x in logic" line is gone and the progress bar sits in its
  place, so the cards are shorter. Status (Connecting, Reconnecting, Error) shows there only when it needs attention.
- **Cards fit beside the feed**: with the feed docked, the cards narrow (down to 130 px, dropping their small art)
  to stay on one row before wrapping.
- **Feed dock limits**: the docked feed can't be dragged so wide (or tall) that it squeezes the tracker or pushes
  itself off-screen. On a narrow tracker, labels shorten and the Feed button shows just its icon.

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
