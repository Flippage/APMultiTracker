"""
The engine: one process, many tracked slots.

* Each tracked slot is a headless Archipelago client (tag "Tracker", so it never sends checks) with its own
  Universal Tracker ``TrackerCore``.
* All logic work (generation and logic sweeps) runs on a single worker thread, so the event loop stays free
  for network traffic and slots never touch a multiworld at the same time.
* The desktop app talks to us over a WebSocket on 127.0.0.1 with small JSON messages (see ``Engine.handle``).
"""
from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import sys
import traceback
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable, Optional

import websockets

import Utils
from CommonClient import CommonContext, server_loop
from worlds import AutoWorld

from . import ENGINE_VERSION

DEFAULT_PORT = 38291
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
                 yaml_folder: str):
        super().__init__(server, password or None)
        self.engine = engine
        self.sid = sid
        self.auth = slot_name
        self.slot_name = slot_name  # CommonContext clears .auth on disconnect
        self.requested_server = server
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
        self.updater_task = asyncio.create_task(self._updater(), name=f"updater {sid}")

    # ---- connection

    async def server_auth(self, password_requested: bool = False):
        if password_requested and not self.password:
            self.set_status("error", "This room needs a password.")
            await self.disconnect(False)
            return
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
        if "archipelago.gg" in self.requested_server.lower() and not self.logic_ready:
            # Hosted rooms sleep after a while idle and can come back on a different port.
            msg += (" If the room has been idle, open its page on archipelago.gg to wake it up, "
                    "and check the port hasn't changed.")
        if self.username and not self.disconnected_intentionally:
            self.set_status("reconnecting", msg + " Reconnecting…")
        else:
            self.set_status("error", msg)

    async def connection_closed(self):
        self.logic_ready = False
        await super().connection_closed()
        if self.status not in ("error", "stopped") and not self.disconnected_intentionally:
            self.set_status("reconnecting", "Lost connection to the server. Reconnecting…")

    def on_print_json(self, args: dict):
        pass  # chat / item messages are not shown; avoids N copies of every message in the log

    def on_package(self, cmd: str, args: dict):
        try:
            if cmd == "RoomInfo":
                self.room_seed = args.get("seed_name", "")
            elif cmd == "Connected":
                self.slot_data = args.get("slot_data") or {}
                self.engine.spawn(self._initialize(), f"init {self.sid}")
            elif cmd in ("RoomUpdate", "ReceivedItems", "LocationInfo"):
                self.watcher_event.set()
            elif cmd == "Retrieved":
                keys = args.get("keys", {})
                if self._hint_key in keys:
                    self.watcher_event.set()
                self._deferred_changed([k for k in self.deferred_keys if k in keys])
            elif cmd == "SetReply":
                key = args.get("key", "")
                if key == self._hint_key:
                    self.watcher_event.set()
                self._deferred_changed([key] if key in self.deferred_keys else [])
        except Exception as e:
            log.exception(e)
            self.set_status("error", f"Tracker error: {e}")

    @property
    def _hint_key(self) -> str:
        return f"_read_hints_{self.team}_{self.slot}"

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
        }
        return inputs

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

        errors = [line.location_label for group, lines in core.log_lines.items() if group < 0 for line in lines]
        items = [{"name": name, "count": count, "prog": name in ret.prog_items}
                 for name, count in ret.all_items.items()]
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
            "locations": locations,
            "items": items,
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
            shim.messages.append([{"type": "color", "color": "red", "text": f"Couldn't explain that: {e}"}])
        finally:
            client_log.removeHandler(capture)
        lines = [self._render(parts) for parts in shim.messages if parts]
        return lines or [[{"text": "Nothing to explain.", "color": ""}]]

    def _render(self, parts) -> list[dict]:
        """AP JSON message parts → plain segments the app can colour (names resolved here)."""
        if isinstance(parts, dict):
            parts = [parts]
        out = []
        for p in parts:
            kind = p.get("type", "text")
            text = str(p.get("text", ""))
            color = p.get("color", "") if kind == "color" else ""
            try:
                if kind == "player_id":
                    text, color = self.player_names.get(int(text), text), "player"
                elif kind == "item_id":
                    text, color = self.item_names.lookup_in_slot(int(text), p.get("player", self.slot)), "item"
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
        Utils.persistent_store(PERSIST_KEY, self.persist_id, {"ignored": sorted(self.core.ignored_locations)})
        self.watcher_event.set()

    async def stop(self):
        self.set_status("stopped", "")
        self.updater_task.cancel()
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
        try:
            async for raw in ws:
                try:
                    await self.command(ws, json.loads(raw))
                except Exception as e:
                    log.error(traceback.format_exc())
                    await ws.send(json.dumps({"type": "error", "message": str(e)}))
        except websockets.ConnectionClosed:
            pass
        finally:
            self.clients.discard(ws)

    async def command(self, ws, msg: dict) -> None:
        cmd = msg.get("cmd")
        if cmd == "track":
            await self.track(msg["id"], msg["server"], msg["slot"], msg.get("password"), msg.get("yaml_folder", ""))
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
        elif cmd == "ping":
            await ws.send(json.dumps({"type": "pong", "req": msg.get("req")}))
        elif cmd == "shutdown":
            self.stop_event.set()

    async def track(self, sid: str, server: str, slot: str, password: Optional[str], yaml_folder: str):
        old = self.slots.get(sid)
        if old and (old.server_address or "").endswith(server) and old.auth == slot \
                and old.yaml_folder == (yaml_folder or "") and old.status != "error":
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
        ctx = SlotContext(self, sid, server, slot, password, yaml_folder)
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
                await self.track(ctx.sid, ctx.requested_server, ctx.slot_name, ctx.password, ctx.yaml_folder)

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
