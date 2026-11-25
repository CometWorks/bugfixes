"""Exercise a suit impact from rest with the dedicated server fix disabled or enabled."""

import argparse
import math
import os
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
os.environ.setdefault("RIG_TASK", "character-grid-push")
sys.path.insert(0, str(HERE.parent / "merge-block-physics"))
import rig


def cube() -> str:
    blocks = rig.block("CubeBlock", "LargeBlockArmorBlock", (0, 0, 0))
    blocks += rig.block("TerminalBlock", "ControlPanel", (0, 1, 0))
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        f"<EntityId>{rig.GRID_ID}</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0"/>'
        '<Forward x="0" y="0" z="-1"/><Up x="0" y="1" z="0"/></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        "<IsStatic>false</IsStatic><DisplayName>FROZEN CUBE</DisplayName></MyObjectBuilder_EntityBase>"
    )


def state(path: Path) -> tuple[bool, list[float]]:
    awake, *xyz = path.read_text().split()
    return awake == "True", list(map(float, xyz))


def push(client: rig.Client, path: Path) -> float:
    api = client.api
    forward = api.get_character()["forward"]
    _, origin = state(path)
    start = [x - 8 * d for x, d in zip(origin, forward)]
    start[1] -= 0.8  # aim the suit's torso at the armor block
    api.character_teleport(*start)
    client.until(
        "suit at run-up",
        lambda: math.dist(api.get_character()["position"], start) < 1,
        10,
    )
    path.with_suffix(".stop").touch()
    client.until(
        "server grid at rest and asleep",
        lambda: not path.with_suffix(".stop").exists() and not state(path)[0],
        20,
    )
    _, before = state(path)
    client.until("grid replicated to client", client.sees_grid, 15)
    api.key("W", hold_frames=100)
    closest = math.inf
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        closest = min(closest, math.dist(api.get_character()["position"], before))
        time.sleep(0.1)
    if closest > 4:
        raise RuntimeError(
            f"Suit missed the cube. Closest approach {closest:.2f} m, start {start}, grid {before}, final character {api.get_character()}"
        )
    _, after = state(path)
    print(
        f"Server grid moved from {before} to {after}; closest suit approach {closest:.2f} m",
        flush=True,
    )
    return sum((end - begin) * d for end, begin, d in zip(after, before, forward))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("variant", choices=["control", "fix"])
    variant = parser.parse_args().variant
    slot = rig.claim_slot()
    try:
        server = rig.Server(
            slot,
            rig.DEDICATED / "DedicatedServer64",
            bugfixes=True,
            admin=slot["CLIENT_ID_BASE"],
            grid_xml=cube(),
            extra_plugins={"push-probe": (HERE / "probe", "Probe.xml")},
        )
        if variant == "control":
            (server.data / "Bugfixes.cfg").write_text(
                "<PluginConfig><CharacterGridPushPatch>false</CharacterGridPushPatch></PluginConfig>"
            )
        server.start()
        if not server.wait(180):
            raise RuntimeError(f"Server exited during startup. See {server.log}")
        client = rig.Client(slot, rig.GAME / "Bin64", False)
        client.start(server.port)
        client.until("world ready", client.ready, 180)
        client.until(
            "suit respawn",
            lambda: client.api.control_click(text="Respawn", screen=2),
            30,
        )
        client.until("character", client.character, 30)
        distances = [push(client, server.data / "push-state") for _ in range(2)]
        print(
            f"{variant}: forward displacement after two pushes from sleep (m): {distances}",
            flush=True,
        )
        return 0 if min(distances) > 0.5 else 1
    finally:
        rig.stop_all()
        rig.run(rig.PARALLEL / "slot.sh", "release", slot["SLOT"])


if __name__ == "__main__":
    sys.exit(main())
