"""Regression rig of SE-1934: blocks losing their own terminal controls when a mod touches them early.

    python3 rig.py control|off|fix

An isolated headless Pulsar Interim client (Linux) loads a world offline with the probe plugin in
probe/. Right after MySession.LoadDataComponents, where mods run LoadData and before any block
entity exists, the probe reads the IMyTerminalBlock controls and the IMyTimerBlock actions and adds
a ProbeButton control to IMyTimerBlock. The world is the game's Empty World with a static station
holding a timer block. Once the world is ready the probe reports the timer's controls and actions,
exits to the main menu, loads the world a second time and reports again.

control runs without Bugfixes, off with this working copy of Bugfixes and
EarlyModTerminalControlsPatch set to false in Bugfixes.cfg, fix with the fix on.

Pass for fix: on both loads the timer has its vanilla controls (OnOff, Name, CustomData,
OpenToolbar, TriggerNow) once each, ProbeButton once, and its actions, and Bugfixes stopped locking
the terminal control factory once each world was ready. Pass for control and off:
the vanilla controls are missing, as in the bug report. Prints the probe's reports and a RESULT
line, exits 0 on pass. Slot of RIG_TASK (default SE-1934), every process under run-test.sh (RAM
guard). RIG_PULSAR names the Pulsar folder to copy (default ~/.config/Pulsar), RIG_CORE_PLUGINS
colon-separated core plugin dev folders to use instead of the hub's. mp.py runs the same test on a
Magnetar server and a joining client.
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

HOME = Path.home()
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
WORKSPACE = Path(os.environ.get("RIG_WORKSPACE", HOME / "ws/se"))
PARALLEL = WORKSPACE / "testing/parallel-test-work"
NEW_PULSAR = WORKSPACE / "testing/pulsar-dev-instances/new-pulsar-instance.sh"
PULSAR_SRC = Path(os.environ.get("RIG_PULSAR", HOME / ".config/Pulsar"))
STEAM = Path(os.environ.get("RIG_STEAM", HOME / ".steam/steam/steamapps/common"))
GAME_CFG = Path(
    os.environ.get("RIG_GAME_CFG", HOME / ".config/SpaceEngineers/SpaceEngineers.cfg")
)
TASK = os.environ.get("RIG_TASK", "SE-1934")
ROOT = HOME / ".se-test" / TASK
CLIENT_GIB = 3.7
# Dev folders of core plugins (linux-compat, dotnet-compat) to use instead of the hub's, for when
# the hub build does not compile against the installed game
CORE_PLUGINS = [Path(p) for p in os.environ.get("RIG_CORE_PLUGINS", "").split(":") if p]
# Older copies of run-test.sh take no game argument
GAME_ARG = ["se1"] if "se1|se2" in (PARALLEL / "run-test.sh").read_text() else []
VANILLA = ("OnOff", "Name", "CustomData", "OpenToolbar", "TriggerNow")
SETTINGS = {
    "GameMode": "Creative",
    "OnlineMode": "OFFLINE",
    "EnableRespawnShips": "false",
    "TrashRemovalEnabled": "false",
    "ExperimentalMode": "true",
    "AutoSaveInMinutes": "0",
}


def run(*command) -> str:
    return subprocess.run(
        [str(c) for c in command], check=True, capture_output=True, text=True
    ).stdout.strip()


def claim_slot() -> dict[str, int]:
    slot = run(PARALLEL / "slot.sh", "claim", TASK)
    values = {
        k: int(v)
        for k, v in re.findall(
            r"(\w+_BASE)=(\d+)", run(PARALLEL / "slot.sh", "env", slot)
        )
    }
    values["SLOT"] = int(slot)
    return values


def block(xsi: str, subtype: str, cell, extra: str = "") -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{xsi}"><SubtypeName>{subtype}</SubtypeName>'
        f'{extra}<Min x="{cell[0]}" y="{cell[1]}" z="{cell[2]}" />'
        '<BlockOrientation Forward="Forward" Up="Up" /></MyObjectBuilder_CubeBlock>'
    )


def station() -> str:
    blocks = block("CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)) + block(
        "TimerBlock",
        "TimerBlockLarge",
        (0, 1, 0),
        "<EntityId>193400000000000002</EntityId>",
    )
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        "<SubtypeName /><EntityId>193400000000000001</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        "<IsStatic>true</IsStatic><DisplayName>TimerStation</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks></MyObjectBuilder_EntityBase>"
    )


def set_setting(text: str, name: str, value: str) -> str:
    new, count = re.subn(
        rf"<{name}>[^<]*</{name}>|<{name}\s*/>", f"<{name}>{value}</{name}>", text
    )
    return (
        new
        if count
        else text.replace("</Settings>", f"<{name}>{value}</{name}></Settings>", 1)
    )


def prepare_world(world: Path) -> None:
    for install in ("SpaceEngineersDedicatedServer", "SpaceEngineers"):
        source = STEAM / install / "Content/CustomWorlds/Empty World"
        if source.exists():
            break
    shutil.copytree(source, world)
    for name in ("SANDBOX_0_0_0_.sbsB5", "Sandbox.sbcB5"):
        (world / name).unlink(missing_ok=True)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / name
        text = path.read_text(encoding="utf-8")
        for key, value in SETTINGS.items():
            text = set_setting(text, key, value)
        path.write_text(text, encoding="utf-8")
    sector = world / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    new, count = re.subn(
        r"<SectorObjects>", "<SectorObjects>" + station(), text, count=1
    )
    if not count:
        new, count = re.subn(
            r"<SectorObjects\s*/>",
            f"<SectorObjects>{station()}</SectorObjects>",
            text,
            count=1,
        )
    assert count == 1, "SectorObjects"
    sector.write_text(new, encoding="utf-8")


def configure_pulsar(pulsar: Path, plugins: dict[str, tuple[Path, str]]) -> None:
    loader = pulsar / "Interim"
    if not loader.exists():
        loader.mkdir()
        for name in ("Sources", "Profiles", "GitHub"):
            if (PULSAR_SRC / "Interim" / name).exists():
                shutil.copytree(PULSAR_SRC / "Interim" / name, loader / name)
        shutil.copy(PULSAR_SRC / "Interim/config.xml", loader / "config.xml")
    sources = loader / "Sources/sources.xml"
    tree = ET.parse(sources)
    root = tree.getroot()
    local = root.find("LocalPluginSources")
    if local is not None:
        root.remove(local)
    local = ET.SubElement(root, "LocalPluginSources")
    for name, (folder, file) in plugins.items():
        plugin = ET.SubElement(local, "LocalPlugin")
        for tag, value in (
            ("Name", name),
            ("Folder", str(folder)),
            ("File", file),
            ("Enabled", "true"),
        ):
            ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>"
        for i in plugins
    )
    (loader / "Profiles").mkdir(exist_ok=True)
    (loader / "Profiles/Current.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder><Local /><Mods /></Profile>\n",
        encoding="utf-8",
    )


def text(path: Path) -> str:
    try:
        return path.read_text(errors="replace")
    except FileNotFoundError:
        return ""


def main() -> int:
    variant = sys.argv[1] if len(sys.argv) > 1 else ""
    assert variant in ("control", "off", "fix"), __doc__

    slot = claim_slot()
    pulsar, appdata = ROOT / "c0", ROOT / "c0-data"
    ROOT.mkdir(parents=True, exist_ok=True)
    if not (pulsar / "Interim.bin").exists():
        run(NEW_PULSAR, pulsar, PULSAR_SRC)
    plugins = {"early-controls-probe": (HERE / "probe", "EarlyControlsProbe.xml")}
    if variant != "control":
        plugins["bugfixes"] = (REPO, "BugfixesClient.xml")
    for core in CORE_PLUGINS:
        plugins[core.name] = (core, "ClientPlugin/ClientPlugin.xml")
    configure_pulsar(pulsar, plugins)

    if appdata.exists():
        shutil.rmtree(appdata)
    appdata.mkdir(parents=True)
    cfg = GAME_CFG.read_text(encoding="utf-8-sig")
    # Without Experimental mode the world load stops at a message box
    cfg = re.sub(
        r'(<Key>ExperimentalMode</Key>\s*<Value>\s*<Value xsi:type="xsd:string">)False',
        r"\1True",
        cfg,
    )
    (appdata / "SpaceEngineers.cfg").write_text(cfg, encoding="utf-8")
    if variant == "off":
        (appdata / "Bugfixes.cfg").write_text(
            "<PluginConfig><EarlyModTerminalControlsPatch>false</EarlyModTerminalControlsPatch></PluginConfig>\n",
            encoding="utf-8",
        )
    world = appdata / "world"
    prepare_world(world)
    probe_log, game_log, launch_log = (
        appdata / "probe.log",
        appdata / "SpaceEngineers.log",
        ROOT / "client.log",
    )

    env = {
        **os.environ,
        "PULSAR_NO_RENDER": "true",
        "ALSOFT_DRIVERS": "null",
        "EARLY_CONTROLS_PROBE_LOG": str(probe_log),
        "EARLY_CONTROLS_PROBE_WORLD": str(world),
    }
    command = [
        pulsar / "Interim.bin", "-multiInstance", "-bin64", STEAM / "SpaceEngineers/Bin64", "-lazySteam", "-noprompt", "-noupdate", "-nosplash",
        "-stablelogs", "-sources", "--no-steam", "--client-id", slot["CLIENT_ID_BASE"], "-appdata", appdata,
        "--quality", "minimal", "--resolution", "640x480", "--no-audio", "--headless", "--no-render",
    ]  # fmt: skip
    started = time.monotonic()
    with open(launch_log, "w") as out:
        process = subprocess.Popen(
            [str(PARALLEL / "run-test.sh"), *GAME_ARG, str(CLIENT_GIB), "--", *map(str, command)],
            cwd=pulsar, env=env, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
            start_new_session=True,
        )  # fmt: skip
    try:
        # The probe exits the game when it is done; a crash ends it earlier
        deadline = time.monotonic() + 1800
        while process.poll() is None and time.monotonic() < deadline:
            if "DONE" in text(probe_log):
                time.sleep(10)
                break
            # A probe that failed to compile never writes: stop instead of idling in the menu
            if not text(probe_log) and "GUI Stats" in text(game_log):
                print("probe did not load, see Pulsar's info.log", flush=True)
                break
            time.sleep(2)
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
        run(PARALLEL / "slot.sh", "release", slot["SLOT"])

    probe, game = text(probe_log), text(game_log)
    logs = ROOT / f"logs-{variant}"
    shutil.rmtree(logs, ignore_errors=True)
    logs.mkdir()
    for path in (probe_log, game_log, launch_log):
        if path.exists():
            shutil.copy(path, logs / path.name)

    for line in probe.splitlines():
        print(f"probe: {line}")
    reports = [
        dict(re.findall(r"(\w+)=(\S+)", line))
        for line in probe.splitlines()
        if " REPORT " in line
    ]
    early = (
        probe.count("early access in LoadData") == 2 and "timers_in_world=0" in probe
    )

    def fixed(report) -> bool:
        ids = dict(item.split(":") for item in report["ids"].split(","))
        return (
            all(ids.get(i) == "1" for i in VANILLA + ("ProbeButton",))
            and int(report["timer_actions"]) > 0
            and report["timer_TriggerDelay_property"] == "True"
        )

    complete = len(reports) == 2 and "DONE both loads reported" in probe
    unlocked = game.count("stopped locking the terminal control factory")
    if variant == "fix":
        passed = complete and early and all(fixed(r) for r in reports) and unlocked == 2
    else:
        passed = complete and early and not any(fixed(r) for r in reports)
    exceptions = game.count("Exception")
    print(
        f"RESULT variant={variant} passed={passed} reports={len(reports)} early_before_blocks={early} "
        f"fixed={[fixed(r) for r in reports]} factory_unlocked={unlocked} exceptions_in_log={exceptions} "
        f"seconds={time.monotonic() - started:.0f} logs={logs}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
