"""Check the hand drill on an isolated offline Octahedron seed 2 client.

Use the se-remote skill's Python environment. Pass the test client's Remote URL
and 'before' or 'after'. The world needs Octahedron-Asteroid-0348 at its original
position and the seed 2 ore definitions. A copy of the asteroid in an otherwise
empty creative world also works.
"""

import argparse
import math
import time

from se_remote import RemoteAPI

POSITION = (-104242.36, -10768.99, 49238.69)
ASTEROID = 498800651204024542


def check(api: RemoteAPI, expect_markers: bool) -> None:
    assert api.get_state()["multiplayer"] == "offline", "Use an isolated offline client"
    assert any(
        asteroid["entityId"] == ASTEROID
        for asteroid in api.get_voxel_maps(*POSITION, radius=40)
    ), "Seed 2 asteroid 0348 missing"

    api.character_teleport(*POSITION)
    api.add_inventory_item("PhysicalGunObject", "HandDrillItem")
    api.character_equip("HandDrillItem")
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        time.sleep(1)
        character = api.get_character()
        signals = api.get_hud_signals()
        grid_detectors = {d["entityId"] for d in signals["oreDetectors"]}
        deposits = [
            d
            for d in signals["oreDeposits"]
            if d["voxelEntityId"] == ASTEROID and d["detectorId"] not in grid_detectors
        ]
        if deposits:
            break

    assert (character["weapon"] or {}).get("type") == "MyHandDrill", character
    assert math.dist(character["position"], POSITION) < 4, character
    print(f"{len(deposits)} deposits", [d["materials"] for d in deposits])
    if expect_markers:
        assert any("Iron" in d["materials"] for d in deposits)
    else:
        assert not deposits


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("url")
    parser.add_argument("mode", choices=("before", "after"))
    args = parser.parse_args()
    with RemoteAPI(args.url) as api:
        check(api, args.mode == "after")
