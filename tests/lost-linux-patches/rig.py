"""Regression rig of SE-1587: lost LinuxCompat patches and the block NREs they led to.

    python3 rig.py input|files|tube [--linux-compat DIR] [--bugfixes]

An isolated headless Pulsar Interim client (Linux) loads a world offline with the probe plugin in
probe/. In the main menu the probe runs one input update, as a focused window does every frame,
then loads the world, logs to <appdata>/probe.log and exits the game after the world ran 20 s.
The world is the game's Empty World with a static station holding a large piston base with its
top on a second grid, and a conveyor tube damaged to 10 %.

input: the probe drops every Harmony patch on MyVRagePlatform.get_Input2 at Init, as a lost MonoMod
detour leaves it. The reporter's client crashed in MyVRageInput.UpdateStates that way.

files: the probe drops every Harmony patch on MyFileSystem.FileExists. Block models whose path
casing differs from the disk then load as Error.mwm, so the piston gets no subparts and the tube no
conveyor line, as on the reporter's client.

tube: like files, but the station holds only the damaged tube, so the piston can't crash the game
before the tube does.

--linux-compat runs a LinuxCompat working copy as a dev folder instead of the hub build (it needs
../../linux-dependencies and ../../linux-native-wrappers next to it). --bugfixes adds this working
copy of Bugfixes.

Pass: the input update was ok, the world ran 20 s, and no NullReferenceException in the game log.
Prints the evidence and a RESULT line, exits 0 on pass. Slot of RIG_TASK (default SE-1587), every
process under run-test.sh (RAM guard). RIG_PULSAR names the Pulsar folder to copy (default
~/.config/Pulsar).
"""

from __future__ import annotations

import argparse
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
TASK = os.environ.get("RIG_TASK", "SE-1587")
ROOT = HOME / ".se-test" / TASK
CLIENT_GIB = 3.7
WORLD_SECONDS = 20
# Older copies of run-test.sh take no game argument
GAME_ARG = ["se1"] if "se1|se2" in (PARALLEL / "run-test.sh").read_text() else []

BASE_GRID, PISTON, TUBE = 158700000000000001, 158700000000000002, 158700000000000003
TOP_GRID, TOP = 158700000000000011, 158700000000000012


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


# ---------------------------------------------------------------------------
# The world: the game's Empty World with a piston station
# ---------------------------------------------------------------------------


def block(xsi: str, subtype: str, cell, extra: str = "") -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{xsi}"><SubtypeName>{subtype}</SubtypeName>'
        f'{extra}<Min x="{cell[0]}" y="{cell[1]}" z="{cell[2]}" />'
        '<BlockOrientation Forward="Forward" Up="Up" /><ColorMaskHSV x="0" y="-0.8" z="0.55" />'
        "</MyObjectBuilder_CubeBlock>"
    )


def grid(entity_id: int, name: str, y: float, blocks: str, static: bool) -> str:
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        f"<SubtypeName /><EntityId>{entity_id}</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>"
        f'<PositionAndOrientation><Position x="0" y="{y}" z="0" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        f"<IsStatic>{str(static).lower()}</IsStatic><DisplayName>{name}</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks></MyObjectBuilder_EntityBase>"
    )


def station(scenario: str) -> str:
    tube = block(
        "ConveyorConnector",
        "ConveyorTube",
        (1, -1, 0),
        f"<EntityId>{TUBE}</EntityId><IntegrityPercent>0.1</IntegrityPercent><BuildPercent>1</BuildPercent>",
    )
    armor = block("CubeBlock", "LargeBlockArmorBlock", (0, -1, 0))
    if scenario == "tube":
        return grid(BASE_GRID, "TubeStation", 0.0, armor + tube, True)
    base = (
        armor
        + block(
            "PistonBase",
            "LargePistonBase",
            (0, 0, 0),
            f"<EntityId>{PISTON}</EntityId><IntegrityPercent>1</IntegrityPercent>"
            f"<BuildPercent>1</BuildPercent><TopBlockId>{TOP}</TopBlockId>"
            "<Velocity>0</Velocity><CurrentPosition>0</CurrentPosition>",
        )
        + tube
    )
    top = block(
        "PistonTop",
        "LargePistonTop",
        (0, 0, 0),
        f"<EntityId>{TOP}</EntityId><ParentEntityId>{PISTON}</ParentEntityId>",
    )
    return grid(BASE_GRID, "PistonStation", 0.0, base, True) + grid(
        TOP_GRID, "PistonTop", 5.0, top, False
    )


SETTINGS = {
    "GameMode": "Creative",
    "OnlineMode": "OFFLINE",
    "EnableRespawnShips": "false",
    "TrashRemovalEnabled": "false",
    "ExperimentalMode": "true",
    "AutoSaveInMinutes": "0",
}


def set_setting(text: str, name: str, value: str) -> str:
    new, count = re.subn(
        rf"<{name}>[^<]*</{name}>|<{name}\s*/>", f"<{name}>{value}</{name}>", text
    )
    return (
        new
        if count
        else text.replace("</Settings>", f"<{name}>{value}</{name}></Settings>", 1)
    )


def prepare_world(world: Path, scenario: str) -> None:
    if world.exists():
        shutil.rmtree(world)
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
        r"<SectorObjects>", "<SectorObjects>" + station(scenario), text, count=1
    )
    if not count:
        new, count = re.subn(
            r"<SectorObjects\s*/>",
            f"<SectorObjects>{station(scenario)}</SectorObjects>",
            text,
            count=1,
        )
    assert count == 1, "SectorObjects"
    sector.write_text(new, encoding="utf-8")


# ---------------------------------------------------------------------------
# The client
# ---------------------------------------------------------------------------


def configure_pulsar(pulsar: Path, plugins: dict[str, tuple[Path, str]]) -> None:
    legacy = pulsar / "Legacy"
    sources = legacy / "Sources/sources.xml"
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
    (legacy / "Profiles").mkdir(exist_ok=True)
    (legacy / "Profiles/Current.xml").write_text(
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
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("scenario", choices=("input", "files", "tube"))
    parser.add_argument("--linux-compat", type=Path)
    parser.add_argument("--bugfixes", action="store_true")
    args = parser.parse_args()

    slot = claim_slot()
    pulsar, appdata = ROOT / "c0", ROOT / "c0-data"
    ROOT.mkdir(parents=True, exist_ok=True)
    if not (pulsar / "Interim.bin").exists():
        run(NEW_PULSAR, pulsar, PULSAR_SRC)
    plugins = {"lost-patch-probe": (HERE / "probe", "LostPatchProbe.xml")}
    if args.linux_compat:
        plugins["linux-compat"] = (
            args.linux_compat.resolve(),
            "ClientPlugin/ClientPlugin.xml",
        )
    if args.bugfixes:
        plugins["bugfixes"] = (REPO, "BugfixesClient.xml")
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
    world = appdata / "world"
    prepare_world(world, args.scenario)
    probe_log, game_log, launch_log = (
        appdata / "probe.log",
        appdata / "SpaceEngineers.log",
        ROOT / "client.log",
    )

    env = {
        **os.environ,
        "PULSAR_NO_RENDER": "true",
        "ALSOFT_DRIVERS": "null",
        "LOST_PATCH_PROBE_LOG": str(probe_log),
        "LOST_PATCH_PROBE_WORLD": str(world),
        "LOST_PATCH_PROBE_DROP": "files" if args.scenario == "tube" else args.scenario,
        "LOST_PATCH_PROBE_SECONDS": str(WORLD_SECONDS),
    }
    command = [
        pulsar / "Interim.bin", "-multiInstance", "-bin64", STEAM / "SpaceEngineers/Bin64", "-lazySteam", "-noprompt", "-noupdate", "-nosplash",
        "-stablelogs", "-sources", "--no-steam", "--client-id", slot["CLIENT_ID_BASE"], "-appdata", appdata,
        "--quality", "minimal", "--resolution", "640x480", "--headless", "--no-render",
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

    probe, game = text(probe_log), text(game_log)
    logs = (
        ROOT
        / f"logs-{args.scenario}{'-lc' if args.linux_compat else ''}{'-bf' if args.bugfixes else ''}"
    )
    shutil.rmtree(logs, ignore_errors=True)
    logs.mkdir()
    for path in (probe_log, game_log, launch_log):
        if path.exists():
            shutil.copy(path, logs / path.name)

    for line in probe.splitlines():
        print(f"probe: {line}")
    nres = [
        i
        for i, line in enumerate(game.splitlines())
        if "NullReferenceException" in line
    ]
    lines = game.splitlines()
    for i in nres[:2]:
        for frame in lines[i : i + 6]:
            print(f"game: {frame.strip()[:300]}")
    missing_audio = game.count("Unable to find audio file")
    ran = f"DONE world ran {WORLD_SECONDS} s" in probe
    input_ok = "input update: ok" in probe
    passed = ran and not nres and input_ok
    print(
        f"RESULT scenario={args.scenario} linux_compat={'dev' if args.linux_compat else 'hub'} "
        f"bugfixes={args.bugfixes} passed={passed} world_ran={ran} nre={len(nres)} input_ok={input_ok} "
        f"missing_audio={missing_audio} seconds={time.monotonic() - started:.0f} logs={logs}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
