"""
The engine: one process, many tracked slots.

* Each tracked slot is a headless Archipelago client (tag "Tracker", so it never sends checks) with its own
  Universal Tracker ``TrackerCore``.
* All logic work (generation and logic sweeps) runs on a single worker thread, so the event loop stays free
  for network traffic and slots never touch a multiworld at the same time.
* The desktop app talks to us over a WebSocket on 127.0.0.1 with small JSON messages (see ``Engine.handle``).
"""
from __future__ import annotations

import collections

import argparse
import asyncio
import json
import logging
import os
import sys
import time
import traceback
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable, Optional

import websockets

import Utils
from CommonClient import CommonContext, server_loop
from worlds import AutoWorld

from . import ENGINE_VERSION

DEFAULT_PORT = 38291
# A slot that hears nothing from its server for this long is treated as dead and reconnects. Archipelago sends a
# keep-alive every 100 s and the server echoes it, so a live connection is never this quiet.
STALE_SECONDS = float(os.environ.get("APMT_STALE_SECONDS", "240"))
MAX_RETRY_SECONDS = 30   # CommonClient doubles its reconnect delay forever; we cap it
PERSIST_KEY = "apmultitracker"

log = logging.getLogger("APMultiTracker")

if not sys.stdout:  # frozen GUI builds have no stdout; some worlds print() during generation
    sys.stdout = open(os.devnull, "w", encoding="utf-8")


class EngineError(Exception):
    pass


def _ut():
    """Universal Tracker's modules, imported lazily so a missing UT is reported instead of crashing."""
    try:
        from worlds.tracker import UT_VERSION, DeferredEntranceMode
        from worlds.tracker.TrackerCore import TrackerCore
        return TrackerCore, UT_VERSION, DeferredEntranceMode
    except Exception as e:
        raise EngineError("Universal Tracker (tracker.apworld) isn't installed in Archipelago, "
                          "or this version of it isn't supported.") from e


def ut_version() -> str:
    try:
        return _ut()[1]
    except EngineError:
        return ""


def apply_host_settings(core) -> None:
    """The settings TrackerCore.run_generator would load; needed before a core can do anything else."""
    from worlds.tracker import TrackerWorld
    # UT's Players folder setting must exist, or reading it fails (we never show settings dialogs).
    players = object.__getattribute__(TrackerWorld.settings, "player_files_path")
    try:
        if not players.exists():
            os.makedirs(players.resolve(), exist_ok=True)
    except OSError:
        pass
    (_, core.output_format, core.hide_excluded, core.use_split, deferred, core.enable_glitched_logic,
     core.sorting_priorities, core.sorting_method) = core._set_host_settings()
    if core.enforce_deferred_connections is None:
        core.enforce_deferred_connections = deferred
    # Always report excluded checks with their real status; the app decides whether to show them
    # (with UT's hide_excluded_locations on, they'd otherwise look out of logic).
    core.hide_excluded = False


def last_line(text: str) -> str:
    lines = [line.strip() for line in (text or "").strip().splitlines() if line.strip()]
    return lines[-1] if lines else ""


# ---------------------------------------------------------------------------------------------- slots

class SlotContext(CommonContext):
    """A read-only tracker connection for one slot."""
    game = ""
    tags = CommonContext.tags | {"Tracker"}
    items_handling = 0b111
    want_slot_data = True

    def __init__(self, engine: "Engine", sid: str, server: str, slot_name: str, password: Optional[str],
                 yaml_folder: str, room: str = ""):
        super().__init__(server, password or None)
        self.engine = engine
        self.sid = sid
        self.auth = slot_name
        self.slot_name = slot_name  # CommonContext clears .auth on disconnect
        self.requested_server = server
        self.room = room or server
        self.yaml_folder = yaml_folder or ""
        self.core = None
        self.logic_ready = False
        self.status = "connecting"
        self.message = ""
        self.slot_data: dict = {}
        self.region_by_name: dict[str, str] = {}
        self.deferred_keys: list[str] = []
        self.deferred_callback: Optional[Callable] = None
        self.persist_id = ""
        self.room_seed = ""
        self.warning = ""
        self.last_packet = time.monotonic()
        self.last_attempt = time.monotonic()
        self.retry_failed_connect = False   # first connection failed (e.g. room asleep): keep trying
        self._hint_capture: Optional[list] = None   # server replies collected while buying a hint
        self._hint_lock = asyncio.Lock()
        self.updater_task = asyncio.create_task(self._updater(), name=f"updater {sid}")
        self.watchdog_task = asyncio.create_task(self._watchdog(), name=f"watchdog {sid}")

    # ---- connection

    async def server_auth(self, password_requested: bool = False):
        if password_requested and not self.password:
            self.set_status("error", "This room needs a password.")
            await self.disconnect(False)
            return
        # CommonClient clears .auth when a connection closes and would otherwise ask the (absent) console for it.
        if not self.auth:
            self.auth = self.slot_name
        await self.get_username()
        await self.send_connect(game="")

    def event_invalid_slot(self):
        self.set_status("error", f"There's no slot called “{self.slot_name}” in this room.")
        self.engine.spawn(self.disconnect(False), f"disconnect {self.sid}")

    def event_invalid_game(self):
        self.set_status("error", "The server refused the connection (invalid game).")
        self.engine.spawn(self.disconnect(False), f"disconnect {self.sid}")

    def handle_connection_loss(self, msg: str) -> None:
        log.warning(f"[{self.slot_name}] {msg}")
        self.current_reconnect_delay = min(self.current_reconnect_delay, MAX_RETRY_SECONDS)
        self.retry_failed_connect = not self.username and not self.disconnected_intentionally
        if "archipelago.gg" in self.requested_server.lower() and not self.logic_ready:
            # Hosted rooms sleep after a while idle and can come back on a different port.
            msg += (" If the room has been idle, open its page on archipelago.gg to wake it up, "
                    "and check the port hasn't changed.")
        if self.username and not self.disconnected_intentionally:
            self.set_status("reconnecting", msg + " Reconnecting…")
        elif self.retry_failed_connect:
            self.set_status("reconnecting", msg + " Trying again every minute…")
        else:
            self.set_status("error", msg)

    async def connection_closed(self):
        self.logic_ready = False
        await super().connection_closed()
        if self.status not in ("error", "stopped") and not self.disconnected_intentionally:
            self.set_status("reconnecting", "Lost connection to the server. Reconnecting…")

    def on_print_json(self, args: dict):
        if self._hint_capture is not None and args.get("type") in ("CommandResult", "Hint"):
            try:
                self._hint_capture.append(self._render(args.get("data") or [], frozenset({self.slot})))
            except Exception:
                log.error(traceback.format_exc())
        try:
            self.engine.on_feed(self, args)
        except Exception:
            log.error(traceback.format_exc())

    def on_package(self, cmd: str, args: dict):
        self.last_packet = time.monotonic()   # anything from the server proves the connection is alive
        if cmd == "Connected":
            self.retry_failed_connect = False
            self.current_reconnect_delay = self.starting_reconnect_delay
        try:
            if cmd == "RoomInfo":
                self.room_seed = args.get("seed_name", "")
            elif cmd == "Connected":
                self.slot_data = args.get("slot_data") or {}
                self.set_notify(self._status_key)   # goal reached? (resent on every reconnect by CommonContext)
                self.engine.spawn(self._initialize(), f"init {self.sid}")
            elif cmd in ("RoomUpdate", "ReceivedItems", "LocationInfo"):
                self.watcher_event.set()
            elif cmd == "Retrieved":
                keys = args.get("keys", {})
                if self._hint_key in keys or self._status_key in keys:
                    self.watcher_event.set()
                self._deferred_changed([k for k in self.deferred_keys if k in keys])
            elif cmd == "SetReply":
                key = args.get("key", "")
                if key in (self._hint_key, self._status_key):
                    self.watcher_event.set()
                self._deferred_changed([key] if key in self.deferred_keys else [])
        except Exception as e:
            log.exception(e)
            self.set_status("error", f"Tracker error: {e}")

    @property
    def _hint_key(self) -> str:
        return f"_read_hints_{self.team}_{self.slot}"

    @property
    def _status_key(self) -> str:
        return f"_read_client_status_{self.team}_{self.slot}"

    # ---- status

    def set_status(self, status: str, message: str = "") -> None:
        if status == self.status and message == self.message:
            return
        self.status = status
        self.message = message
        self.engine.send_status(self)

    def status_payload(self) -> dict:
        game = ""
        if self.slot is not None and self.slot in self.slot_info:
            game = self.slot_info[self.slot].game
        return {"type": "status", "id": self.sid, "status": self.status, "message": self.message,
                "game": game, "slot_name": self.slot_name}

    # ---- tracker core

    async def _initialize(self, rebuild: bool = False):
        self.logic_ready = False
        if self.slot is None:
            return
        game = self.slot_info[self.slot].game
        slot_name = self.slot_info[self.slot].name
        persist_id = f"{self.room_seed}:{self.team}:{self.slot}"
        if self.core is not None and persist_id == self.persist_id and not rebuild:
            self.logic_ready = True  # reconnected to the same room: the logic we built still applies
            self.set_status("tracking", "")
            self.watcher_event.set()
            return
        self.persist_id = persist_id
        self.core = None
        self.set_status("generating", "Building logic…")
        try:
            await self.engine.run_logic(self._init_core, game, slot_name, dict(self.slot_data))
        except EngineError as e:
            self.set_status("error", str(e))
            return
        except Exception as e:
            log.error(traceback.format_exc())
            self.set_status("error", f"Couldn't build logic for {game}: {e}")
            return
        if self.deferred_keys:
            self.set_notify(*self.deferred_keys)
        self.logic_ready = True
        self.set_status("tracking", "")
        self.watcher_event.set()

    def _init_core(self, game: str, slot_name: str, slot_data: dict) -> None:
        """Worker thread."""
        TrackerCore, _, DeferredEntranceMode = _ut()
        world_cls = AutoWorld.AutoWorldRegister.world_types.get(game)
        if world_cls is None:
            raise EngineError(f"The apworld for “{game}” isn't installed in Archipelago.")
        if getattr(world_cls, "disable_ut", False):
            raise EngineError(f"The {game} apworld has asked trackers not to track it.")

        core = TrackerCore(log, False, False)
        apply_host_settings(core)
        core.set_slot_params(game, self.slot, slot_name, self.team)
        if not getattr(world_cls, "ut_can_gen_without_yaml", False):
            shared = self.engine.yaml_world(self.yaml_folder)
            core.launch_multiworld = shared.launch_multiworld
            core.common_option_overrides = dict(shared.common_option_overrides)
            core.player_folder_override = shared.player_folder_override
            if shared.launch_multiworld is None:
                where = self.yaml_folder or "Universal Tracker's Players folder"
                detail = last_line(shared.gen_error)
                raise EngineError(f"{game} needs your YAML, but the YAMLs in {where} couldn't be loaded"
                                  + (f": {detail}" if detail else "."))
        core.initalize_tracker_core(world_cls, slot_data)
        # If a world's rebuild from slot_data fails, UT keeps the YAML multiworld but leaves player_id at 1,
        # which can point at a different game. Point it back at this slot, or give up cleanly.
        if core.multiworld is not None and core.player_id is not None \
                and core.multiworld.worlds.get(core.player_id) is not None \
                and core.get_current_world().game != game:
            fallback = core.multiworld.world_name_lookup.get(slot_name)
            if fallback is not None and core.multiworld.worlds[fallback].game == game:
                core.player_id = fallback
            else:
                raise EngineError(f"Universal Tracker couldn't rebuild {game}'s logic: "
                                  f"{last_line(core.gen_error) or 'unknown error'}")
        if core.tracker_disabled:
            raise EngineError(core.gen_error or f"Universal Tracker can't track {game} with these options.")
        if core.multiworld is None or core.player_id is None:
            errors = [line.location_label for lines in core.log_lines.values() for line in lines]
            detail = errors[0] if errors else last_line(core.gen_error)
            if "Unable to find YAML" in detail:
                detail = (f"No YAML for “{slot_name}” in "
                          f"{self.yaml_folder or 'the Players folder'}. Add it, then press Refresh.")
            raise EngineError(detail or f"Couldn't build logic for {game}.")

        # A world can fail to rebuild from slot_data yet leave usable (if less accurate) logic behind.
        self.warning = (f"Universal Tracker couldn't fully rebuild {game}'s logic, so results may be "
                        f"incomplete: {last_line(core.gen_error)}") if core.gen_error else ""
        world = core.get_current_world()
        self.region_by_name = {
            loc.name: (loc.parent_region.name if loc.parent_region is not None else "")
            for loc in core.multiworld.get_locations(core.player_id)
        }
        saved = Utils.persistent_load().get(PERSIST_KEY, {}).get(self.persist_id, {})
        core.ignored_locations = set(saved.get("ignored", []))
        # Pretend items (UT's /manually_collect), kept only if the game still has them
        names = set(world.item_name_to_id)
        if hasattr(core, "manual_items"):
            core.manual_items = [n for n in saved.get("manual", []) if n in names]

        keys = getattr(world, "found_entrances_datastorage_key", None)
        callback = getattr(world, "reconnect_found_entrances", None)
        if keys and callable(callback) and core.enforce_deferred_connections != DeferredEntranceMode.disabled:
            keys = [keys] if isinstance(keys, str) else list(keys)
            self.deferred_keys = [k.format(player=self.slot, team=self.team) for k in keys]
            self.deferred_callback = callback
        self.core = core

    def _deferred_changed(self, keys: list[str]) -> None:
        if not keys or not self.deferred_callback or not self.logic_ready:
            return
        values = {k: self.stored_data.get(k) for k in keys}

        def work():
            for k, v in values.items():
                self.deferred_callback(k, v)

        async def run():
            await self.engine.run_logic(work)
            self.watcher_event.set()

        self.engine.spawn(run(), f"entrances {self.sid}")

    async def _updater(self):
        while True:
            await self.watcher_event.wait()
            await asyncio.sleep(0.12)  # collapse bursts (item floods, several packets per check)
            self.watcher_event.clear()
            if not self.logic_ready or self.core is None or self.slot is None:
                continue
            try:
                await self.update()
            except Exception as e:
                log.error(traceback.format_exc())
                self.set_status("error", f"Tracker error: {e}")

    async def update(self):
        payload = await self.engine.run_logic(self._snapshot, self._inputs())
        self.engine.send_snapshot(self.sid, payload)

    def _inputs(self) -> dict:
        """Snapshot of the network state, taken on the event loop and handed to the logic thread."""
        from NetUtils import HintStatus
        hints = {}
        for hint in self.stored_data.get(self._hint_key, None) or []:
            if (hint["status"] not in (HintStatus.HINT_FOUND, HintStatus.HINT_AVOID)
                    and self.slot_concerns_self(hint["finding_player"])):
                hints[hint["location"]] = hint
        inputs = {
            "items": list(self.items_received),
            "missing": set(self.missing_locations),
            "checked": len(self.checked_locations),
            "total": len(self.checked_locations | self.missing_locations),
            "hints": hints,
            "hint_text": {loc: self._hint_text(h) for loc, h in hints.items()},
            "game": self.slot_info[self.slot].game,
            "server_locations": set(self.server_locations),
            "goal": self.stored_data.get(self._status_key) == 30,   # ClientStatus.CLIENT_GOAL
            "hint_points": int(self.hint_points or 0),
            "hint_percent": int(self.hint_cost or 0),
            "hint_list": self._hint_list(),
            # The room releases a slot's remaining items when it goals ("auto" / "auto_enabled" release mode)
            "auto_release": "auto" in str(self.permissions.get("release", "")),
        }
        return inputs

    def _hint_list(self) -> list[dict]:
        """Every hint involving this slot: where its items are, and what others' items sit in its world."""
        out = []
        for hint in self.stored_data.get(self._hint_key, None) or []:
            try:
                receiver, finder = hint["receiving_player"], hint["finding_player"]
                out.append({
                    "item": self.item_names.lookup_in_slot(hint["item"], receiver),
                    "location": self.location_names.lookup_in_slot(hint["location"], finder),
                    "receiver": self.player_names.get(receiver, f"Player {receiver}"),
                    "finder": self.player_names.get(finder, f"Player {finder}"),
                    "entrance": hint.get("entrance", "") or "",
                    "found": bool(hint.get("found")),
                    "status": int(hint.get("status", 0) or 0),
                    "mine": self.slot_concerns_self(receiver),       # one of this slot's items
                    "in_my_world": self.slot_concerns_self(finder),  # sits in this slot's world
                    "prog": bool(int(hint.get("item_flags", 0) or 0) & 0b001),
                })
            except Exception:
                log.error(traceback.format_exc())
        return out

    def hint_cost_points(self, total_locations: int) -> int:
        """What one hint costs this slot, as the server works it out (a % of the slot's locations)."""
        percent = int(self.hint_cost or 0)
        return max(1, int(percent * 0.01 * total_locations)) if percent else 0

    def hint_items(self) -> list[dict]:
        """Worker thread: this game's items from the rebuilt item pool, how many are still out there, and hint state."""
        core = self.core
        if core is None or core.multiworld is None:
            raise EngineError(f"{self.slot_name} isn't tracking yet.")
        mw = core.multiworld
        pool = collections.Counter(i.name for i in mw.itempool if i.player == core.player_id and i.code is not None)
        # Items a game places directly (locked keys, vanilla items) aren't in the pool but are just as hintable.
        for loc in mw.get_locations():
            item = loc.item
            if item is not None and item.player == core.player_id and item.code is not None and loc.locked:
                pool[item.name] += 1
        received = collections.Counter(self.item_names.lookup_in_slot(i.item, self.slot) for i in list(self.items_received))
        hinted, found = set(), set()
        for h in self._hint_list():
            if h["mine"]:
                (found if h["found"] else hinted).add(h["item"])
        names = set(pool) or set(core.get_current_world().item_name_to_id)
        return sorted(({"name": n, "total": pool.get(n, 0), "left": max(0, pool.get(n, 0) - received.get(n, 0)),
                        "hinted": n in hinted, "found": n in found and n not in hinted} for n in names),
                      key=lambda i: i["name"].lower())

    async def buy_hint(self, kind: str, name: str) -> list[list[dict]]:
        """Asks the server for a hint (!hint item / !hint_location location) as this slot; returns its replies."""
        if not self._socket_open() or self.slot is None:
            raise EngineError(f"{self.slot_name} isn't connected right now.")
        name = name.strip()
        if not name:
            raise EngineError("Pick something to hint first.")
        async with self._hint_lock:
            self._hint_capture = []
            try:
                command = "!hint_location" if kind == "location" else "!hint"
                await self.send_msgs([{"cmd": "Say", "text": f"{command} {name}"}])
                waited = 0.0
                while waited < 6.0:
                    await asyncio.sleep(0.1)
                    waited += 0.1
                    if self._hint_capture:
                        await asyncio.sleep(0.6)   # the result often comes as several lines
                        break
                return list(self._hint_capture)
            finally:
                self._hint_capture = None

    def _hint_text(self, hint: dict) -> str:
        item = self.item_names.lookup_in_slot(hint["item"], hint["receiving_player"])
        if hint["receiving_player"] == self.slot:
            return item
        return f"{item} → {self.player_names.get(hint['receiving_player'], 'someone')}"

    def _sweep(self, inputs: dict):
        """Worker thread: feed the latest items/checks/hints to UT and run its logic sweep."""
        core = self.core
        core.set_missing_locations(inputs["missing"])
        core.set_items_received(inputs["items"])
        core.set_hints({loc: h["status"] for loc, h in inputs["hints"].items()})
        return core.updateTracker()

    def _snapshot(self, inputs: dict) -> dict:
        """Worker thread: run the logic sweep and describe every missing location."""
        from BaseClasses import LocationProgressType
        core = self.core
        ret = self._sweep(inputs)

        world = core.get_current_world()
        mw = core.multiworld
        id_to_name = world.location_id_to_name
        cache = mw.regions.location_cache[core.player_id]
        in_logic = set(core.locations_available)
        glitched = set(core.glitched_locations)
        aliases = core.location_alias_map or {}
        locations = []
        for loc_id in inputs["missing"]:
            name = id_to_name.get(loc_id) or self.location_names.lookup_in_game(loc_id, inputs["game"])
            loc = cache.get(name)
            if loc_id in core.ignored_locations:
                status = "ignored"
            elif loc_id in in_logic:
                status = "in_logic"
            elif loc_id in glitched:
                status = "glitched"
            else:
                status = "out_of_logic"
            label = f"{name} ({aliases[loc_id]})" if loc_id in aliases else name
            locations.append({
                "id": loc_id,
                "name": label,
                "key": name,  # the real location name (label may carry an alias), used for explain/path
                "region": self.region_by_name.get(name, ""),
                "status": status,
                "excluded": bool(loc is not None and loc.progress_type == LocationProgressType.EXCLUDED),
                "hint": inputs["hint_text"].get(loc_id),
            })

        go_mode = "no"
        if ret.state is not None and mw.has_beaten_game(ret.state, core.player_id):
            go_mode = "yes"
        elif ret.glitches_state is not None and mw.has_beaten_game(ret.glitches_state, core.player_id):
            go_mode = "glitched"

        # Entrances you can reach that don't lead anywhere known yet (deferred entrance randomizers): UT lists these
        # as "unconnected" lines until the game reports where they go.
        unconnected = getattr(ret, "unconnected_entrances", None)
        if unconnected is None and ret.state is not None:
            unconnected = [e for region in ret.state.reachable_regions[core.player_id] for e in region.exits
                           if e.connected_region is None and e.can_reach(ret.state)]
        entrances, seen = [], set()
        for e in unconnected or []:
            if e.name in seen:
                continue
            seen.add(e.name)
            entrances.append({"name": e.name, "region": e.parent_region.name if e.parent_region else ""})

        errors = [line.location_label for group, lines in core.log_lines.items() if group < 0 for line in lines]
        # Events the logic has reached (UT's /event_inventory and /event_locations): the event and where it's from.
        events, seen_events = [], set()
        if ret.state is not None:
            for loc in ret.state.advancements:
                if loc.player != core.player_id or loc.item is None or loc.name in seen_events:
                    continue
                seen_events.add(loc.name)
                events.append({"name": loc.item.name, "location": loc.name})
            events.sort(key=lambda e: (e["name"], e["location"]))
        # UT counts pretend items (/manually_collect) as received; they aren't, so leave them out here.
        pretend = collections.Counter(getattr(core, "manual_items", []))
        # When each item last arrived (its position in the received list), for "latest first" sorting.
        last = {}
        for index, received in enumerate(inputs["items"]):
            last[self.item_names.lookup_in_slot(received.item, self.slot)] = index
        items = [{"name": name, "count": count - pretend[name], "prog": name in ret.prog_items, "last": last.get(name, -1)}
                 for name, count in ret.all_items.items() if count - pretend[name] > 0]
        return {
            "type": "slot",
            "id": self.sid,
            "status": "tracking",
            "message": "",
            "game": inputs["game"],
            "slot_name": self.slot_name,
            "slot": self.slot,
            "seed": self.persist_id,
            "checked": inputs["checked"],
            "total": inputs["total"],
            "in_logic": sum(1 for loc in locations if loc["status"] == "in_logic"),
            "glitched": sum(1 for loc in locations if loc["status"] == "glitched"),
            "hinted": sum(1 for loc in locations if loc["hint"] and loc["status"] != "ignored"),
            "ignored": len(core.ignored_locations & inputs["missing"]),
            "go_mode": go_mode,
            "goal": inputs["goal"],
            "auto_release": inputs["auto_release"],
            "entrances": [] if inputs["goal"] and inputs["auto_release"] else entrances,
            "locations": locations,
            "items": items,
            "events": events,
            "hint_points": inputs["hint_points"],
            "hint_cost": self.hint_cost_points(inputs["total"]),
            "hint_percent": inputs["hint_percent"],
            "hints": inputs["hint_list"],
            "manual_items": list(getattr(core, "manual_items", [])),
            "errors": errors,
            "warning": self.warning,
        }

    # ---- explain / logical path

    async def explain(self, query: str, mode: str) -> list[list[dict]]:
        """UT's /explain or /get_logical_path for this slot, as lines of coloured text segments."""
        if not self.logic_ready or self.core is None or self.slot is None:
            return [[{"text": "This game isn't being tracked yet.", "color": "red"}]]
        if self.stored_data.get("_read_race_mode"):
            return [[{"text": "Explaining logic is disabled in race mode.", "color": "red"}]]
        return await self.engine.run_logic(self._explain, self._inputs(), query, mode)

    def _explain(self, inputs: dict, query: str, mode: str) -> list[list[dict]]:
        """Worker thread. Runs UT's own explain/get_logical_path against a small stand-in for its client."""
        from worlds.tracker import TrackerClient
        ret = self._sweep(inputs)
        shim = _ExplainContext(self.core, ret, inputs["server_locations"])
        capture = _ThreadLogCapture(shim.messages)
        client_log = logging.getLogger("Client")
        client_log.addHandler(capture)
        try:
            if mode == "path":
                TrackerClient.get_logical_path(shim, query)
            else:
                TrackerClient.explain(shim, query)
        except Exception as e:
            log.error(traceback.format_exc())
            shim.messages.append([{"type": "color", "color": "red", "text": f"Universal Tracker couldn't do that: {e}"}])
        finally:
            client_log.removeHandler(capture)
        lines = []
        for parts in shim.messages:
            try:
                rendered = self._render(parts)
            except Exception as e:
                log.error(traceback.format_exc())
                rendered = [{"text": f"(couldn't show part of the answer: {e})", "color": "red"}]
            if rendered:
                lines.append(rendered)
        return lines or [[{"text": "Nothing to explain.", "color": ""}]]

    def _render(self, parts, own_slots: frozenset = frozenset()) -> list[dict]:
        """AP JSON message parts → plain segments the app can colour (names resolved here)."""
        def flatten(value):
            if value is None:
                return
            if isinstance(value, (list, tuple)):
                for v in value:
                    yield from flatten(v)
            elif isinstance(value, dict):
                yield value
            else:
                yield {"type": "text", "text": str(value)}

        out = []
        for p in flatten(parts):
            kind = p.get("type", "text")
            text = str(p.get("text", ""))
            color = p.get("color", "") if kind == "color" else ""
            try:
                if kind == "player_id":
                    slot = int(text)
                    text = self.player_names.get(slot, text)
                    color = "player_self" if slot in own_slots else "player"
                elif kind == "item_id":
                    text = self.item_names.lookup_in_slot(int(text), p.get("player", self.slot))
                    flags = int(p.get("flags", 0))
                    color = "item_prog" if flags & 1 else "item_useful" if flags & 2 else "item_trap" if flags & 4 \
                        else "item"
                elif kind == "hint_status":
                    color = {0: "", 10: "slateblue", 20: "salmon", 30: "plum", 40: "green"}.get(
                        int(p.get("hint_status", 0)), "")
                elif kind == "location_id":
                    text, color = self.location_names.lookup_in_slot(int(text), p.get("player", self.slot)), "location"
                elif kind in ("item_name", "location_name", "entrance_name", "player_name"):
                    color = kind.split("_")[0]
            except (ValueError, KeyError):
                pass
            out.append({"text": text, "color": color})
        return out

    # ---- commands

    async def set_ignored(self, location_ids: list[int], ignored: bool):
        if self.core is None:
            return
        if ignored:
            self.core.ignored_locations.update(location_ids)
        else:
            self.core.ignored_locations.difference_update(location_ids)
        self._persist()
        self.watcher_event.set()

    def _persist(self) -> None:
        """Hidden checks and pretend items, saved per seed and slot (like UT's save_entered_commands)."""
        Utils.persistent_store(PERSIST_KEY, self.persist_id, {
            "ignored": sorted(self.core.ignored_locations),
            "manual": list(getattr(self.core, "manual_items", [])),
        })

    def game_item_names(self) -> list[str]:
        if self.core is None:
            raise EngineError(f"{self.slot_name} isn't tracking yet.")
        return sorted(self.core.get_current_world().item_name_to_id)

    async def set_manual(self, items: list[str]) -> None:
        """Pretend to have these items (UT's /manually_collect); an empty list clears them."""
        core = self.core
        if core is None:
            raise EngineError(f"{self.slot_name} isn't tracking yet.")
        if not hasattr(core, "manual_items"):
            raise EngineError("This version of Universal Tracker can't pretend to have items. Update Universal Tracker.")
        world = core.get_current_world()
        unknown = [n for n in items if n not in world.item_name_to_id]
        if unknown:
            raise EngineError(f"{world.game} has no item called “{unknown[0]}”.")
        core.manual_items = list(dict.fromkeys(items))   # no duplicates, keep order
        self._persist()
        self.watcher_event.set()

    async def say(self, text: str):
        if self.slot is None:
            raise EngineError(f"{self.slot_name} isn't connected right now.")
        await self.send_msgs([{"cmd": "Say", "text": text}])

    # ---- staying connected

    def _socket_open(self) -> bool:
        return bool(self.server and self.server.socket is not None and not self.server.socket.closed)

    async def _watchdog(self):
        """Notices connections that died silently (PC slept, network dropped) and keeps retrying ones that failed."""
        tick = min(15.0, max(1.0, STALE_SECONDS / 8))
        while True:
            await asyncio.sleep(tick)
            if self.status == "stopped":
                return
            self.current_reconnect_delay = min(self.current_reconnect_delay, MAX_RETRY_SECONDS)
            now = time.monotonic()
            try:
                if self._socket_open() and self.slot is not None:
                    quiet = now - self.last_packet
                    if quiet > STALE_SECONDS:
                        log.warning(f"[{self.slot_name}] Nothing from the server for {quiet:.0f}s; reconnecting")
                        self.set_status("reconnecting", "The connection went quiet. Reconnecting…")
                        try:
                            await asyncio.wait_for(self.server.socket.close(), 10)
                        except Exception:
                            pass
                        # server_loop ends, and CommonClient's auto-reconnect takes over (delay capped above)
                    elif quiet > STALE_SECONDS / 2:
                        # Bounce to ourselves: the server echoes it back, proving the connection works.
                        await self.send_msgs([{"cmd": "Bounce", "slots": [self.slot]}])
                elif self.retry_failed_connect and (self.server_task is None or self.server_task.done()) \
                        and now - self.last_attempt > 60:
                    self.last_attempt = now
                    log.info(f"[{self.slot_name}] Retrying connection to {self.requested_server}")
                    self.set_status("connecting", "")
                    self.server_task = asyncio.create_task(server_loop(self, self.requested_server),
                                                           name=f"server loop {self.sid}")
            except Exception:
                log.error(traceback.format_exc())

    async def stop(self):
        self.set_status("stopped", "")
        self.updater_task.cancel()
        self.watchdog_task.cancel()
        await self.shutdown()


class _ExplainContext:
    """Just enough of UT's TrackerGameContext for its explain/get_logical_path helpers."""

    def __init__(self, core, ret, server_locations: set):
        self.tracker_core = core
        self._ret = ret
        self.server_locations = server_locations
        self.ui = True  # get_logical_path refuses to run without a UI
        self.messages: list = []

    def updateTracker(self):
        return self._ret

    def print_json(self, parts):
        self.messages.append(parts)

    def set_page(self, line: str):
        self.messages.append([{"type": "color", "color": "red", "text": line}])


class _ThreadLogCapture(logging.Handler):
    """Collects log lines written by the current thread only (other slots may be logging meanwhile)."""

    def __init__(self, messages: list):
        super().__init__(logging.INFO)
        import threading
        self.thread = threading.get_ident()
        self.messages = messages  # shared with print_json, so lines keep UT's order

    def emit(self, record: logging.LogRecord) -> None:
        if record.thread == self.thread:
            color = "red" if record.levelno >= logging.WARNING else ""
            self.messages.append([{"type": "color" if color else "text", "color": color, "text": record.getMessage()}])


class ProbeContext(CommonContext):
    """Connects as a slot just long enough to read the room's player list."""
    game = ""
    tags = CommonContext.tags | {"Tracker", "TextOnly"}
    items_handling = 0b000
    want_slot_data = False

    def __init__(self, server: str, slot_name: str, password: Optional[str]):
        super().__init__(server, password or None)
        self.auth = slot_name
        self.result: asyncio.Future = asyncio.get_running_loop().create_future()

    def _finish(self, value):
        if not self.result.done():
            self.result.set_result(value)

    async def server_auth(self, password_requested: bool = False):
        if password_requested and not self.password:
            self._finish("This room needs a password.")
            return
        await self.get_username()
        await self.send_connect(game="")

    def event_invalid_slot(self):
        self._finish(f"There's no slot called “{self.auth}” in this room.")

    def event_invalid_game(self):
        self._finish("The server refused the connection.")

    def handle_connection_loss(self, msg: str) -> None:
        self._finish(msg)

    def on_print_json(self, args: dict):
        pass

    def on_package(self, cmd: str, args: dict):
        if cmd == "Connected":
            players = [{"slot": slot, "name": info.name, "game": info.game}
                       for slot, info in sorted(self.slot_info.items())
                       if slot > 0 and int(info.type) == 1]
            self._finish(players)


# ---------------------------------------------------------------------------------------------- engine

class Engine:
    def __init__(self, port: int, token: str, parent_pid: int):
        self.port = port
        self.token = token
        self.parent_pid = parent_pid
        self.slots: dict[str, SlotContext] = {}
        self.clients: set = set()
        self.last: dict[str, dict] = {}
        self.yaml_worlds: dict[str, Any] = {}
        self.feed: dict[str, deque] = {}            # room -> recent messages (sent to the app when it connects)
        self.feed_recent: dict[str, dict] = {}      # room -> message key -> (time, slots that delivered it)
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="apmt-logic")
        self.stop_event = asyncio.Event()
        self.tasks: set[asyncio.Task] = set()

    # ---- plumbing

    def spawn(self, coro, name: str) -> None:
        task = asyncio.create_task(coro, name=name)
        self.tasks.add(task)
        task.add_done_callback(self._task_done)

    def _task_done(self, task: asyncio.Task) -> None:
        self.tasks.discard(task)
        if not task.cancelled() and task.exception() is not None:
            log.error(f"Task {task.get_name()} failed", exc_info=task.exception())

    async def run_logic(self, fn: Callable, *args):
        return await asyncio.get_running_loop().run_in_executor(self.executor, fn, *args)

    def yaml_world(self, folder: str):
        """Worker thread: generate from a folder of YAMLs once, shared by every slot that needs it."""
        key = os.path.normcase(os.path.abspath(folder)) if folder else ""
        if key not in self.yaml_worlds:
            TrackerCore = _ut()[0]
            core = TrackerCore(log, False, False)
            if folder:
                if not os.path.isdir(folder):
                    core.gen_error = f"Folder not found: {folder}"
                    self.yaml_worlds[key] = core
                    return core
                core.player_folder_override = folder
            log.info(f"Generating from YAMLs in {folder or 'the UT Players folder'}")
            core.run_generator(None, None)
            self.yaml_worlds[key] = core
        return self.yaml_worlds[key]

    def emit(self, message: dict) -> None:
        if message.get("type") == "slot":
            self.last[message["id"]] = message
        elif message.get("type") == "status" and message["id"] in self.last:
            if message["status"] == "tracking":
                return  # the next snapshot carries it
            self.last[message["id"]] = {**self.last[message["id"]], **message, "type": "slot"}
        data = json.dumps(message, separators=(",", ":"))
        for ws in list(self.clients):
            self.spawn(self._send(ws, data), "send")

    async def _send(self, ws, data: str):
        try:
            await ws.send(data)
        except Exception:
            self.clients.discard(ws)

    def send_status(self, ctx: SlotContext) -> None:
        self.emit(ctx.status_payload())

    def send_snapshot(self, sid: str, payload: dict) -> None:
        if sid in self.slots:
            self.emit(payload)

    # ---- message feed (like the Text Client, one copy per room)

    def on_feed(self, ctx: "SlotContext", args: dict) -> None:
        kind = args.get("type", "")
        if kind == "Tutorial":
            return
        room = ctx.room
        now = time.monotonic()
        key = json.dumps(args, sort_keys=True, default=str)
        recent = self.feed_recent.setdefault(room, {})
        for k in [k for k, (t, _) in recent.items() if now - t > 8]:
            del recent[k]
        # Every tracked slot in a room receives room-wide messages: keep the first copy only.
        # The same slot delivering the same message again is a genuine repeat.
        seen = recent.get(key)
        if seen and ctx.sid not in seen[1]:
            seen[1].add(ctx.sid)
            return
        recent[key] = (now, {ctx.sid})

        own_slots = frozenset(c.slot for c in self.slots.values() if c.room == room and c.slot is not None)
        data = args.get("data") or []
        text = "".join(str(p.get("text", "")) for p in data)
        own_names = {c.slot_name for c in self.slots.values() if c.room == room}
        is_ours = args.get("slot") in own_slots or ctx.player_names.get(args.get("slot")) in own_names
        if kind in ("Join", "Part") and is_ours \
                and ("Tracker" in (args.get("tags") or []) or "stopped tracking" in text):
            return  # our own tracker connections coming and going

        players = {int(p["text"]) for p in data if p.get("type") == "player_id" and str(p.get("text", "")).isdigit()}
        for field in ("receiving", "slot"):
            if isinstance(args.get(field), int):
                players.add(args[field])
        if isinstance(args.get("item"), (list, tuple)) and len(args["item"]) > 2:
            players.add(int(args["item"][2]))
        elif isinstance(args.get("item"), dict) and "player" in args["item"]:
            players.add(int(args["item"]["player"]))
        message = {"type": "feed", "room": room, "kind": kind or "Text", "time": time.time(),
                   "segments": ctx._render(data, own_slots), "players": sorted(players)}
        self.feed.setdefault(room, deque(maxlen=400)).append(message)
        self.emit(message)

    def hello(self) -> dict:
        return {"type": "hello", "engine": ENGINE_VERSION, "ut": ut_version(), "ap": Utils.__version__}

    # ---- websocket

    async def handle(self, ws, *_):
        try:
            first = json.loads(await asyncio.wait_for(ws.recv(), 10))
            if first.get("cmd") != "hello" or (self.token and first.get("token") != self.token):
                await ws.close(4001, "unauthorized")
                return
        except Exception:
            return
        self.clients.add(ws)
        await ws.send(json.dumps(self.hello()))
        for ctx in self.slots.values():
            await ws.send(json.dumps(self.last.get(ctx.sid) or ctx.status_payload()))
        for room, messages in self.feed.items():
            await ws.send(json.dumps({"type": "feed_backlog", "room": room, "messages": list(messages)}))
        try:
            async for raw in ws:
                msg = None
                try:
                    msg = json.loads(raw)
                    await self.command(ws, msg)
                except Exception as e:
                    log.error(traceback.format_exc())
                    reply = {"type": "error", "message": str(e)}
                    if isinstance(msg, dict) and msg.get("req") is not None:
                        reply["req"] = msg["req"]
                    await ws.send(json.dumps(reply))
        except websockets.ConnectionClosed:
            pass
        finally:
            self.clients.discard(ws)

    async def command(self, ws, msg: dict) -> None:
        cmd = msg.get("cmd")
        if cmd == "track":
            await self.track(msg["id"], msg["server"], msg["slot"], msg.get("password"), msg.get("yaml_folder", ""),
                             msg.get("room", ""))
        elif cmd == "untrack":
            await self.untrack(msg["id"])
        elif cmd == "ignore":
            ctx = self.slots.get(msg["id"])
            if ctx:
                await ctx.set_ignored([int(x) for x in msg.get("locations", [])], bool(msg.get("ignored", True)))
        elif cmd == "refresh":
            await self.refresh(msg.get("id"))
        elif cmd == "probe":
            players = await self.probe(msg["server"], msg["slot"], msg.get("password"))
            reply = {"type": "probe", "req": msg.get("req")}
            reply.update({"players": players} if isinstance(players, list) else {"error": players})
            await ws.send(json.dumps(reply))
        elif cmd == "explain":
            ctx = self.slots.get(msg["id"])
            lines = await ctx.explain(str(msg.get("query", "")), msg.get("mode", "explain")) if ctx                 else [[{"text": "That game isn't being tracked.", "color": "red"}]]
            await ws.send(json.dumps({"type": "explain", "req": msg.get("req"), "lines": lines}))
        elif cmd == "say":
            ctx = self.slots.get(msg["id"])
            error = None
            try:
                if ctx is None:
                    raise EngineError("That slot isn't being tracked.")
                await ctx.say(str(msg.get("text", "")))
            except EngineError as e:
                error = str(e)
            await ws.send(json.dumps({"type": "said", "req": msg.get("req"), "error": error}))
        elif cmd == "manual":
            ctx = self.slots.get(msg["id"])
            error = None
            try:
                if ctx is None:
                    raise EngineError("That game isn't being tracked.")
                await ctx.set_manual([str(x) for x in msg.get("items", [])])
            except EngineError as e:
                error = str(e)
            await ws.send(json.dumps({"type": "manual", "req": msg.get("req"), "error": error}))
        elif cmd == "hint_items":
            ctx = self.slots.get(msg["id"])
            try:
                if ctx is None:
                    raise EngineError("That game isn't being tracked.")
                reply = {"items": await self.run_logic(ctx.hint_items)}
            except EngineError as e:
                reply = {"items": [], "error": str(e)}
            await ws.send(json.dumps({"type": "hint_items", "req": msg.get("req"), **reply}))
        elif cmd == "buy_hint":
            ctx = self.slots.get(msg["id"])
            try:
                if ctx is None:
                    raise EngineError("That game isn't being tracked.")
                reply = {"lines": await ctx.buy_hint(str(msg.get("kind", "item")), str(msg.get("name", "")))}
            except EngineError as e:
                reply = {"lines": [], "error": str(e)}
            await ws.send(json.dumps({"type": "buy_hint", "req": msg.get("req"), **reply}))
        elif cmd == "item_names":
            ctx = self.slots.get(msg["id"])
            try:
                if ctx is None:
                    raise EngineError("That game isn't being tracked.")
                reply = {"names": ctx.game_item_names()}
            except EngineError as e:
                reply = {"names": [], "error": str(e)}
            await ws.send(json.dumps({"type": "item_names", "req": msg.get("req"), **reply}))
        elif cmd == "clear_feed":
            self.feed.pop(msg.get("room", ""), None)
        elif cmd == "ping":
            await ws.send(json.dumps({"type": "pong", "req": msg.get("req")}))
        elif cmd == "shutdown":
            self.stop_event.set()

    async def track(self, sid: str, server: str, slot: str, password: Optional[str], yaml_folder: str,
                    room: str = ""):
        old = self.slots.get(sid)
        if old and old.requested_server == server and old.slot_name == slot and old.password == (password or None) \
                and old.yaml_folder == (yaml_folder or "") and old.room == (room or server) and old.status != "error":
            return  # already tracking exactly this
        if old:
            await self.untrack(sid)
        try:
            _ut()
        except EngineError as e:
            self.emit({"type": "status", "id": sid, "status": "error", "message": str(e), "game": "",
                       "slot_name": slot})
            return
        log.info(f"Tracking {slot} on {server}")
        ctx = SlotContext(self, sid, server, slot, password, yaml_folder, room)
        self.slots[sid] = ctx
        self.send_status(ctx)
        ctx.server_task = asyncio.create_task(server_loop(ctx), name=f"server loop {sid}")

    async def untrack(self, sid: str):
        ctx = self.slots.pop(sid, None)
        self.last.pop(sid, None)
        if ctx:
            log.info(f"Stopped tracking {ctx.slot_name}")
            await ctx.stop()
            self.emit({"type": "removed", "id": sid})

    async def refresh(self, sid: Optional[str]):
        """Re-read YAMLs and rebuild logic (for one slot, or all of them)."""
        targets = [self.slots[sid]] if sid in self.slots else list(self.slots.values()) if not sid else []
        for ctx in targets:
            key = os.path.normcase(os.path.abspath(ctx.yaml_folder)) if ctx.yaml_folder else ""
            self.yaml_worlds.pop(key, None)
        for ctx in targets:
            if ctx.slot is not None and ctx.server and not ctx.server.socket.closed:
                self.spawn(ctx._initialize(rebuild=True), f"init {ctx.sid}")
            else:
                await self.untrack(ctx.sid)
                await self.track(ctx.sid, ctx.requested_server, ctx.slot_name, ctx.password, ctx.yaml_folder, ctx.room)

    async def probe(self, server: str, slot: str, password: Optional[str]):
        ctx = ProbeContext(server, slot, password)
        ctx.server_task = asyncio.create_task(server_loop(ctx), name="probe")
        try:
            return await asyncio.wait_for(asyncio.shield(ctx.result), 20)
        except asyncio.TimeoutError:
            return "Timed out connecting to the server."
        finally:
            try:
                await ctx.shutdown()
            except Exception:
                pass

    # ---- lifetime

    async def watch_parent(self):
        if not self.parent_pid:
            return
        while not self.stop_event.is_set():
            await asyncio.sleep(2)
            if not pid_alive(self.parent_pid):
                log.info("App closed; stopping engine")
                self.stop_event.set()

    async def run(self):
        async with websockets.serve(self.handle, "127.0.0.1", self.port, max_size=None,
                                    ping_interval=None, ping_timeout=None):
            log.info(f"APMultiTracker Engine {ENGINE_VERSION} listening on 127.0.0.1:{self.port} "
                     f"(UT {ut_version() or 'not installed'}, AP {Utils.__version__})")
            self.spawn(self.watch_parent(), "parent watch")
            await self.stop_event.wait()
        for sid in list(self.slots):
            try:
                await self.untrack(sid)
            except Exception:
                pass
        self.executor.shutdown(wait=False, cancel_futures=True)


def pid_alive(pid: int) -> bool:
    if sys.platform == "win32":
        import ctypes
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
        if not handle:
            return False
        try:
            return kernel32.WaitForSingleObject(handle, 0) == 0x102  # WAIT_TIMEOUT: still running
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def main(*args: str) -> None:
    parser = argparse.ArgumentParser(description="APMultiTracker Engine")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--token", default="")
    parser.add_argument("--parent", type=int, default=0, help="Exit when this process exits.")
    opts, _ = parser.parse_known_args(args)
    Utils.init_logging("APMultiTrackerEngine")
    import settings
    settings.no_gui = True  # headless: a missing ROM/folder setting must fail, not open a hidden file dialog
    engine = Engine(opts.port, opts.token, opts.parent)
    try:
        asyncio.run(engine.run())
    except OSError as e:
        log.error(f"Couldn't listen on port {opts.port}: {e}")
