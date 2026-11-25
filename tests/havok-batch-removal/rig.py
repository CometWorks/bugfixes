#!/usr/bin/env python3
"""Regression test of Docs/havok-batch-removal.md: a Havok remove batch with a body that is not in the world.

Usage: rig.py on|off [twice|removed]   (Bugfixes from this checkout enabled or not)

An offline headless --no-render client in its own Pulsar folder (~/.se-test/SE-1534-c0, made with
testing/pulsar-dev-instances/new-pulsar-instance.sh) loads a world with grids. The probe plugin in
probe/ then batches the removal of a grid's rigid body twice (twice, the default) or after removing
it from the world (removed), and finishes the batch.

off: the client must die in Havok.dll, at +0x5b8b70 when systemd-coredump keeps the core.
on:  the client must go on, with the body out of the world once and the Bugfixes warning in the log.

Ports and ids come from the workspace test slot of SE-1534, the client starts under run-test.sh
(RAM guard). Run it with the Python of the se-remote skill. Prints a PASS or FAIL line, exits 0 on
PASS.
"""

import glob, os, re, shutil, signal, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path.home() / ".claude/skills/se-remote"))
from se_remote import RemoteAPI  # noqa: E402

HOME = Path.home()
TASK = "SE-1534"
TESTING = HOME / "ws/se/testing"
SLOT = int(
    subprocess.run(
        [str(TESTING / "parallel-test-work/slot.sh"), "claim", TASK],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.split()[-1]
)
PULSAR = HOME / f".se-test/{TASK}-c0"
DATA = HOME / f".se-test/{TASK}-c0-data"
PORT = 25000 + 10 * SLOT
CLIENT_ID = str(76561199500010000 + 10 * SLOT)
HERE = Path(__file__).resolve().parent
BUGFIXES = HERE.parents[1]
BIN64 = HOME / ".steam/debian-installation/steamapps/common/SpaceEngineers/Bin64"
WORLD_SRC = HOME / ".config/SpaceEngineers/Saves/1234567892/RemoteAPITestSpace"
CRASH_SITE = "Havok.dll + 0x5b8b70"

PROFILE = """<?xml version="1.0" encoding="utf-8"?>
<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Name>Current</Name>
  <GitHub />
  <DevFolder>
    <LocalFolderConfig><Id>remote</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
    <LocalFolderConfig><Id>direct-transport</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
    <LocalFolderConfig><Id>batch-probe</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
{bugfixes}  </DevFolder>
  <Local />
  <Mods />
</Profile>
"""

PROBE_SOURCE = f"""    <LocalPlugin>
      <Name>batch-probe</Name>
      <Folder>{HERE / "probe"}</Folder>
      <File>BatchProbe.xml</File>
      <Enabled>true</Enabled>
    </LocalPlugin>
"""


def setup(enabled):
    if not PULSAR.exists():
        subprocess.run(
            [str(TESTING / "pulsar-dev-instances/new-pulsar-instance.sh"), str(PULSAR)],
            check=True,
            stdout=subprocess.DEVNULL,
        )
    legacy = PULSAR / "Legacy"
    bf = "    <LocalFolderConfig><Id>bugfixes</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>\n"
    (legacy / "Profiles/Current.xml").write_text(
        PROFILE.format(bugfixes=bf if enabled else "")
    )
    src = legacy / "Sources/sources.xml"
    text = src.read_text()
    text = re.sub(
        r"(<Name>bugfixes</Name>\s*<Folder>)[^<]*(</Folder>)",
        rf"\g<1>{BUGFIXES}\g<2>",
        text,
    )
    text = re.sub(
        r" *<LocalPlugin>\s*<Name>batch-probe</Name>.*?</LocalPlugin>\n",
        "",
        text,
        flags=re.S,
    )
    text = text.replace(
        "<LocalPluginSources>\n", "<LocalPluginSources>\n" + PROBE_SOURCE, 1
    )
    src.write_text(text)
    DATA.mkdir(parents=True, exist_ok=True)
    (DATA / "Remote.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n  <Enabled>true</Enabled>\n  <ListenIP>127.0.0.1</ListenIP>\n'
        f"  <ListenPort>{PORT}</ListenPort>\n  <AdminPassword>SpaceEngineers</AdminPassword>\n</PluginConfig>\n"
    )
    world = DATA / "Saves" / CLIENT_ID / "SE1534"
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
    for f in glob.glob(str(DATA / "SpaceEngineers*.log")) + glob.glob(
        str(DATA / "batch-probe*.txt")
    ):
        os.remove(f)
    return world


def game_log():
    logs = sorted(glob.glob(str(DATA / "SpaceEngineers*.log")), key=os.path.getmtime)
    return Path(logs[-1]).read_text(errors="replace") if logs else ""


def client_pid():
    """The game process, by its own -appdata folder (run-test.sh is its parent)."""
    for cmdline in glob.glob("/proc/[0-9]*/cmdline"):
        try:
            words = Path(cmdline).read_bytes().split(b"\0")
        except OSError:
            continue
        if words[0].endswith(b"Interim.bin") and str(DATA).encode() in words:
            return int(cmdline.split("/")[2])
    return None


def crash_site(pid):
    """The top frames of the client's core dump, if systemd-coredump has one."""
    if not pid or not shutil.which("coredumpctl"):
        return ""
    for _ in range(24):  # a 3 GiB core takes a while to be written
        out = subprocess.run(
            ["coredumpctl", "info", "--no-pager", str(pid)],
            capture_output=True,
            text=True,
        ).stdout
        if "Stack trace" in out or "Signal:" in out and "Storage:" in out:
            return out
        time.sleep(5)
    return ""


def main():
    mode = sys.argv[1]
    probe_mode = sys.argv[2] if len(sys.argv) > 2 else "twice"
    world = setup(mode == "on")
    argv = [
        str(TESTING / "parallel-test-work/run-test.sh"),
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
        "se1534",
    ]
    launcher_log = HERE / f"launcher-{mode}-{probe_mode}.log"
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
        pid = client_pid()
        (DATA / "batch-probe.txt").write_text(probe_mode + "\n")
        done = DATA / "batch-probe-done.txt"
        alive = True
        for _ in range(
            180
        ):  # the probe looks every 30 frames, and a loaded machine is slow
            time.sleep(1)
            if proc.poll() is not None:
                alive = False
                break
            if done.exists():
                break
        if alive:  # a few more frames with the body back in the world
            time.sleep(5)
            alive = proc.poll() is None
        report = done.read_text().strip() if done.exists() else "(no report)"
        log = game_log()
        skipped = [l.strip() for l in log.splitlines() if "Havok batch removal" in l]
        print(f"mode={mode} probe={probe_mode} alive={alive} probe report: {report}")
        for l in skipped:
            print("log:", l)
        if mode == "off":
            core = crash_site(pid) if not alive else ""
            frames = [
                l.strip() for l in core.splitlines() if re.match(r"\s*#[0-3] ", l)
            ]
            for l in frames:
                print("core:", l)
            if not core:
                print("core: no core dump to read, judging by the client's death alone")
            at_site = CRASH_SITE in core
            result = "PASS" if not alive and (at_site or not core) else "FAIL"
            print(
                f"{result}: without Bugfixes the client dies"
                + (f" at {CRASH_SITE}" if at_site else "")
            )
        else:
            ok = (
                alive
                and "body left the world: True" in report
                and "back in the world: True" in report
                and skipped
            )
            result = "PASS" if ok else "FAIL"
            print(
                f"{result}: with Bugfixes the client goes on and the stray batch entry is skipped"
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
        (HERE / f"game-{mode}-{probe_mode}.log").write_text(game_log())
    sys.exit(0 if result == "PASS" else 1)


if __name__ == "__main__":
    main()
