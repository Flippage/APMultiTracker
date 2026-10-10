"""
APMultiTracker Engine.

A headless companion to the APMultiTracker desktop app. It runs inside Archipelago (so every installed
apworld is available) and uses Universal Tracker's logic engine to track many slots at once, streaming
the results to the app over a local WebSocket.

Requires Universal Tracker (tracker.apworld) to be installed alongside it.
"""
from typing import ClassVar

from settings import Group
from worlds.AutoWorld import World
from worlds.LauncherComponents import Component, Type, components, icon_paths

ENGINE_VERSION = "0.3.0"
COMPONENT_NAME = "APMultiTracker Engine"


def launch_engine(*args: str) -> None:
    from worlds.LauncherComponents import launch
    from .engine import main
    launch(main, name=COMPONENT_NAME, args=args)


class EngineSettings(Group):
    pass


class EngineWorld(World):
    """Never generated: only exists so Archipelago registers this apworld."""
    settings: ClassVar[EngineSettings]
    settings_key = "apmultitracker_engine"
    game = COMPONENT_NAME
    hidden = True
    item_name_to_id = {}
    location_name_to_id = {}


icon_paths["apmultitracker_ico"] = f"ap:{__name__}/icon.png"
components.append(Component(COMPONENT_NAME, None, func=launch_engine, component_type=Type.CLIENT,
                            icon="apmultitracker_ico",
                            description="Background engine for the APMultiTracker desktop app."))
