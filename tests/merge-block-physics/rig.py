"""Regression rig of the merge block physics fix (Docs/merge-block-physics.md).

    python3 rig.py server control|fix    a Magnetar server loads a world with a merge block
    python3 rig.py join control|fix      a headless client joins a server and streams one in

A merge block gets its merge field (its Physics) only from a dummy named "...merge..." in its
model. The rig makes that dummy go missing without a mod: the side under test runs from a stand-in
game install whose Content is a tree of links to the real one, except that
Models/Cubes/large/MergeBlock.mwm is a copy of MergeBlock_LOD1.mwm. That is the same block model
without its dummies, so everything else about the block still loads.

server: the server runs from the stand-in install. control is without Bugfixes, fix with this
working copy as a dev folder. Pass: the server gets its world ready.

join: the server runs from the real install without Bugfixes. The client runs from the stand-in
install, with or without Bugfixes. It joins over DirectTransport, respawns, and teleports next to
the grid, so the grid comes in through the entity creation thread, as in the crash report.
Pass: the client sees the grid and is still running 30 s later.

Prints the evidence lines and a RESULT line, exits 0 on pass. control is expected to fail with
the NullReferenceException in MyShipMergeBlock.OnAddedToScene.

Ports, ids and folders come from the workspace test slot of RIG_TASK (default SE-1480), every
process starts under run-test.sh (RAM guard). Paths are overridable by environment variables:
RIG_WORKSPACE, RIG_MAGNETAR, RIG_PULSAR, RIG_STEAM (steamapps/common), RIG_REMOTE_REPO,
RIG_DIRECT_TRANSPORT_REPO, RIG_GAME_CFG. The Remote repo's skills/se-remote must be importable
(it needs httpx).
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
REPO = Path(__file__).resolve().parents[2]
WORKSPACE = Path(os.environ.get("RIG_WORKSPACE", HOME / "ws/se"))
PARALLEL = WORKSPACE / "testing/parallel-test-work"
NEW_PULSAR = WORKSPACE / "testing/pulsar-dev-instances/new-pulsar-instance.sh"
MAGNETAR = Path(os.environ.get("RIG_MAGNETAR", HOME / ".config/Magnetar"))
PULSAR = Path(os.environ.get("RIG_PULSAR", HOME / ".config/Pulsar"))
STEAM = Path(os.environ.get("RIG_STEAM", HOME / ".steam/steam/steamapps/common"))
GAME = STEAM / "SpaceEngineers"
DEDICATED = STEAM / "SpaceEngineersDedicatedServer"
REMOTE_REPO = Path(os.environ.get("RIG_REMOTE_REPO", REPO.parent / "remote"))
DIRECT_TRANSPORT_REPO = Path(
    os.environ.get("RIG_DIRECT_TRANSPORT_REPO", WORKSPACE / "se1/direct-transport")
)
GAME_CFG = Path(
    os.environ.get("RIG_GAME_CFG", HOME / ".config/SpaceEngineers/SpaceEngineers.cfg")
)
TASK = os.environ.get("RIG_TASK", "SE-1480")
ROOT = HOME / ".se-test" / TASK

sys.path.insert(0, str(REMOTE_REPO / "skills/se-remote"))
from se_remote import RemoteAPI  # noqa: E402

CLIENT_GIB = 3.7
SERVER_GIB = 4.0
CRASH = "MyShipMergeBlock.OnAddedToScene"
GRID_ID = 144000000000148001
MERGE_ID = 144000000000148002
GRID_POSITION = (0.0, 0.0, 0.0)


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
# The stand-in install
# ---------------------------------------------------------------------------


def stand_in(install: Path, target: Path) -> Path:
    """target mirrors install through links, with the merge block model replaced"""
    if target.exists():
        shutil.rmtree(target)
    replaced = ("Content", "Models", "Cubes", "large")
    folder, mirror = install, target
    for depth, name in enumerate(replaced):
        mirror.mkdir(parents=True)
        for entry in folder.iterdir():
            if entry.name != name:
                (mirror / entry.name).symlink_to(entry)
        folder, mirror = folder / name, mirror / name
    mirror.mkdir()
    for entry in folder.iterdir():
        if entry.name != "MergeBlock.mwm":
            (mirror / entry.name).symlink_to(entry)
    donor = (folder / "MergeBlock_LOD1.mwm").read_bytes()
    assert b"merge_" not in donor.lower(), "the stand-in model still has a merge dummy"
    (mirror / "MergeBlock.mwm").write_bytes(donor)
    return target


# ---------------------------------------------------------------------------
# The world: the game's Empty World with one station holding a merge block
# ---------------------------------------------------------------------------


def block(xsi: str, subtype: str, cell, extra: str = "") -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{xsi}"><SubtypeName>{subtype}</SubtypeName>'
        f'{extra}<Min x="{cell[0]}" y="{cell[1]}" z="{cell[2]}" />'
        '<BlockOrientation Forward="Forward" Up="Up" /><ColorMaskHSV x="0" y="-0.8" z="0.55" />'
        "</MyObjectBuilder_CubeBlock>"
    )


def station() -> str:
    blocks = block("CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)) + block(
        "MergeBlock",
        "LargeShipMergeBlock",
        (0, 0, -1),
        f"<EntityId>{MERGE_ID}</EntityId>",
    )
    p = GRID_POSITION
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        f"<SubtypeName /><EntityId>{GRID_ID}</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>"
        f'<PositionAndOrientation><Position x="{p[0]}" y="{p[1]}" z="{p[2]}" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        "<IsStatic>true</IsStatic><DisplayName>MergeBlockStation</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks></MyObjectBuilder_EntityBase>"
    )


SETTINGS = {
    "GameMode": "Creative",
    "OnlineMode": "PUBLIC",
    "EnableRespawnShips": "false",
    "EnableSpaceSuitRespawn": "true",
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


def prepare_world(world: Path) -> None:
    if world.exists():
        shutil.rmtree(world)
    shutil.copytree(DEDICATED / "Content/CustomWorlds/Empty World", world)
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


# ---------------------------------------------------------------------------
# Loader configuration
# ---------------------------------------------------------------------------


def profile_xml(ids) -> str:
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>"
        for i in ids
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder><Local /><Mods /></Profile>\n"
    )


def write_sources(sources: Path, plugins: dict[str, tuple[Path, str]]) -> None:
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


# ---------------------------------------------------------------------------
# Processes
# ---------------------------------------------------------------------------

started: list[tuple[int, Path]] = []
# Older copies of run-test.sh take no game argument
GAME_ARG = ["se1"] if "se1|se2" in (PARALLEL / "run-test.sh").read_text() else []


def start(
    command, need_gib: float, cwd: Path, log: Path, env: dict
) -> subprocess.Popen:
    with open(log, "w") as out:
        process = subprocess.Popen(
            [
                str(PARALLEL / "run-test.sh"),
                *GAME_ARG,
                str(need_gib),
                "--",
                *map(str, command),
            ],
            cwd=cwd,
            env={**os.environ, **env},
            stdout=out,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            start_new_session=True,
        )
    started.append((process.pid, log))
    return process


def stop_all() -> None:
    for pid, _ in started:
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    for pid, _ in started:
        try:
            os.waitpid(pid, 0)
        except ChildProcessError:
            pass


def text(path: Path) -> str:
    try:
        return path.read_text(errors="replace")
    except FileNotFoundError:
        return ""


class Server:
    def __init__(self, slot: dict, ds64: Path, bugfixes: bool, admin: int):
        self.root = ROOT / "ds0"
        self.config, self.data = self.root / "magnetar", self.root / "data"
        self.log = self.root / "server.log"
        self.game_log = self.data / "SpaceEngineersDedicated.log"
        self.port = slot["DS_SERVER_PORT_BASE"]
        self.ds64 = ds64
        if self.root.exists():
            shutil.rmtree(self.root)
        self.config.mkdir(parents=True)
        template = MAGNETAR / "Magnetar"
        shutil.copytree(template / "Sources", self.config / "Sources")
        shutil.copy(template / "config.xml", self.config / "config.xml")
        if (template / "GitHub").exists():
            shutil.copytree(template / "GitHub", self.config / "GitHub")
        plugins = {
            "direct-transport": (DIRECT_TRANSPORT_REPO, "DirectTransportServer.xml")
        }
        if bugfixes:
            plugins["bugfixes"] = (REPO, "BugfixesServer.xml")
        write_sources(self.config / "Sources/sources.xml", plugins)
        (self.config / "Profiles").mkdir()
        (self.config / "Profiles/Current.xml").write_text(
            profile_xml(plugins), encoding="utf-8"
        )
        world = self.data / "Saves/Se1480"
        prepare_world(world)
        (self.data / "SpaceEngineers-Dedicated.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/'
            'XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
            f"<IP>127.0.0.1</IP><SteamPort>{slot['DS_STEAM_PORT_BASE']}</SteamPort><ServerPort>{self.port}</ServerPort>"
            f"<Administrators><unsignedLong>{admin}</unsignedLong></Administrators><Banned />"
            f"<ServerName>{TASK}</ServerName><WorldName>Se1480</WorldName>"
            "<PauseGameWhenEmpty>false</PauseGameWhenEmpty><AutoRestartEnabled>false</AutoRestartEnabled>"
            "<IgnoreLastSession>true</IgnoreLastSession><RemoteApiEnabled>false</RemoteApiEnabled>"
            f"<LoadWorld>{world}</LoadWorld></MyConfigDedicated>\n",
            encoding="utf-8",
        )

    def start(self) -> None:
        self.process = start(
            [
                MAGNETAR / "MagnetarInterim.bin",
                "-multiInstance",
                "-stableLogs",
                "-noimplicitmod",
                "-consent",
                "deny",
                "-config",
                self.config,
                "-ds64",
                self.ds64,
                "-path",
                self.data,
            ],
            SERVER_GIB,
            MAGNETAR,
            self.log,
            {"SE_DIRECT_TRANSPORT": "1"},
        )

    def ready(self) -> bool:
        return "Game ready" in text(self.log)

    def wait(self, timeout: float = 1800) -> bool:
        """True once ready, False when the server exited first"""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.ready():
                return True
            if self.process.poll() is not None:
                return False
            time.sleep(2)
        raise TimeoutError(f"The server neither got ready nor exited, see {self.log}")

    def evidence(self) -> list[str]:
        return [self.log, self.game_log]


class Client:
    def __init__(self, slot: dict, bin64: Path, bugfixes: bool):
        self.pulsar = ROOT / "c0"
        self.appdata = ROOT / "c0-data"
        self.launch_log = ROOT / "client.log"
        self.log = self.appdata / "SpaceEngineers.log"
        self.port = slot["REMOTE_PORT_BASE"]
        self.steam_id = slot["CLIENT_ID_BASE"]
        self.bin64 = bin64
        if not (self.pulsar / "Interim.bin").exists():
            run(NEW_PULSAR, self.pulsar, PULSAR)
        legacy = self.pulsar / "Legacy"
        plugins = {
            "remote": (REMOTE_REPO, "Remote.xml"),
            "direct-transport": (DIRECT_TRANSPORT_REPO, "DirectTransportClient.xml"),
        }
        if bugfixes:
            plugins["bugfixes"] = (REPO, "BugfixesClient.xml")
        write_sources(legacy / "Sources/sources.xml", plugins)
        (legacy / "Profiles/Current.xml").write_text(
            profile_xml(plugins), encoding="utf-8"
        )
        if self.appdata.exists():
            shutil.rmtree(self.appdata)
        self.appdata.mkdir(parents=True)
        (self.appdata / "Remote.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfig><Enabled>true</Enabled>'
            f"<ListenIP>127.0.0.1</ListenIP><ListenPort>{self.port}</ListenPort>"
            "<AdminPassword>SpaceEngineers</AdminPassword></PluginConfig>\n",
            encoding="utf-8",
        )
        shutil.copy(GAME_CFG, self.appdata / "SpaceEngineers.cfg")

    def start(self, server_port: int) -> None:
        self.process = start(
            [
                self.pulsar / "Interim.bin",
                "-multiInstance",
                "-bin64",
                self.bin64,
                "-lazySteam",
                "-noprompt",
                "-noupdate",
                "-nosplash",
                "-stablelogs",
                "-sources",
                "--no-steam",
                "--client-id",
                self.steam_id,
                "--client-name",
                "Merger",
                "-appdata",
                self.appdata,
                "--quality",
                "minimal",
                "--resolution",
                "640x480",
                "--no-audio",
                "--headless",
                "--no-render",
                "--connect",
                f"127.0.0.1:{server_port}",
            ],
            CLIENT_GIB,
            self.pulsar,
            self.launch_log,
            {"PULSAR_NO_RENDER": "true"},
        )
        self.api = RemoteAPI(
            f"http://127.0.0.1:{self.port}", username="admin", password="SpaceEngineers"
        )

    def alive(self) -> bool:
        return self.process.poll() is None

    def until(self, what: str, predicate, timeout: float):
        """predicate's first truthy value; None when the client exits first"""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not self.alive():
                return None
            try:
                value = predicate()
                if value:
                    return value
            except (
                Exception
            ):  # noqa: BLE001 -- the API is slow or down while a world loads
                pass
            time.sleep(2)
        raise TimeoutError(f"Client: {what}")

    def ready(self) -> bool:
        for box in [
            s
            for s in self.api.list_screens()
            if s.get("type") == "MyGuiScreenMessageBox"
        ]:
            self.api.control_click(text="OK", screen=box["index"])
        return bool(self.api.get_state().get("ready"))

    def character(self):
        try:
            character = self.api.get_character()
            if character.get("state") not in (None, "dead"):
                return character
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        screens = self.api.list_screens()
        medical = next(
            (i for i, s in enumerate(screens) if "Medical" in s.get("type", "")), None
        )
        if medical is not None:
            for button in ("Respawn", "Join"):
                try:
                    self.api.control_click(text=button, screen=medical)
                    break
                except Exception:  # noqa: BLE001 -- the other page of the screen
                    continue
        return None

    def sees_grid(self) -> bool:
        p = GRID_POSITION
        entities = self.api.get_entities(p[0], p[1], p[2], 100.0).get("entities", [])
        return any(e.get("entityId") == GRID_ID for e in entities)

    def evidence(self) -> list[str]:
        return [self.launch_log, self.log]


def report(paths, extra=()) -> bool:
    """Prints the interesting lines, True when the merge block crash is among them"""
    crashed = False
    for path in paths:
        lines = text(path).splitlines()
        for i, line in enumerate(lines):
            if (
                CRASH in line
                or "MergeBlockPhysics" in line
                or "Unhandled exception" in line
            ):
                print(f"{path.name}: {line.strip()[:400]}")
                if CRASH in line and not crashed:
                    crashed = True
                    # The frames down to the caller that added the grid
                    for frame in lines[i + 1 : i + 6]:
                        print(f"{path.name}:   {frame.strip()[:400]}")
    for line in extra:
        print(line)
    return crashed


def server_scenario(slot: dict, variant: str) -> tuple[bool, bool]:
    ds64 = stand_in(DEDICATED, ROOT / "install-ds") / "DedicatedServer64"
    server = Server(slot, ds64, variant == "fix", slot["CLIENT_ID_BASE"])
    server.start()
    ready = server.wait()
    crashed = report(
        server.evidence(), [f"server ready={ready} exit={server.process.poll()}"]
    )
    return ready, crashed


def join_scenario(slot: dict, variant: str) -> tuple[bool, bool]:
    bin64 = stand_in(GAME, ROOT / "install-client") / "Bin64"
    server = Server(
        slot, DEDICATED / "DedicatedServer64", False, slot["CLIENT_ID_BASE"]
    )
    server.start()
    if not server.wait():
        report(server.evidence(), ["server did not start"])
        return False, False
    client = Client(slot, bin64, variant == "fix")
    client.start(server.port)
    notes = []
    passed = False
    if client.until("world ready", client.ready, 1800):
        notes.append("client world ready")
        character = client.until("character", client.character, 300)
        if character:
            notes.append(f"character at {character.get('position')}")
            p = GRID_POSITION
            client.api.character_teleport(p[0] + 10, p[1], p[2] + 10)
            seen = client.until("grid streamed in", client.sees_grid, 120)
            if seen:
                notes.append("client sees the merge block station")
                time.sleep(30)
                passed = client.alive()
                notes.append(f"client alive 30 s later: {passed}")
    notes.append(f"client exit={client.process.poll()}")
    crashed = report(client.evidence(), notes)
    return passed, crashed


def main() -> int:
    scenario, variant = sys.argv[1], sys.argv[2]
    assert scenario in ("server", "join") and variant in ("control", "fix"), __doc__
    ROOT.mkdir(parents=True, exist_ok=True)
    slot = claim_slot()
    try:
        passed, crashed = (server_scenario if scenario == "server" else join_scenario)(
            slot, variant
        )
    finally:
        stop_all()
        # The next run reuses the folders, so keep this run's logs
        logs = ROOT / f"logs-{scenario}-{variant}"
        shutil.rmtree(logs, ignore_errors=True)
        logs.mkdir()
        for log in (
            ROOT / "ds0/server.log",
            ROOT / "ds0/data/SpaceEngineersDedicated.log",
            ROOT / "client.log",
            ROOT / "c0-data/SpaceEngineers.log",
        ):
            if log.exists():
                shutil.copy(log, logs / log.name)
    print(
        f"RESULT scenario={scenario} variant={variant} slot={slot['SLOT']} passed={passed} merge_block_nre={crashed}"
    )
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
