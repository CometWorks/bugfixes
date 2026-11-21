"""Refused join rig (Docs/replicable-hierarchy-after-disconnect.md, SE-1824).

    python3 rig.py control|fix

One Magnetar server and one headless client on an empty space world with one small ship: a seat,
a battery and an armor block. The client's character sits in the seat. The probe plugin in probe/
then hands MyMultiplayerClient.OnUserJoined a UserNotConnected join result on the game's main
thread, as when the server or a cluster Gateway refuses the client mid-session. The game disposes
the multiplayer session first and unloads the world after it, with the pilot still in the seat.

Checks:
- every client plugin built (Pulsar only warns about one that did not, so fix would run as control)
- the pilot is in the seat when refused
On the client log written after the refusal:
- the world unloads cleanly: no "Failed to cleanly unload session"
- no NullReferenceException thrown from MyCharacterReplicable.OnParentChanged
- the client rejoins the same server and gets back into the world, without "Loading screen failed",
  "Packet processing error" or the process exiting

control runs the client without Bugfixes, fix with this working copy as a dev folder. control is
expected to fail the two unload checks (the bug); its rejoin result is reported either way. fix must
pass all of them.

Prints one line per check and a RESULT line, exits 0 when every check passed.

Ports, ids and folders come from the workspace test slot of RIG_TASK (default SE-1824), every
process starts under run-test.sh (RAM guard). Paths are overridable by environment variables:
RIG_WORKSPACE, RIG_MAGNETAR, RIG_PULSAR, RIG_STEAM (steamapps/common), RIG_REMOTE_REPO,
RIG_DIRECT_TRANSPORT_REPO, RIG_GAME_CFG. RIG_CORE_PLUGINS lists working copies of core plugins
(linux-compat, dotnet-compat), separated by ":", that the server and the client load as dev
folders in place of the hub's, for when those do not fit the installed game. The Remote repo's
skills/se-remote must be importable (it needs httpx: uv run --with httpx python3 rig.py fix).
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
MAGNETAR = Path(os.environ.get("RIG_MAGNETAR", HOME / ".config/Magnetar"))
PULSAR = Path(os.environ.get("RIG_PULSAR", HOME / ".config/Pulsar"))
STEAM = Path(os.environ.get("RIG_STEAM", HOME / ".steam/steam/steamapps/common"))
GAME = STEAM / "SpaceEngineers"
DEDICATED = STEAM / "SpaceEngineersDedicatedServer"
REMOTE_REPO = Path(os.environ.get("RIG_REMOTE_REPO", REPO.parent / "remote"))
DIRECT_TRANSPORT_REPO = Path(
    os.environ.get("RIG_DIRECT_TRANSPORT_REPO", WORKSPACE / "se1/direct-transport")
)
CORE_PLUGINS = [Path(p) for p in os.environ.get("RIG_CORE_PLUGINS", "").split(":") if p]
GAME_CFG = Path(
    os.environ.get("RIG_GAME_CFG", HOME / ".config/SpaceEngineers/SpaceEngineers.cfg")
)
TASK = os.environ.get("RIG_TASK", "SE-1824")
ROOT = HOME / ".se-test" / TASK

sys.path.insert(0, str(REMOTE_REPO / "skills/se-remote"))
from se_remote import RemoteAPI  # noqa: E402

CLIENT_GIB = 3.7
SERVER_GIB = 4.0
GRID_ID = 144000000000182401
SEAT = (0, 0, 0)
UNCLEAN = "Failed to cleanly unload session"
REJOIN_FAILED = ("Loading screen failed", "Packet processing error")


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
# The world: the game's Empty World with the ship
# ---------------------------------------------------------------------------


def block(xsi: str, subtype: str, cell, forward="Forward", up="Up", extra="") -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{xsi}"><SubtypeName>{subtype}</SubtypeName>'
        f'{extra}<Min x="{cell[0]}" y="{cell[1]}" z="{cell[2]}" />'
        f'<BlockOrientation Forward="{forward}" Up="{up}" /><ColorMaskHSV x="0" y="-0.8" z="0.55" />'
        "</MyObjectBuilder_CubeBlock>"
    )


def ship() -> str:
    blocks = block("Cockpit", "LargeBlockCockpitSeat", SEAT)
    blocks += block(
        "BatteryBlock",
        "LargeBlockBatteryBlock",
        (0, 0, 1),
        extra="<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>",
    )
    blocks += block("CubeBlock", "LargeBlockArmorBlock", (0, 0, 2))
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        f"<SubtypeName /><EntityId>{GRID_ID}</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        "<IsStatic>false</IsStatic><DampenersEnabled>true</DampenersEnabled>"
        "<DisplayName>SeatShip</DisplayName><DestructibleBlocks>true</DestructibleBlocks>"
        "</MyObjectBuilder_EntityBase>"
    )


SETTINGS = {
    "GameMode": "Creative",
    "OnlineMode": "PUBLIC",
    "EnableRespawnShips": "false",
    "EnableSpaceSuitRespawn": "true",
    "TrashRemovalEnabled": "false",
    "ThrusterDamage": "false",
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
    new, count = re.subn(r"<SectorObjects>", "<SectorObjects>" + ship(), text, count=1)
    if not count:
        new, count = re.subn(
            r"<SectorObjects\s*/>",
            f"<SectorObjects>{ship()}</SectorObjects>",
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

started: list[int] = []
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
    started.append(process.pid)
    return process


def stop_all() -> None:
    for pid in started:
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    for pid in started:
        try:
            os.waitpid(pid, 0)
        except ChildProcessError:
            pass


def text(path: Path) -> str:
    try:
        return path.read_text(errors="replace")
    except FileNotFoundError:
        return ""


def until(what: str, predicate, timeout: float, alive=lambda: True):
    """predicate's first truthy value"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not alive():
            raise RuntimeError(f"exited while waiting for {what}")
        try:
            value = predicate()
            if value:
                return value
        except Exception:  # noqa: BLE001 -- the API is slow or down while a world loads
            pass
        time.sleep(1)
    raise TimeoutError(what)


class Server:
    def __init__(self, slot: dict, admins):
        self.root = ROOT / "ds0"
        self.config, self.data = self.root / "magnetar", self.root / "data"
        self.log = self.root / "server.log"
        self.port = slot["DS_SERVER_PORT_BASE"]
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
        for core in CORE_PLUGINS:
            plugins[core.name] = (core, "ServerPlugin/ServerPlugin.xml")
        write_sources(self.config / "Sources/sources.xml", plugins)
        (self.config / "Profiles").mkdir()
        (self.config / "Profiles/Current.xml").write_text(
            profile_xml(plugins), encoding="utf-8"
        )
        world = self.data / "Saves/Refused"
        prepare_world(world)
        admin_ids = "".join(f"<unsignedLong>{a}</unsignedLong>" for a in admins)
        (self.data / "SpaceEngineers-Dedicated.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/'
            'XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
            f"<IP>127.0.0.1</IP><SteamPort>{slot['DS_STEAM_PORT_BASE']}</SteamPort><ServerPort>{self.port}</ServerPort>"
            f"<Administrators>{admin_ids}</Administrators><Banned />"
            f"<ServerName>{TASK}</ServerName><WorldName>Refused</WorldName>"
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
                DEDICATED / "DedicatedServer64",
                "-path",
                self.data,
            ],
            SERVER_GIB,
            MAGNETAR,
            self.log,
            {"SE_DIRECT_TRANSPORT": "1"},
        )

    def wait(self) -> None:
        until(
            "the server to get ready",
            lambda: "Game ready" in text(self.log),
            1800,
            lambda: self.process.poll() is None,
        )


class Client:
    def __init__(self, slot: dict, index: int, name: str, bugfixes: bool):
        self.name = name
        self.pulsar = ROOT / f"c{index}"
        self.appdata = ROOT / f"c{index}-data"
        self.launch_log = ROOT / f"{name}.log"
        self.port = slot["REMOTE_PORT_BASE"] + index
        self.steam_id = slot["CLIENT_ID_BASE"] + index
        if not (self.pulsar / "Interim.bin").exists():
            run(NEW_PULSAR, self.pulsar, PULSAR)
        # The loader's own folder; older copies of new-pulsar-instance.sh leave it out
        loader = self.pulsar / "Interim"
        if not loader.exists():
            loader.mkdir()
            for name in ("Sources", "Profiles", "GitHub"):
                if (PULSAR / "Interim" / name).exists():
                    shutil.copytree(PULSAR / "Interim" / name, loader / name)
            shutil.copy(PULSAR / "Interim/config.xml", loader / "config.xml")
        plugins = {
            "remote": (REMOTE_REPO, "Remote.xml"),
            "direct-transport": (DIRECT_TRANSPORT_REPO, "DirectTransportClient.xml"),
        }
        if bugfixes:
            plugins["bugfixes"] = (REPO, "BugfixesClient.xml")
        plugins["refused-join-probe"] = (HERE / "probe", "RefusedJoinProbe.xml")
        for core in CORE_PLUGINS:
            plugins[core.name] = (core, "ClientPlugin/ClientPlugin.xml")
        write_sources(loader / "Sources/sources.xml", plugins)
        (loader / "Profiles/Current.xml").write_text(
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
                GAME / "Bin64",
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
                self.name,
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

    def join(self) -> None:
        until(f"{self.name}: world ready", self.ready, 1800, self.alive)
        until(f"{self.name}: character", self.character, 300, self.alive)

    def log(self) -> str:
        return text(self.appdata / "SpaceEngineers.log")

    def probe(self, command: str, answer: str, timeout: float) -> str:
        """Sends the probe a command, returns its first report line that starts with answer"""
        done = self.appdata / "refused-join-probe-done.txt"
        seen = len(text(done).splitlines())
        (self.appdata / "refused-join-probe.txt").write_text(command + "\n")

        def reported():
            for line in text(done).splitlines()[seen:]:
                if line.startswith((answer, "error", "not ")):
                    return line
            return None

        line = until(f"{self.name}: probe {command}", reported, timeout, self.alive)
        print(f"probe: {line}", flush=True)
        return line

    def at_main_menu(self) -> bool:
        screens = {s.get("type") for s in self.api.list_screens()}
        return "MyGuiScreenMainMenu" in screens and not self.api.get_state().get(
            "active"
        )


checks: list[bool] = []


def check(name: str, passed: bool, detail) -> None:
    checks.append(passed)
    print(f"{'PASS' if passed else 'FAIL'} {name}: {detail}", flush=True)


def sit(pilot: Client) -> None:
    pilot.api.character_teleport(SEAT[0], SEAT[1], SEAT[2] - 3.0)
    time.sleep(2)
    for _ in range(5):
        pilot.api.character_look_at(*SEAT)
        pilot.api.character_use()
        try:
            until(
                "sitting",
                lambda: pilot.api.get_character().get("state") == "sitting",
                8,
            )
            return
        except TimeoutError:
            continue
    raise RuntimeError(f"the pilot did not sit: {pilot.api.get_character()}")


def onparent_nre(log: str) -> bool:
    """A NullReferenceException with MyCharacterReplicable.OnParentChanged in its stack"""
    return any(
        "OnParentChanged" in part
        for part in log.split("System.NullReferenceException")[1:]
    )


def scenario(slot: dict, variant: str) -> None:
    pilot = Client(slot, 0, "Pilot", variant == "fix")
    server = Server(slot, (pilot.steam_id,))
    server.start()
    server.wait()
    pilot.start(server.port)
    pilot.join()
    # A plugin that fails to build is left out with only a warning, and fix would run as control
    failed = [
        l.split("[ERROR]")[-1].strip()
        for l in text(pilot.pulsar / "Interim/info.log").splitlines()
        if "Failed to build" in l
    ]
    check("every plugin built", not failed, failed or "yes")
    sit(pilot)
    print("pilot seated", flush=True)

    # The server refuses the seated pilot
    mark = len(pilot.log())
    line = pilot.probe("refuse", "refusing", 60)
    check("the pilot is seated when refused", "seated True" in line, line)
    until("the main menu", pilot.at_main_menu, 120, pilot.alive)
    unload = pilot.log()[mark:]
    unclean = [l.strip() for l in unload.splitlines() if UNCLEAN in l]
    check("the world unloads cleanly", not unclean, unclean or "no unload error")
    nre = onparent_nre(unload)
    check(
        "no NullReferenceException from OnParentChanged",
        not nre,
        "thrown" if nre else "none",
    )

    # Back into the same server
    mark = len(pilot.log())
    pilot.probe("join", "joining", 60)

    def back():
        for l in pilot.log()[mark:].splitlines():
            if any(f in l for f in REJOIN_FAILED):
                return l.strip()
        if pilot.ready() and pilot.character():
            return "in the world"
        return None

    try:
        outcome = until("the rejoin", back, 300, pilot.alive)
    except (TimeoutError, RuntimeError) as e:
        outcome = f"{type(e).__name__}: {e}"
    check("the rejoin reaches the world", outcome == "in the world", outcome)


def main() -> int:
    variant = sys.argv[1] if len(sys.argv) > 1 else ""
    assert variant in ("control", "fix"), __doc__
    ROOT.mkdir(parents=True, exist_ok=True)
    slot = claim_slot()
    error = None
    try:
        scenario(slot, variant)
    except Exception as e:  # noqa: BLE001 -- reported in the RESULT line
        error = f"{type(e).__name__}: {e}"
        print(f"ERROR {error}", flush=True)
    finally:
        stop_all()
        logs = ROOT / f"logs-{variant}"
        shutil.rmtree(logs, ignore_errors=True)
        logs.mkdir()
        for log in (
            ROOT / "ds0/server.log",
            ROOT / "Pilot.log",
            ROOT / "c0-data/SpaceEngineers.log",
            ROOT / "c0-data/refused-join-probe-done.txt",
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
