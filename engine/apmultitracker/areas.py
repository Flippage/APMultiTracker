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


ADAPTERS: dict[str, Callable[..., AreaWatcher]] = {
    "Pokemon Emerald": _Emerald,
    "The Minish Cap": _MinishCap,
    "Paper Mario: The Thousand-Year Door": _TTYD,
    "Twilight Princess": _TwilightPrincess,
    "CrossCode": _CrossCode,
}


def make(game: str, world_cls, core, team: int, slot: int) -> Optional[AreaWatcher]:
    """Worker thread. The game's area watcher, or None if it has none (or its apworld isn't laid out as expected)."""
    factory = ADAPTERS.get(game)
    if factory is None or core is None or core.multiworld is None:
        return None
    try:
        return factory(world_cls, core, team, slot)
    except Exception:
        log.warning(f"Current area isn't available for {game} (its apworld isn't laid out as expected)", exc_info=True)
        return None
