using System;
using System.IO;
using System.Linq;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace VoiceProbe;

// Test only, for tests/voice-chat-position/rig.py. "go" in voice-probe.txt in the user data
// folder starts a run. The probe plays voice chat audio (silence) through a 3D emitter on the
// local character, as MyVoiceChatSessionComponent.PlayVoice does for another player, then:
//   start   the character 20 m from the camera
//   moved   the character 60 m from the camera, another sentence played
//   seated  the character put into a cockpit, another sentence played
// For each step it writes the distance the audio engine has for the voice (DistanceToListener)
// and the real distance of the character from the camera into voice-probe-done.txt.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const int Wait = 90;
    private static readonly byte[] Sentence = new byte[24000]; // 0.5 s of 24 kHz 16-bit mono

    private int frame;
    private int step = -1;
    private int stepFrame;
    private MyEntity3DSoundEmitter emitter;
    private Vector3D camera;
    private MyCockpit seat;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        ++frame;
        if (MySession.Static?.LocalCharacter == null)
            return;

        if (step < 0)
        {
            if (frame % 30 != 0)
                return;
            var request = Path.Combine(MyFileSystem.UserDataPath, "voice-probe.txt");
            if (!File.Exists(request))
                return;
            File.Delete(request);
            step = 0;
            stepFrame = frame;
        }

        if (frame - stepFrame < Wait)
            return;
        stepFrame = frame;

        try
        {
            Step();
        }
        catch (Exception e)
        {
            Done($"error: {e}");
            step = -1;
        }
    }

    private void Step()
    {
        var character = MySession.Static.LocalCharacter;
        switch (step++)
        {
            case 0:
                if (character.Parent is MyCockpit spawnSeat)
                    ((IMyCockpit)spawnSeat).RemovePilot();
                Done(
                    $"audio {MyAudio.Static?.GetType().Name}, voice chat {MyAudio.Static?.EnableVoiceChat}"
                );
                break;
            case 1:
                camera = character.PositionComp.GetPosition();
                seat = MyEntities
                    .GetEntities()
                    .OfType<MyCubeGrid>()
                    .SelectMany(g => g.GetFatBlocks().OfType<MyCockpit>())
                    .Where(c => c.Pilot == null && c.BlockDefinition.EnableShipControl)
                    .OrderBy(c => Vector3D.Distance(c.PositionComp.GetPosition(), camera))
                    .FirstOrDefault();
                Done(
                    $"camera {Fmt(camera)}, seat {seat?.DisplayNameText} at {Fmt(seat?.PositionComp.GetPosition() ?? Vector3D.Zero)}"
                );
                HoldCamera();
                character.PositionComp.SetPosition(camera + new Vector3D(20, 0, 0));
                break;
            case 2:
                emitter = new MyEntity3DSoundEmitter(character);
                emitter.PlaySound(Sentence, 1f, 100f);
                break;
            case 3:
                Report("start");
                character.PositionComp.SetPosition(camera + new Vector3D(0, 0, 60));
                break;
            case 4:
                emitter.PlaySound(Sentence, 1f, 100f);
                break;
            case 5:
                Report("moved");
                if (seat == null)
                {
                    Done("finished, no seat");
                    step = -1;
                    return;
                }
                ((IMyCockpit)seat).AttachPilot(character);
                break;
            case 6:
                HoldCamera(); // the seat took the camera
                break;
            case 7:
                emitter.PlaySound(Sentence, 1f, 100f);
                break;
            case 8:
                Report("seated");
                Done("finished");
                step = -1;
                break;
        }
    }

    // The listener is the camera: keep it still in spectator mode while the speaker moves
    private void HoldCamera()
    {
        MySession.Static.SetCameraController(MyCameraControllerEnum.Spectator, null, camera);
        MySpectatorCameraController.Static.Position = camera;
    }

    private void Report(string name)
    {
        var character = MySession.Static.LocalCharacter;
        var camNow = MySector.MainCamera.Position;
        var real = Vector3D.Distance(character.PositionComp.WorldAABB.Center, camNow);
        var voice = emitter.Sound;
        // MySourceVoice is internal to VRage.Audio
        var heard =
            voice?.GetType().GetProperty("DistanceToListener")?.GetValue(voice) as float?
            ?? float.NaN;
        Done(
            FormattableString.Invariant(
                $"{name}: heard {heard:F1} m, real {real:F1} m, character {Fmt(character.PositionComp.GetPosition())}, camera {Fmt(camNow)}, in scene {character.InScene}, seated {character.Parent is MyCockpit}, playing {voice?.IsPlaying}"
            )
        );
    }

    private static string Fmt(Vector3D v) =>
        FormattableString.Invariant($"({v.X:F1}, {v.Y:F1}, {v.Z:F1})");

    private static void Done(string line)
    {
        MyLog.Default.WriteLineAndConsole($"VoiceProbe: {line}");
        File.AppendAllText(
            Path.Combine(MyFileSystem.UserDataPath, "voice-probe-done.txt"),
            line + "\n"
        );
    }
}
