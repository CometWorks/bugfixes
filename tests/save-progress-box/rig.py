"""Save progress box rig (Docs/save-progress-box-after-unload.md).

    python3 rig.py control|fix [no|yes]

One Magnetar server and one headless client that joins it. The client loads the probe in probe/.
The rig has the probe exit to the main menu; right after the multiplayer object is disposed,
while the session is still set, the probe raises the window close request, as when the player
clicks the window's X in that moment. That leaves the "Do you want to save your progress?" box
on the main menu with no session. The rig then clicks No (or Yes) on the box through the Remote
plugin, as the player did.

control runs the client without Bugfixes: the click must throw the NullReferenceException in
MySpaceAnalytics.GatherMinedStatsData of the report. A player's game crashes on it; under the
Remote plugin the click comes back as HTTP 500 with the exception, which the rig checks. fix runs it with this working copy as a dev
folder: the game must log the Bugfixes line and exit without an exception.

Prints one line per check and a RESULT line, exits 0 when every check passed.

Builds on tests/thrust-flames/rig.py for the server and client setup; its environment variables
apply here too (RIG_TASK defaults to SE-1877).
"""

from __future__ import annotations

import importlib.util
import os
import re
import shutil
import sys
import time
from pathlib import Path

os.environ.setdefault("RIG_TASK", "SE-1877")
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location(
    "flames", HERE.parent / "thrust-flames/rig.py"
)
flames = importlib.util.module_from_spec(spec)
spec.loader.exec_module(flames)

PROBE = HERE / "probe"
CRASH = "NullReferenceException"
CRASH_FRAME = "MySpaceAnalytics.GatherMinedStatsData"
FIXED = "Save progress box answered with no session, exiting the game"
checks = flames.checks
check = flames.check


class Client(flames.Client):
    def __init__(self, slot, bugfixes):
        super().__init__(slot, 0, "Leaver", bugfixes)
        loader = self.pulsar / "Interim"
        plugins = {
            "remote": (flames.REMOTE_REPO, "Remote.xml"),
            "direct-transport": (
                flames.DIRECT_TRANSPORT_REPO,
                "DirectTransportClient.xml",
            ),
            "savebox-probe": (PROBE, "SaveBoxProbe.xml"),
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

    def log(self) -> str:
        return flames.text(self.appdata / "SpaceEngineers.log")

    def save_box(self):
        """The save progress box on the main menu, None until it is there"""
        screens = self.api.list_screens()
        if any("MainMenu" in s.get("type", "") for s in screens):
            return next(
                (s for s in screens if s.get("type") == "MyGuiScreenMessageBox"), None
            )
        return None


def scenario(slot: dict, variant: str, answer: str) -> None:
    fix = variant == "fix"
    client = Client(slot, fix)
    server = flames.Server(slot, (client.steam_id,))
    server.start()
    server.wait()
    client.start(server.port)
    client.join()
    time.sleep(5)

    (client.appdata / "savebox-probe.txt").write_text("exit\n")
    done = client.appdata / "savebox-probe-done.txt"
    flames.until("the probe's report", lambda: flames.text(done), 300, client.alive)
    report = flames.text(done).strip()
    print(f"probe: {report}", flush=True)
    check(
        "the window close request came while the session was set and Sync.IsServer was true",
        "session_set=True multiplayer=False is_server=True" in report,
        report,
    )
    box = flames.until(
        "the save box on the main menu", client.save_box, 120, client.alive
    )
    check("the save progress box is on the main menu", bool(box), box)
    check("no session at the click", "session_now=False" in report, report)

    time.sleep(5)  # the player clicked 56 s later; the menu only has to settle
    button = "Yes" if answer == "yes" else "No"
    # The Remote plugin runs the click on the update thread and answers an exception out of it
    # with HTTP 500 and the exception in the body, so the game keeps running then.
    answered = ""
    try:
        client.api.control_click(text=button, screen=box["index"])
    except (
        Exception
    ) as e:  # noqa: BLE001 -- the game may be gone before the API answers
        response = getattr(e, "response", None)
        answered = response.text if response is not None else ""
        print(f"click {button}: {type(e).__name__}: {answered[:2000]}", flush=True)

    deadline = time.monotonic() + 120
    while client.alive() and time.monotonic() < deadline:
        time.sleep(1)
    log = client.log() + answered
    exception = CRASH in log and CRASH_FRAME in log
    print(
        f"client exited: {not client.alive()}, exit code {client.process.poll()}",
        flush=True,
    )
    if fix:
        check("Bugfixes handled the box", FIXED in log, "")
        check("no NullReferenceException", CRASH not in log, "")
        check("the game exited after the click", not client.alive(), "")
    else:
        check(
            f"vanilla throws {CRASH} in {CRASH_FRAME}",
            exception,
            "not in the log or the click's answer",
        )


def main() -> int:
    variant = sys.argv[1] if len(sys.argv) > 1 else ""
    answer = sys.argv[2] if len(sys.argv) > 2 else "no"
    assert variant in ("control", "fix") and answer in ("no", "yes"), __doc__
    flames.ROOT.mkdir(parents=True, exist_ok=True)
    slot = flames.claim_slot()
    error = None
    try:
        scenario(slot, variant, answer)
    except Exception as e:  # noqa: BLE001 -- reported in the RESULT line
        error = f"{type(e).__name__}: {e}"
        print(f"ERROR {error}", flush=True)
    finally:
        flames.stop_all()
        logs = flames.ROOT / f"logs-{variant}-{answer}"
        shutil.rmtree(logs, ignore_errors=True)
        logs.mkdir()
        for log in (
            flames.ROOT / "ds0/server.log",
            flames.ROOT / "Leaver.log",
            flames.ROOT / "c0-data/SpaceEngineers.log",
            flames.ROOT / "c0-data/savebox-probe-done.txt",
        ):
            if log.exists():
                shutil.copy(log, logs / f"{log.parent.name}-{log.name}")
    passed = error is None and bool(checks) and all(checks)
    print(
        f"RESULT variant={variant} answer={answer} slot={slot['SLOT']} checks={len(checks)} "
        f"failed={checks.count(False)} passed={passed}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
