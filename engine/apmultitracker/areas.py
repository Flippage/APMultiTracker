"""Where the player is right now, for games whose client reports it, and which of the game's checks are there.

Archipelago has no common "current area" field: each game's own client reports it its own way (a data storage value,
or a Bounce to the trackers on its slot), and PopTracker packs map it to their tabs with their own tables. Each
adapter here turns one game's report into an area name plus the location ids in that area, using the apworld's own
data where it has it, or a PopTracker pack's tables where its licence allows (see area_crosscode.py).

Adapters run their setup on the logic thread (they read the rebuilt multiworld); updates run on the event loop.
"""
from __future__ import annotations

import collections
import importlib
import logging
from typing import Any, Callable, Optional

log = logging.getLogger("APMultiTracker")


class AreaWatcher:
    """One game's adapter. `keys` are data storage keys to watch; `on_value` / `on_bounce` return True when the
    current area changed."""

    def __init__(self, keys: list[str]):
        self.keys = keys
        self.name = ""                      # "" = unknown (game not running, or not reported yet)
        self.locations: frozenset[int] = frozenset()

    def _set(self, name: str, locations) -> bool:
        locations = frozenset(locations)
        if name == self.name and locations == self.locations:
            return False
        self.name, self.locations = name, locations
        return True

    def on_value(self, key: str, value: Any) -> bool:
        return False

    def on_bounce(self, data: dict) -> bool:
        return False

    def payload(self) -> Optional[dict]:
        if not self.name:
            return None
        return {"name": self.name, "locations": sorted(self.locations)}


def _package(world_cls) -> str:
    return world_cls.__module__.rsplit(".", 1)[0] if "." in world_cls.__module__ else world_cls.__module__


def _sub(world_cls, name: str):
    return importlib.import_module(f"{_package(world_cls)}.{name}")


def _present(core) -> dict[str, int]:
    """This seed's locations for the tracked player: name -> id."""
    return {loc.name: loc.address for loc in core.multiworld.get_locations(core.player_id) if loc.address is not None}


def _common_prefix(names: list[str]) -> str:
    """'Rogueport Docks: X', 'Rogueport Center: Y' -> 'Rogueport'."""
    heads = [n.split(":", 1)[0].split() for n in names if n]
    if not heads:
        return ""
    out = []
    for words in zip(*heads):
        if len(set(words)) != 1:
            break
        out.append(words[0])
    return " ".join(out) or collections.Counter(" ".join(h) for h in heads).most_common(1)[0][0]


# ---------------------------------------------------------------------------------------------- Pokémon Emerald
# The BizHawk client sends {"type": "MapUpdate", "mapId": ...} to its slot's trackers whenever the map changes
# (not stored anywhere, so the area is unknown until the player next changes map). The apworld's data says which
# map each region is on.

class _Emerald(AreaWatcher):
    def __init__(self, world_cls, core, team: int, slot: int):
        super().__init__([])
        data = _sub(world_cls, "data").data
        self.map_name = {v: k for k, v in data.constants.items() if k.startswith("MAP_") and isinstance(v, int)}
        self.label = {name: m.label for name, m in data.maps.items()}
        self.by_map: dict[str, set[int]] = collections.defaultdict(set)
        for loc in core.multiworld.get_locations(core.player_id):
            if loc.address is None or loc.parent_region is None:
                continue
            region = data.regions.get(loc.parent_region.name)
            if region is not None:
                self.by_map[region.parent_map.name].add(loc.address)

    def on_bounce(self, data: dict) -> bool:
        if not isinstance(data, dict) or data.get("type") != "MapUpdate":
            return False
        name = self.map_name.get(data.get("mapId"))
        if name is None:
            return self._set("", ())
        return self._set(self.label.get(name, name), self.by_map.get(name, ()))


# ---------------------------------------------------------------------------------------------- The Minish Cap
# The client stores the room id in tmc_room_<team>_<slot>; every location in the apworld knows its room.

class _MinishCap(AreaWatcher):
    def __init__(self, world_cls, core, team: int, slot: int):
        super().__init__([f"tmc_room_{team}_{slot}"])
        present = set(_present(core).values())
        self.by_room: dict[int, set[int]] = collections.defaultdict(set)
        self.region: dict[int, collections.Counter] = collections.defaultdict(collections.Counter)
        for loc in _sub(world_cls, "locations").all_locations:
            if loc.id in present:
                self.by_room[loc.room_area].add(loc.id)
                self.region[loc.room_area][loc.region] += 1

    def on_value(self, key: str, value: Any) -> bool:
        if not isinstance(value, int) or value == 0:
            return self._set("", ())
        names = self.region.get(value)
        return self._set(names.most_common(1)[0][0] if names else "No checks in this room", self.by_room.get(value, ()))


# ---------------------------------------------------------------------------------------------- Paper Mario: TTYD
# The client stores the room code (e.g. "gor_01") in ttyd_room_<team>_<slot>; every location in the apworld is
# tagged with its area code ("gor" = Rogueport). (The TTYD PopTracker pack has finer tabs, but no licence.)

class _TTYD(AreaWatcher):
    def __init__(self, world_cls, core, team: int, slot: int):
        super().__init__([f"ttyd_room_{team}_{slot}"])
        present = _present(core)
        self.by_area: dict[str, set[int]] = collections.defaultdict(set)
        names: dict[str, list[str]] = collections.defaultdict(list)
        for loc in _sub(world_cls, "Locations").import_locations():
            rel = str(getattr(loc.rel, "value", loc.rel) or "").lower()
            if loc.name in present and rel:
                self.by_area[rel].add(present[loc.name])
                names[rel].append(loc.name)
        self.label = {rel: _common_prefix(n) for rel, n in names.items()}

    def on_value(self, key: str, value: Any) -> bool:
        if not isinstance(value, str) or not value:
            return self._set("", ())
        rel = value.split("_", 1)[0].lower()
        return self._set(self.label.get(rel) or value, self.by_area.get(rel, ()))


# ---------------------------------------------------------------------------------------------- Twilight Princess
# The client stores the zone (e.g. "Faron", "Forest Temple") in TP_<team>_<slot>_Current Region; every location in
# the apworld is tagged with its zone number. (The TP PopTracker pack is GPL, so its tables aren't used here.)

class _TwilightPrincess(AreaWatcher):
    def __init__(self, world_cls, core, team: int, slot: int):
        super().__init__([f"TP_{team}_{slot}_Current Region"])
        present = _present(core)
        self.node_number: dict[str, int] = dict(_sub(world_cls, "ClientUtils").NODE_TO_STRING)
        self.by_node: dict[int, set[int]] = collections.defaultdict(set)
        for name, data in _sub(world_cls, "Locations").LOCATION_TABLE.items():
            node = getattr(data, "region", None)
            if name in present and node is not None:
                self.by_node[int(node)].add(present[name])

    def on_value(self, key: str, value: Any) -> bool:
        if not isinstance(value, str) or value in ("", "Menu") or value not in self.node_number:
            return self._set("", ())
        return self._set(value, self.by_node.get(self.node_number[value], ()))


# ---------------------------------------------------------------------------------------------- CrossCode
# The game mod stores its map name (e.g. "cold-dng.b1.room-3") in CrossCode_<team>_<slot>_mapName. Areas, dungeon
# floors and which checks are in each come from lurch9229's CrossCode PopTracker pack (MIT licence).

class _CrossCode(AreaWatcher):
    def __init__(self, world_cls, core, team: int, slot: int):
        super().__init__([f"CrossCode_{team}_{slot}_mapName"])
        from . import area_crosscode as cc
        self.cc = cc
        present = set(_present(core).values())
        self.paths = {lid: path for lid, path in cc.LOCATIONS.items() if lid in present}

    def on_value(self, key: str, value: Any) -> bool:
        if not isinstance(value, str) or not value or value == "newgame":
            return self._set("", ())
        parts = value.split(".")
        area = self.cc.REGIONS.get(parts[0])
        if area is None:
            return self._set("", ())
        in_area = {lid: path for lid, path in self.paths.items() if path.split("/", 1)[0] == area}
        floor = self.cc.FLOORS.get(".".join(parts[:2])) if len(parts) > 1 else None
        if floor:
            starts = tuple(f"{area} {f} " for f in floor.split("/"))
            on_floor = {lid for lid, path in in_area.items() if (path.split("/", 1) + [""])[1].startswith(starts)}
            if on_floor:
                return self._set(f"{area} {floor}", on_floor)
        return self._set(area, in_area)


# ---------------------------------------------------------------------------------------------- Universal Tracker maps
# Any apworld that ships a map tracker for Universal Tracker with auto-tabbing (a `tracker_world` with
# `map_page_setting_key` / `map_page_index`, e.g. Donkey Kong 64): the watched value picks a map, and the pack's
# locations.json says which checks are on it. Same data UT uses to switch its own map tab.

def _load_pack_json(read: Callable[[str], bytes], path: str):
    import json
    import re
    text = read(path).decode("utf-8-sig")
    try:
        return json.loads(text)
    except ValueError:   # PopTracker allows comments and trailing commas
        text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
        text = re.sub(r"(?m)^\s*//.*$", "", text)
        text = re.sub(r",(\s*[}\]])", r"\1", text)
        return json.loads(text)


class _UTMap(AreaWatcher):
    def __init__(self, world_cls, core, team: int, slot: int):
        tw = getattr(world_cls, "tracker_world", None)
        index = tw.get("map_page_index") if isinstance(tw, dict) else None
        if not callable(index):
            raise LookupError("no UT map auto-tabbing")
        read = self._reader(world_cls, core, tw)
        key = tw.get("map_page_setting_key") or f"{{player}}_{{team}}_UT_MAP"
        super().__init__([key.format(player=slot, team=team)])
        self.index = index

        def files(entry) -> list[str]:
            v = tw.get(entry) or []
            return [v] if isinstance(v, str) else list(v)

        self.maps: list[str] = []
        for f in files("map_page_maps"):
            self.maps += [m.get("name", "") for m in _load_pack_json(read, f)]
        present = _present(core)
        by_id = {v: k for k, v in present.items()}
        mapping = tw.get("poptracker_name_mapping") or {}
        self.by_map: dict[str, set[int]] = collections.defaultdict(set)
        todo = []
        for f in files("map_page_locations"):
            todo += _load_pack_json(read, f)
        while todo:
            node = todo.pop()
            todo += node.get("children", []) or []
            maps = [m.get("map") for m in node.get("map_locations", []) or []]
            if not maps:
                continue
            for section in node.get("sections", []) or []:
                name = section.get("name")
                target = mapping.get(f"{node.get('name', '')}/{name}", name)
                lid = target if isinstance(target, int) and target in by_id else present.get(target)
                if lid is not None:
                    for m in maps:
                        self.by_map[m].add(lid)
        # A map showing (nearly) every check is an overview, not a place
        total = max(1, len(present))
        self.overview = {m for m, ids in self.by_map.items() if len(ids) >= 0.8 * total and total > 20}

    @staticmethod
    def _reader(world_cls, core, tw: dict) -> Callable[[str], bytes]:
        """Reads a file of the pack: inside the apworld (map_page_folder), or the external pack zip the player chose
        for Universal Tracker (external_pack_key in host.yaml). Never asks for one."""
        import os
        import pkgutil
        import zipfile
        external = tw.get("external_pack_key")
        if external:
            ref = core.get_current_world().settings[external]
            ref = str(ref) if ref else ""
            if not ref or not zipfile.is_zipfile(ref):
                raise LookupError(f"no PopTracker pack chosen for Universal Tracker ({external} in host.yaml)")
            prefix = os.path.splitext(os.path.basename(ref))[0] + "/" if tw.get("nested_pack") else ""

            def read_zip(path: str) -> bytes:
                with zipfile.ZipFile(ref) as z:
                    return z.read(prefix + path)
            return read_zip
        folder = tw.get("map_page_folder")
        if not folder:
            raise LookupError("no map pack")

        def read_module(path: str) -> bytes:
            data = pkgutil.get_data(world_cls.__module__, f"/{folder}/{path}")
            if data is None:
                raise FileNotFoundError(path)
            return data
        return read_module

    def on_value(self, key: str, value: Any) -> bool:
        try:
            i = int(self.index(value if value is not None else ""))
        except Exception:
            return self._set("", ())
        if i < 0 or i >= len(self.maps) or not self.maps[i] or self.maps[i] in self.overview:
            return self._set("", ())
        name = self.maps[i]
        label = name.replace("_", " ").replace("-", " ")
        return self._set(label.title() if label.islower() else label, self.by_map.get(name, ()))


ADAPTERS: dict[str, Callable[..., AreaWatcher]] = {
    "Pokemon Emerald": _Emerald,
    "The Minish Cap": _MinishCap,
    "Paper Mario: The Thousand-Year Door": _TTYD,
    "Twilight Princess": _TwilightPrincess,
    "CrossCode": _CrossCode,
}


def make(game: str, world_cls, core, team: int, slot: int) -> Optional[AreaWatcher]:
    """Worker thread. The game's area watcher, or None if it has none (or its apworld isn't laid out as expected)."""
    if core is None or core.multiworld is None:
        return None
    factory = ADAPTERS.get(game)
    if factory is not None:
        try:
            return factory(world_cls, core, team, slot)
        except Exception:
            log.warning(f"Current area isn't available for {game} (its apworld isn't laid out as expected)", exc_info=True)
    # Otherwise (or if that failed): Universal Tracker's map auto-tabbing, if the apworld has it
    tw = getattr(world_cls, "tracker_world", None)
    if isinstance(tw, dict) and callable(tw.get("map_page_index")):
        try:
            return _UTMap(world_cls, core, team, slot)
        except Exception:
            log.warning(f"Current area isn't available for {game} (couldn't read its Universal Tracker map pack)", exc_info=True)
    return None
