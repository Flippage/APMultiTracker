# Changelog

## v0.11.0

- **Hint shop** (engine v0.4.8): a **Hints** toggle next to Items and Explain opens a panel for the focused game:
  - Your **hint points** and what a hint costs (the room's hint cost), and how many you can buy.
  - **Where is my item?**: search your game's items (with how many of each are still out there), pick one and press
    **Buy hint**. The button shows the cost, and the server's answer appears right under it. Re-showing a hint you
    already have is free.
  - Every hint for this game: **where your items are**, and **others' items in your world**, with found hints
    tucked away behind the **Found** switch.
- Right-click a check → **Hint this location** to find out what's there (like `!hint_location`).
- With the feed docked along the bottom, the Items / Hints column now runs down to the bottom of the window and the
  feed sits beside it, so the hint lists get the room.
- **Items received** can be sorted **latest first** (click **A–Z ▾** in the Items panel), so the newest items are on top.
- The **Pretend you have an item…** box works like Where is my item?: click into it (or its ▾) for the full item list,
  type to filter.
- Fix: clicking a button (like **Explain** or **Logical path**) left a cyan ring on it, which also made filled buttons
  a little wider. The ring now only shows when you move around with the Tab key, and never changes a button's size.

## v0.10.5

- **GO MODE notifications**: when one of your games goes into go mode, you get a Windows notification with the game's
  box art (or a banner in the corner when APMultiTracker is in front) and a short **GO MODE jingle**. Click it to open
  that game. It only fires for real go mode: not glitched, not games that were already in go mode when the app
  started, and not go mode that's only thanks to pretend items.
- Settings → **Notifications**: turn the notification or the jingle off, set the **jingle volume** (−/+), choose your
  own sound file, or press **Test**. The jingle stays quiet while Windows is in do not disturb, but does play over
  full-screen games. The notification shows for about 5 seconds (never more than 6) and stays in Windows' notification center.

## v0.10.4

- **Pretend you have an item** (engine v0.4.5): in the Items panel, type an item into **Pretend you have an item…**
  (suggestions come from the game's item list) to see what it would unlock, like Universal Tracker's
  `/manually_collect`. The logic counts pretend items as received, but they're not added to your items received. Each
  shows as an orange chip with an × to remove it, and while any are on, the game's header shows an orange
  **N pretend items** chip (click it to see them). Pretend items are saved per seed, like hidden checks.

## v0.10.3

- **Events** (engine v0.4.4): the Items panel (the **Items** toggle) has an **Events** tab next to Items received,
  listing the events the logic has reached, like Universal Tracker's `/event_inventory` and `/event_locations`: each
  event, with the location it comes from when that's different. Events are milestones the game's logic uses, like
  beating a boss or opening a door.

## v0.10.2

- **Hide or unhide everything at once**: right-click the check list for **Hide all in-logic checks…** (like Universal
  Tracker's `/ignore_all`) or **Unhide all hidden checks…** (like `/reset_ignored`). With the **Hidden** filter on, an
  **Unhide all** button does the same. Both ask first, and hidden checks stay hidden after a restart.

## v0.10.1

- **Excluded checks** (engine v0.4.3): checks excluded in a player's YAML (they only ever hold filler) are shown
  dimmed with an **EXCLUDED** tag, after the other checks in their region, and no longer count toward a game's number
  or keep it from showing BK. Settings → Tracker → **Hide excluded checks** leaves them out of the lists completely.
  Like in Universal Tracker, this works for games whose logic is built from the player's YAML.
- Fix: with Universal Tracker's `hide_excluded_locations` turned on in `host.yaml`, reachable excluded checks were
  shown as out of logic.
- Fix: in a narrow Tracker (e.g. with the feed docked), the completion % was cut off; it now moves to the next line.

## v0.10.0

- **New entrances** (engine v0.4.2): in games with entrance randomizers that reveal entrances as you find them (like
  SM64: Spicy Mycena 64's painting shuffle), entrances you can reach but haven't been through yet now show in the
  check lists, like Universal Tracker's unconnected entrances: the entrance's name in lime with a **NEW ENTRANCE**
  tag, under the region it's in. Once you go through one, it's replaced by the checks behind it.
- New entrances count toward a game's number on its card, so a game whose only way forward is a new painting isn't
  shown as BK. The focused game's header shows how many there are.
- **New entrances filter**: a **New entrances (N)** filter next to Hinted lists just the new entrances. It shows for
  games that have some.
- **Denser check list**: the focused game's checks take about 40% less height each (smaller hide button, tighter
  rows and region headers), so a lot more fit on screen.
- **Focused header**: the counts (in logic, glitched, hinted, new entrances) and go mode / goaled sit on the first
  line, the checked count with its bar and the completion % on the second.
- Hovering a check (in the focused list or an All games column) shows its full name.

## v0.9.2

- **Completion %**: the focused game's header shows how much of the game is checked, to two decimal places like
  the room page on archipelago.gg (e.g. **55.27%**), in a blue chip next to the checked count.

## v0.9.1

- **Patch notes in the app**: Settings → Updates → **Patch notes** shows what changed in every version. The current
  version is open; older ones are collapsed under their version number and open with a click.
- **Goal complete** (engine v0.4.1): once a game's goal is done, its number turns into a green tick, a **GOALED** pill
  replaces GO MODE and its progress bars turn green. If the room releases on goal (auto release), the bar fills up
  and the game's checks are cleared, since they've been sent out. If it doesn't, the remaining checks stay listed
  until the host releases them.
- **BK** now only shows when a game is really stuck: a game in go mode or with its goal done never shows BK, even
  with nothing left in logic.
- **Game cards**: the progress bar runs the full width along the bottom edge of the card, so cards are a line
  shorter. Connecting / Reconnecting / Error show in place of the slot name.

## v0.9.0

- **BK**: when a game has nothing in logic, its number shows a red **BK** instead of 0, on the game card, its All
  games column, the focused view and the sidebar.
- The in-logic number on game cards and All games columns is now centred on the box art (it sat about 2 px low).

## v0.8.1

- The **GO MODE** pill on an All games column now sits next to the in-logic number, lined up with it, instead of
  beside the game's name.

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
