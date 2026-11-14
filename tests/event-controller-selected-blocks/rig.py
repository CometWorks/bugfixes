#!/usr/bin/env python3
"""Regression test of Docs/event-controller-selected-blocks.md: paste an event controller whose SelectedBlocks lists one battery twice.

Usage: rig.py on|off   (Bugfixes from this checkout enabled or not)
Offline headless --no-render client in its own Pulsar folder (~/.se-test/SE-1481-c0, made with
testing/pulsar-dev-instances/new-pulsar-instance.sh), on the ports and ids of slot $SLOT.
Run it with the Python of the se-remote skill. Prints a PASS or FAIL line.
"""

import glob, os, re, shutil, signal, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path.home() / ".claude/skills/se-remote"))
from se_remote import RemoteAPI  # noqa: E402

HOME = Path.home()
SLOT = int(
    os.environ.get("SLOT", "21")
)  # testing/parallel-test-work/slot.sh claim SE-1481
TASK = "SE-1481"
PULSAR = HOME / f".se-test/{TASK}-c0"
DATA = HOME / f".se-test/{TASK}-c0-data"
PORT = 25000 + 10 * SLOT
CLIENT_ID = str(76561199500010000 + 10 * SLOT)
BUGFIXES = Path(__file__).resolve().parents[2]
BIN64 = HOME / ".steam/debian-installation/steamapps/common/SpaceEngineers/Bin64"
WORLD_SRC = HOME / ".config/SpaceEngineers/Saves/1234567892/RemoteAPITestSpace"
TESTING = HOME / "ws/se/testing/parallel-test-work"
HERE = Path(__file__).resolve().parent

PROFILE = """<?xml version="1.0" encoding="utf-8"?>
<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Name>Current</Name>
  <GitHub />
  <DevFolder>
    <LocalFolderConfig><Id>remote</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
    <LocalFolderConfig><Id>direct-transport</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
{bugfixes}  </DevFolder>
  <Local />
  <Mods />
</Profile>
"""


def setup(enabled):
    legacy = PULSAR / "Legacy"
    bf = "    <LocalFolderConfig><Id>bugfixes</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>\n"
    (legacy / "Profiles/Current.xml").write_text(
        PROFILE.format(bugfixes=bf if enabled else "")
    )
    src = legacy / "Sources/sources.xml"
    text = src.read_text()
    text = re.sub(
        r"<Folder>[^<]*/se1/plugins/bugfixes[^<]*</Folder>",
        f"<Folder>{BUGFIXES}</Folder>",
        text,
    )
    src.write_text(text)
    DATA.mkdir(parents=True, exist_ok=True)
    (DATA / "Remote.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n  <Enabled>true</Enabled>\n  <ListenIP>127.0.0.1</ListenIP>\n'
        f"  <ListenPort>{PORT}</ListenPort>\n  <AdminPassword>SpaceEngineers</AdminPassword>\n</PluginConfig>\n"
    )
    world = DATA / "Saves" / CLIENT_ID / "SE1481"
    if world.exists():
        shutil.rmtree(world)
    shutil.copytree(WORLD_SRC, world)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        p = world / name
        p.write_text(
            p.read_text().replace(
                "<TrashRemovalEnabled>true", "<TrashRemovalEnabled>false"
            )
        )
    for f in glob.glob(str(DATA / "SpaceEngineers*.log")):
        os.remove(f)
    return world


def game_log():
    logs = sorted(glob.glob(str(DATA / "SpaceEngineers*.log")), key=os.path.getmtime)
    return Path(logs[-1]).read_text(errors="replace") if logs else ""


def main():
    mode = sys.argv[1]
    world = setup(mode == "on")
    argv = [
        str(TESTING / "run-test.sh"),
        "se1",
        "3.7",
        "--",
        str(PULSAR / "Interim.bin"),
        "-noSplash",
        "-noPrompt",
        "-noUpdate",
        "-sources",
        "-lazySteam",
        "-multiInstance",
        "-bin64",
        str(BIN64),
        "-appdata",
        str(DATA),
        "--no-steam",
        "--headless",
        "--no-render",
        "--quality",
        "minimal",
        "--resolution",
        "640x480",
        "--no-audio",
        "--client-id",
        CLIENT_ID,
        "--client-name",
        "se1481",
    ]
    launcher_log = HERE / f"launcher-{mode}.log"
    env = {**os.environ, "PULSAR_NO_RENDER": "true"}
    with open(launcher_log, "wb") as out:
        proc = subprocess.Popen(
            argv,
            stdout=out,
            stderr=subprocess.STDOUT,
            env=env,
            start_new_session=True,
            cwd=str(PULSAR),
        )
    (HERE / "client.pid").write_text(f"{proc.pid}\n")
    api = RemoteAPI(f"http://127.0.0.1:{PORT}", timeout=30)
    result = "FAIL"
    try:
        deadline = time.time() + 900
        while True:
            if proc.poll() is not None:
                raise RuntimeError(
                    f"client exited before the API answered, see {launcher_log}"
                )
            try:
                api.ping()
                break
            except Exception:
                if time.time() > deadline:
                    raise TimeoutError("Remote API not answering")
                time.sleep(5)
        api.load(str(world))
        api.wait_for_state(ready=True, max_wait=600, poll_interval=3)
        time.sleep(5)
        grids = api.paste_blueprint(
            xml=(HERE / "ecdup.sbc").read_text(), timeout_seconds=60
        )
        print(
            "pasted:",
            [(g.get("displayName") or g.get("name"), g.get("entityId")) for g in grids],
        )
        alive = True
        for _ in range(20):  # ProcessSelectedBlocks runs every 10th frame after Init
            time.sleep(1)
            if proc.poll() is not None:
                alive = False
                break
            try:
                api.ping()
            except Exception:
                pass
        time.sleep(3)
        log = game_log()
        crash = (
            "An item with the same key has already been added" in log
            and "ProcessSelectedBlocks" in log
        )
        dropped = [
            l for l in log.splitlines() if "EventControllerSelectedBlocksPatch" in l
        ]
        selected = None
        if alive and not crash:
            try:
                selected = api.list_blocks(grids[0]["entityId"])
            except Exception as e:  # noqa: BLE001
                selected = f"(block query failed: {e})"
        print(f"mode={mode} alive={alive} crash_in_log={crash}")
        for l in dropped:
            print("log:", l.strip())
        if selected is not None:
            print("event controller:", selected)
        if mode == "off":
            result = "PASS" if crash else "FAIL"
            print(
                f"{result}: without Bugfixes the game crashes in ProcessSelectedBlocks"
            )
        else:
            result = "PASS" if alive and not crash and dropped else "FAIL"
            print(
                f"{result}: with Bugfixes the game keeps running and drops the repeated id"
            )
    finally:
        try:
            api.exit_game(force=True)
        except Exception:
            pass
        try:
            proc.wait(60)
        except subprocess.TimeoutExpired:
            os.killpg(proc.pid, signal.SIGTERM)
            try:
                proc.wait(30)
            except subprocess.TimeoutExpired:
                os.killpg(proc.pid, signal.SIGKILL)
        log = game_log()
        (HERE / f"game-{mode}.log").write_text(log)
    sys.exit(0 if result == "PASS" else 1)


if __name__ == "__main__":
    main()
