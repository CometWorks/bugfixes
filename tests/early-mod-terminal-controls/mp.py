"""Multiplayer regression rig of SE-1934: early mod terminal controls on Magnetar and a joining client.

    python3 mp.py control|fix

One Magnetar server and one headless client that joins it over DirectTransport, both loading the
probe in probe/. On each, right after MySession.LoadDataComponents and before any block exists,
the probe reads the IMyTerminalBlock controls and the IMyTimerBlock actions and adds a ProbeButton
control to IMyTimerBlock. The world is the game's Empty World with a static station holding a
timer block. The server reports the timer's controls once its world is ready. The client reports
once the station has streamed in, exits to the main menu, rejoins and reports again.

control runs both without Bugfixes, fix with this working copy of Bugfixes on both. fix must show
the vanilla timer controls (OnOff, Name, CustomData, OpenToolbar, TriggerNow) once each, ProbeButton
once and the timer's actions on the server and on both joins, and Bugfixes must stop locking the
terminal control factory once each world is ready. control must show them missing.

Prints the probe reports and a RESULT line, exits 0 on pass. Builds on tests/thrust-flames/rig.py
for the server and client setup; its environment variables apply here too (RIG_TASK defaults to
SE-1934-mp). It needs httpx for the Remote API client of that rig.
"""

from __future__ import annotations

import importlib.util
import os
import re
import shutil
import sys
import time
from pathlib import Path

os.environ.setdefault("RIG_TASK", "SE-1934-mp")
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location(
    "flames", HERE.parent / "thrust-flames/rig.py"
)
flames = importlib.util.module_from_spec(spec)
spec.loader.exec_module(flames)
spec = importlib.util.spec_from_file_location("offline", HERE / "rig.py")
offline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(offline)

PROBE = HERE / "probe"
PROBE_LOG = "early-controls-probe.log"
UNLOCKED = "stopped locking the terminal control factory"
flames.ship = offline.station
checks = flames.checks
check = flames.check


class Server(flames.Server):
    def __init__(self, slot, admins, bugfixes):
        super().__init__(slot, admins)
        plugins = {
            "direct-transport": (
                flames.DIRECT_TRANSPORT_REPO,
                "DirectTransportServer.xml",
            ),
            "early-controls-probe": (PROBE, "EarlyControlsProbe.xml"),
        }
        if bugfixes:
            plugins["bugfixes"] = (flames.REPO, "BugfixesServer.xml")
        for core in flames.CORE_PLUGINS:
            plugins[core.name] = (core, "ServerPlugin/ServerPlugin.xml")
        flames.write_sources(self.config / "Sources/sources.xml", plugins)
        (self.config / "Profiles/Current.xml").write_text(
            flames.profile_xml(plugins), encoding="utf-8"
        )


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
            "early-controls-probe": (PROBE, "EarlyControlsProbe.xml"),
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


def reports(log: str) -> list[dict[str, str]]:
    return [
        dict(re.findall(r"(\w+)=(\S+)", line))
        for line in log.splitlines()
        if " REPORT " in line
    ]


def fixed(report: dict[str, str]) -> bool:
    ids = dict(item.split(":") for item in report["ids"].split(","))
    return (
        all(ids.get(i) == "1" for i in offline.VANILLA + ("ProbeButton",))
        and int(report["timer_actions"]) > 0
        and report["timer_TriggerDelay_property"] == "True"
    )


def scenario(slot: dict, variant: str) -> None:
    fix = variant == "fix"
    client = Client(slot, fix)
    server = Server(slot, (client.steam_id,), fix)
    server_probe = server.data / PROBE_LOG
    client_probe = client.appdata / PROBE_LOG
    server.start()
    server.wait()
    flames.until(
        "the server's probe report",
        lambda: reports(flames.text(server_probe)),
        300,
        lambda: server.process.poll() is None,
    )
    client.start(server.port)
    # The station streams to the client once it has a character near it: respawn and move the
    # character there on each join.
    placed = 0
    deadline = time.monotonic() + 1800
    while "DONE" not in flames.text(client_probe):
        if not client.alive() or time.monotonic() > deadline:
            raise RuntimeError("the client's probe did not finish")
        joins = flames.text(client_probe).count("phase World")
        if joins > placed:
            try:
                if client.character():
                    client.api.character_teleport(0.0, 10.0, -20.0)
                    placed = joins
            except (
                Exception
            ):  # noqa: BLE001 -- the API is slow or down while a world loads
                pass
        time.sleep(2)

    server_log, client_log = flames.text(server_probe), flames.text(client_probe)
    for name, log in (("server", server_log), ("client", client_log)):
        for line in log.splitlines():
            print(f"{name} probe: {line}", flush=True)
    server_reports, client_reports = reports(server_log), reports(client_log)
    early = (
        server_log.count("early access in LoadData") == 1
        and client_log.count("early access in LoadData") == 2
        and "timers_in_world=0" in server_log
    )
    check(
        "early access in LoadData before any block, once on the server, on both joins on the client",
        early and "timers_in_world=1" not in server_log + client_log,
        "",
    )
    check(
        "the server reported once, the client on both joins",
        len(server_reports) == 1
        and len(client_reports) == 2
        and "DONE both loads reported" in client_log,
        f"server={len(server_reports)} client={len(client_reports)}",
    )
    results = [fixed(r) for r in server_reports + client_reports]
    if fix:
        check("timer controls and actions complete everywhere", all(results), results)
        unlocked = (
            flames.text(server.log).count(UNLOCKED),
            flames.text(client.appdata / "SpaceEngineers.log").count(UNLOCKED),
        )
        check(
            "factory lock removed once the world was ready (server once, client twice)",
            unlocked == (1, 2),
            unlocked,
        )
    else:
        check("timer controls missing everywhere (the bug)", not any(results), results)


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
        flames.run(flames.PARALLEL / "slot.sh", "release", slot["SLOT"])
        logs = flames.ROOT / f"logs-{variant}"
        shutil.rmtree(logs, ignore_errors=True)
        logs.mkdir()
        for log in (
            flames.ROOT / "ds0/server.log",
            flames.ROOT / f"ds0/data/{PROBE_LOG}",
            flames.ROOT / "Joiner.log",
            flames.ROOT / "c0-data/SpaceEngineers.log",
            flames.ROOT / f"c0-data/{PROBE_LOG}",
        ):
            if log.exists():
                shutil.copy(log, logs / f"{log.parent.name}-{log.name}")
    passed = error is None and bool(checks) and all(checks)
    print(
        f"RESULT variant={variant} slot={slot['SLOT']} checks={len(checks)} "
        f"failed={checks.count(False)} passed={passed} logs={logs}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
