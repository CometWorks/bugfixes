"""Rotor reattach thread rig (Docs/mechanical-reattach-thread.md).

    python3 rig.py control|fix

One Magnetar server with 16 rotors near the spawn point, each a static grid with a stator and a
dynamic grid with its rotor head, and one headless client that joins it. The client loads the
probe in probe/, which counts the calls that change a grid's BlocksWaitingOnAttachUpdate set
from any thread but the update thread.

1. join     the client joins and the server streams the rotor grids to it. The client initializes
            them on its entity creation thread, and a head whose stator is already in the world
            marks the stator for reattach from that thread.
2. stream   for 20 s the probe hands copies of the heads' grids to the entity creation thread, the
            same way, while the game reattaches the rotors on the update thread.
3. hammer   for 20 s a worker thread marks the rotors for reattach in a tight loop.

control runs the client without Bugfixes, fix with this working copy as a dev folder.
control must show calls from the entity creation thread after the join; whether the set gets
corrupted (or the client crashes) depends on timing, so it is printed, not checked. fix must show
no call off the update thread, keep the client alive, keep every head attached and the sets
consistent.

Prints one line per check and a RESULT line, exits 0 when every check passed.

Builds on tests/thrust-flames/rig.py for the server and client setup; its environment variables
apply here too (RIG_TASK defaults to SE-1822).
"""

from __future__ import annotations

import importlib.util
import os
import re
import shutil
import sys
import time
from pathlib import Path

os.environ.setdefault("RIG_TASK", "SE-1822")
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location(
    "flames", HERE.parent / "thrust-flames/rig.py"
)
flames = importlib.util.module_from_spec(spec)
spec.loader.exec_module(flames)

ROTORS = 16
PROBE = HERE / "probe"
CRASH = re.compile(r"AddIfNotPresent|HashSet`1\.(Add|Remove|Resize)")


def rotor_pair(i: int) -> str:
    x, y, z = 30.0 + 10 * (i % 4), 10.0 * (i // 4), -40.0
    base_id, stator_id = 144000000000182200 + 10 * i, 144000000000182201 + 10 * i
    head_grid_id, head_id = 144000000000182202 + 10 * i, 144000000000182203 + 10 * i

    def grid(entity_id, pos, static, block):
        return (
            '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
            f"<SubtypeName /><EntityId>{entity_id}</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>"
            f'<PositionAndOrientation><Position x="{pos[0]}" y="{pos[1]}" z="{pos[2]}" />'
            '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
            f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{block}</CubeBlocks>"
            f"<IsStatic>{str(static).lower()}</IsStatic><DisplayName>Rotor{i}</DisplayName>"
            "<DestructibleBlocks>true</DestructibleBlocks></MyObjectBuilder_EntityBase>"
        )

    stator = (
        '<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_MotorStator"><SubtypeName>LargeStator</SubtypeName>'
        f'<EntityId>{stator_id}</EntityId><Min x="0" y="0" z="0" /><BlockOrientation Forward="Forward" Up="Up" />'
        f"<TopBlockId>{head_id}</TopBlockId></MyObjectBuilder_CubeBlock>"
    )
    head = (
        '<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_MotorRotor"><SubtypeName>LargeRotor</SubtypeName>'
        f'<EntityId>{head_id}</EntityId><Min x="0" y="0" z="0" /><BlockOrientation Forward="Forward" Up="Up" />'
        f"<ParentEntityId>{stator_id}</ParentEntityId></MyObjectBuilder_CubeBlock>"
    )
    return grid(base_id, (x, y, z), True, stator) + grid(
        head_grid_id, (x, y + 2.5, z), False, head
    )


flames.ship = lambda: "".join(rotor_pair(i) for i in range(ROTORS))
checks = flames.checks
check = flames.check


class Client(flames.Client):
    def __init__(self, slot, bugfixes):
        super().__init__(slot, 0, "Joiner", bugfixes)
        loader = self.pulsar / "Interim"
        plugins = {
            "remote": (flames.REMOTE_REPO, "Remote.xml"),
            "direct-transport": (
                flames.DIRECT_TRANSPORT_REPO,
                "DirectTransportClient.xml",
            ),
            "reattach-probe": (PROBE, "ReattachProbe.xml"),
        }
        if bugfixes:
            plugins["bugfixes"] = (flames.REPO, "BugfixesClient.xml")
        for core in flames.CORE_PLUGINS:
            plugins[core.name] = (core, "ClientPlugin/ClientPlugin.xml")
        flames.write_sources(loader / "Sources/sources.xml", plugins)
        (loader / "Profiles/Current.xml").write_text(
            flames.profile_xml(plugins), encoding="utf-8"
        )
        # A server with plugins runs in experimental mode and turns away a client that does not.
        cfg = self.appdata / "SpaceEngineers.cfg"
        cfg.write_text(
            re.sub(
                r"(<Key>ExperimentalMode</Key>\s*<Value[^>]*>\s*<Value[^>]*>)\w+",
                r"\g<1>True",
                cfg.read_text(encoding="utf-8"),
            ),
            encoding="utf-8",
        )

    def probe(self, request: str, timeout: float) -> dict[str, str] | None:
        """The probe's report line for request as key=value pairs, None if the client died."""
        done = self.appdata / "reattach-probe-done.txt"
        before = len(flames.text(done).splitlines())
        (self.appdata / "reattach-probe.txt").write_text(request + "\n")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not self.alive():
                return None
            lines = flames.text(done).splitlines()
            if len(lines) > before:
                line = lines[before]
                print(f"probe {request}: {line}", flush=True)
                return dict(re.findall(r"(\w+)=(\[[^\]]*\]|\S+)", line))
            time.sleep(1)
        raise TimeoutError(f"probe {request}")


def scenario(slot: dict, variant: str) -> None:
    fix = variant == "fix"
    client = Client(slot, fix)
    server = flames.Server(slot, (client.steam_id,))
    server.start()
    server.wait()
    client.start(server.port)
    client.join()
    client.api.character_teleport(40.0, 10.0, -20.0)
    time.sleep(15)  # the rotor grids stream in and attach

    report = client.probe("report", 120)
    if report is None:
        died = CRASH.search(flames.text(client.appdata / "SpaceEngineers.log"))
        check("join: client alive", False, died.group(0) if died else "died")
        return
    calls = int(report["off_thread_calls"])
    if fix:
        check(
            "join: no change of the attach set off the update thread",
            calls == 0,
            report["off_threads"],
        )
        check(
            "join: attach sets consistent",
            report["set_consistent"] == "True",
            report["corrupt"],
        )
    else:
        check(
            "join: the entity creation thread changes the attach set (vanilla race)",
            "Entity creation thread" in report["off_threads"],
            report["off_threads"],
        )
        print(
            f"join: attach sets consistent: {report['set_consistent']} {report['corrupt']}",
            flush=True,
        )
    check(
        f"join: all {ROTORS} heads attached",
        report["attached"] == f"{ROTORS}/{ROTORS}",
        report["attached"],
    )

    for request in ("stream 20", "hammer 20"):
        report = client.probe(request, 180)
        name = request.split()[0]
        if report is None:
            died = CRASH.search(flames.text(client.appdata / "SpaceEngineers.log"))
            check(
                f"{name}: client {'crashed in HashSet' if died else 'died'}",
                not fix and bool(died),
                died.group(0) if died else "no HashSet frame in the log",
            )
            return
        broken = int(report["off_thread_calls"]) > 0 and (
            report["set_consistent"] != "True" or report["worker_error"] != "none"
        )
        if fix:
            check(
                f"{name}: no change of the attach set off the update thread",
                report["off_thread_calls"] == "0",
                report["off_threads"],
            )
            check(
                f"{name}: set consistent",
                report["set_consistent"] == "True",
                report["waiting"],
            )
            check(
                f"{name}: all {ROTORS} heads attached",
                report["attached"] == f"{ROTORS}/{ROTORS}",
                report["attached"],
            )
            check(
                f"{name}: worker without error",
                report["worker_error"] == "none",
                report["worker_error"],
            )
        else:
            print(
                f"{name}: off-thread calls {report['off_thread_calls']}, set broken: {broken}",
                flush=True,
            )

    if not fix:
        # The vanilla client survived both races; that alone is not a failure of the rig, the
        # join check above already showed the race. Record it.
        print("control: the client survived stream and hammer this time", flush=True)
    check("client alive at the end", client.alive() or not fix, "")


def main() -> int:
    variant = sys.argv[1] if len(sys.argv) > 1 else ""
    assert variant in ("control", "fix"), __doc__
    flames.ROOT.mkdir(parents=True, exist_ok=True)
    slot = flames.claim_slot()
    error = None
    try:
        scenario(slot, variant)
    except Exception as e:  # noqa: BLE001 -- reported in the RESULT line
        error = f"{type(e).__name__}: {e}"
        print(f"ERROR {error}", flush=True)
    finally:
        flames.stop_all()
        logs = flames.ROOT / f"logs-{variant}"
        shutil.rmtree(logs, ignore_errors=True)
        logs.mkdir()
        for log in (
            flames.ROOT / "ds0/server.log",
            flames.ROOT / "Joiner.log",
            flames.ROOT / "c0-data/SpaceEngineers.log",
            flames.ROOT / "c0-data/reattach-probe-done.txt",
        ):
            if log.exists():
                shutil.copy(log, logs / f"{log.parent.name}-{log.name}")
    passed = error is None and bool(checks) and all(checks)
    print(
        f"RESULT variant={variant} slot={slot['SLOT']} checks={len(checks)} "
        f"failed={checks.count(False)} passed={passed}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
