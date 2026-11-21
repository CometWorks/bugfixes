using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using Shared.Plugin;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.Utils;

namespace ClientPlugin.Patches;

// Fixes a voice chat speaker being heard from where they first spoke, not from where they are.
// See Docs/voice-chat-position.md.
//
// MyVoiceChatSessionComponent.PlayVoice plays a speaker's voice through a MyEntity3DSoundEmitter
// on their character. Its source voice is a buffered one, made by MyXAudio2.GetSound(emitter,
// dimension), which places it in 3D once, at the character's position relative to the camera.
// From then on MyXAudio2.Update3DCuesPositions should move it every frame, but
// Update3DVoicePosition looks up the voice's cue first and returns when there is none. A voice
// chat source has no cue, so its 3D position, distance attenuation and panning stay what they
// were when it was made. MyXAudio2.IsVoiceStopped never counts an idle buffered voice as
// stopped, so the source lives until the emitter is disposed (the speaker leaves, respawns or
// voice chat is turned off), and every later sentence comes from that same frozen offset to the
// camera: walk away, sit down somewhere, turn around, the voice stays where it began.
//
// Present on Windows: MyXAudio2 is platform independent game code.
//
// The transpiler swaps the cue lookup in Update3DVoicePosition for one that gives a voice without
// a cue the settings GetSound placed it with: no default maximum distance (the emitter's custom
// one applies) and a linear volume curve. The method then runs its own buffered branch. Only the
// lookup is replaced, so the plugin never touches the SharpDX types, which LinuxCompat swaps for
// its own on Linux.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyXAudio2), "Update3DVoicePosition")]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class VoiceChatPositionPatch
{
    private static readonly MySoundData NoCue = new() { VolumeCurve = MyCurveType.Linear };

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
    {
        var getCue = AccessTools.Method(typeof(MyCueBank), nameof(MyCueBank.GetCue));
        var list = code.ToList();
        var found = 0;
        foreach (var instruction in list.Where(i => i.Calls(getCue)))
        {
            instruction.opcode = System.Reflection.Emit.OpCodes.Call;
            instruction.operand = AccessTools.Method(
                typeof(VoiceChatPositionPatch),
                nameof(GetCue)
            );
            found++;
        }
        if (found != 1)
            throw new System.InvalidOperationException(
                $"VoiceChatPositionPatch: expected one MyCueBank.GetCue call, found {found}"
            );
        return list;
    }

    private static MySoundData GetCue(MyCueBank bank, MyCueId cueId) =>
        bank.GetCue(cueId)
        ?? (
            cueId.Hash == MyStringHash.NullOrEmpty && Common.Config?.Enabled == true ? NoCue : null
        );
}
