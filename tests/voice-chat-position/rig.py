#!/usr/bin/env python3
"""Regression test of Docs/voice-chat-position.md: voice chat heard from where the speaker first spoke.

Usage: rig.py on|off   (Bugfixes from this checkout enabled or not)

An offline headless client with audio (no --no-render: LinuxCompat turns audio off with it) (--audio-file) in its own Pulsar folder
(~/.se-test/SE-1805-c0, made with testing/pulsar-dev-instances/new-pulsar-instance.sh from
$SE1805_PULSAR_TEMPLATE, default ~/.config/Pulsar) loads RemoteAPITestSpace. The probe plugin in
probe/ plays voice chat audio from the local character 20 m from the camera, moves the character
to 60 m and plays again, then seats it in a cockpit and plays again. After each step it compares
the distance the audio engine uses for the voice with the character's real distance.

off: the voice must stay at the first distance (the bug).
on:  the voice must follow the character, within 2 m, in all three steps.

The Remote plugin comes from $SE1805_REMOTE (a CometWorks/remote checkout that builds against the
current game). Ports come from the workspace test slot of SE-1805, the client starts under
run-test.sh (RAM guard). Run it with the Python of the se-remote skill. Prints a PASS or FAIL line,
exits 0 on PASS.
"""

import glob, os, re, shutil, signal, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path.home() / ".claude/skills/se-remote"))
from se_remote import RemoteAPI  # noqa: E402

HOME = Path.home()
TASK = "SE-1805"
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
TEMPLATE = Path(os.environ.get("SE1805_PULSAR_TEMPLATE", HOME / ".config/Pulsar"))
REMOTE = Path(os.environ.get("SE1805_REMOTE", HOME / "ws/se/se1/plugins/remote"))
PORT = 25000 + 10 * SLOT
CLIENT_ID = str(76561199500010000 + 10 * SLOT)
HERE = Path(__file__).resolve().parent
BUGFIXES = HERE.parents[1]
BIN64 = Path(
    os.environ.get(
        "SE1805_BIN64", HOME / ".steam/steam/steamapps/common/SpaceEngineers/Bin64"
    )
)
WORLD_SRC = Path(
    os.environ.get(
        "SE1805_WORLD",
        HOME / ".config/SpaceEngineers/Saves/1000002/RemoteAPITestSpace",
    )
)
TOLERANCE = 2.0

PROFILE = """<?xml version="1.0" encoding="utf-8"?>
<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Name>Current</Name>
  <GitHub />
  <DevFolder>
    <LocalFolderConfig><Id>remote</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
    <LocalFolderConfig><Id>voice-probe</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>
{bugfixes}  </DevFolder>
  <Local />
  <Mods />
</Profile>
"""


def local_plugin(name, folder, file):
    return (
        f"    <LocalPlugin>\n      <Name>{name}</Name>\n      <Folder>{folder}</Folder>\n"
        f"      <File>{file}</File>\n      <Enabled>true</Enabled>\n    </LocalPlugin>\n"
    )


def setup(enabled):
    if not PULSAR.exists():
        subprocess.run(
            [
                str(TESTING / "pulsar-dev-instances/new-pulsar-instance.sh"),
                str(PULSAR),
                str(TEMPLATE),
            ],
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
    for name in ("remote", "bugfixes", "voice-probe"):
        text = re.sub(
            rf" *<LocalPlugin>\s*<Name>{name}</Name>.*?</LocalPlugin>\n",
            "",
            text,
            flags=re.S,
        )
    text = text.replace(
        "<LocalPluginSources />", "<LocalPluginSources>\n</LocalPluginSources>"
    )
    text = text.replace(
        "<LocalPluginSources>\n",
        "<LocalPluginSources>\n"
        + local_plugin("remote", REMOTE, "Remote.xml")
        + local_plugin("bugfixes", BUGFIXES, "BugfixesClient.xml")
        + local_plugin("voice-probe", HERE / "probe", "VoiceProbe.xml"),
        1,
    )
    src.write_text(text)
    DATA.mkdir(parents=True, exist_ok=True)
    (DATA / "Remote.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n  <Enabled>true</Enabled>\n  <ListenIP>127.0.0.1</ListenIP>\n'
        f"  <ListenPort>{PORT}</ListenPort>\n  <AdminPassword>SpaceEngineers</AdminPassword>\n</PluginConfig>\n"
    )
    set_client_cfg()
    world = DATA / "Saves" / CLIENT_ID / "SE1805"
    if world.exists():
        shutil.rmtree(world)
    shutil.copytree(WORLD_SRC, world)
    for f in glob.glob(str(DATA / "SpaceEngineers*.log")) + glob.glob(
        str(DATA / "voice-probe*.txt")
    ):
        os.remove(f)
    return world


CFG_KEYS = {
    "ExperimentalMode": "True",  # RemoteAPITestSpace is an experimental world
    "FirstTimeRun": "False",
    "WelcomeScreenCurrentStatus": "1",
    "GDPRConsent": "False",
    "GDPRConsentSentUpdated": "True",
    "GoodBotHints": "False",
}


def set_client_cfg():
    """The keys that let the world load without a dialog, in the client's own SpaceEngineers.cfg."""
    cfg = DATA / "SpaceEngineers.cfg"
    if not cfg.exists():
        shutil.copy(HOME / ".config/SpaceEngineers/SpaceEngineers.cfg", cfg)
    text = cfg.read_text()
    for key, value in CFG_KEYS.items():
        item = re.compile(
            rf"(<Key>{key}</Key>\s*<Value>\s*<Value xsi:type=\"xsd:string\">)[^<]*(</Value>)"
        )
        if item.search(text):
            text = item.sub(rf"\g<1>{value}\g<2>", text)
        else:
            text = text.replace(
                "</dictionary>",
                f"  <item>\n        <Key>{key}</Key>\n        <Value>\n"
                f'          <Value xsi:type="xsd:string">{value}</Value>\n'
                "        </Value>\n      </item>\n    </dictionary>",
                1,
            )
    cfg.write_text(text)


def game_log():
    logs = sorted(glob.glob(str(DATA / "SpaceEngineers*.log")), key=os.path.getmtime)
    return Path(logs[-1]).read_text(errors="replace") if logs else ""


def parse(report):
    """{step: (heard, real)} from the probe's report lines."""
    steps = {}
    for m in re.finditer(
        r"^(\w+): heard ([\d.]+|NaN) m, real ([\d.]+) m", report, re.M
    ):
        steps[m.group(1)] = (float(m.group(2)), float(m.group(3)))
    return steps


def main():
    mode = sys.argv[1]
    world = setup(mode == "on")
    audio = DATA / "voice.wav"
    argv = [
        str(TESTING / "parallel-test-work/run-test.sh"),
        "6",
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
        "--quality",
        "minimal",
        "--resolution",
        "640x480",
        "--audio-file",
        str(audio),
        "--client-id",
        CLIENT_ID,
        "--client-name",
        "se1805",
    ]
    launcher_log = HERE / f"launcher-{mode}.log"
    env = dict(os.environ)
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
        (DATA / "voice-probe.txt").write_text("go\n")
        done = DATA / "voice-probe-done.txt"
        for _ in range(400):  # 9 steps of 90 frames, slower on a loaded machine
            time.sleep(1)
            if proc.poll() is not None:
                break
            text = done.read_text() if done.exists() else ""
            if "finished" in text or "error:" in text:
                break
        report = done.read_text().strip() if done.exists() else "(no report)"
        print(f"mode={mode}")
        for line in report.splitlines():
            print("probe:", line)
        steps = parse(report)
        heard = [v[0] for v in steps.values()]
        if len(steps) < 3 or any(h != h for h in heard):
            print("FAIL: the probe did not report the voice in every step")
        elif steps["moved"][1] < steps["start"][1] + 20:
            print("FAIL: the camera moved with the character, nothing to compare")
        elif mode == "off":
            start = steps["start"][0]
            stale = [
                s
                for s, (heard, real) in steps.items()
                if s != "start"
                and abs(heard - start) < TOLERANCE
                and abs(real - start) > 10
            ]
            result = "PASS" if len(stale) == len(steps) - 1 else "FAIL"
            print(
                f"{result}: without Bugfixes the voice stays {start:.1f} m away after the speaker moves ({', '.join(stale)})"
            )
        else:
            off = {s: v for s, v in steps.items() if abs(v[0] - v[1]) > TOLERANCE}
            result = "PASS" if not off and "seated" in steps else "FAIL"
            print(
                f"{result}: with Bugfixes the voice follows the speaker in {', '.join(steps)}"
                + (f"; off in {off}" if off else "")
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
        (HERE / f"game-{mode}.log").write_text(game_log())
        if audio.exists():
            audio.unlink()
    sys.exit(0 if result == "PASS" else 1)


if __name__ == "__main__":
    main()
