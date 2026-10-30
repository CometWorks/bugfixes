using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using Shared.Plugin;
using SpaceEngineers.Game.Entities.Blocks;

namespace Shared.Patches;

// Client + Server
//
// Keeps a sound block saved with a looping sound playing from crashing a dedicated server when
// the block gets power. See Docs/sound-block-emitter.md.
//
// MySoundBlock.Init reads IsPlaying and IsLoopableSound from the object builder and subscribes the
// block to per frame updates, but creates m_soundEmitter only when not on a dedicated server. A
// working block then reaches UpdateLoopableSoundEmitter (marked "Never runs on DS" in the game's
// source), which runs its loop timer and ends with m_soundEmitter.Update(). That is an unhandled
// NullReferenceException on the main thread, which stops the server. Any world or prefab holding
// such a block does it, like the WLYC-Oberon-Nuclear-Generating-Station encounter (SE-787).
//
// The transpiler updates the emitter through a method that skips a null emitter. The loop timer
// keeps running, so the block stops playing at the end of its loop period as on a client.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MySoundBlock), "UpdateLoopableSoundEmitter")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class SoundBlockEmitterPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var update = AccessTools.Method(
            typeof(MyEntity3DSoundEmitter),
            nameof(MyEntity3DSoundEmitter.Update)
        );
        var il = instructions.ToList();
        var replaced = 0;
        foreach (var instruction in il.Where(i => i.Calls(update)))
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(
                typeof(SoundBlockEmitterPatch),
                nameof(UpdateEmitter)
            );
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "SoundBlockEmitterPatch: expected one emitter update in MySoundBlock.UpdateLoopableSoundEmitter, replaced {0}",
                replaced
            );

        return il;
    }

    private static void UpdateEmitter(MyEntity3DSoundEmitter emitter)
    {
        if (emitter != null || Common.Config?.Enabled != true)
            emitter.Update(); // Disabled: throws like the game does
    }
}
