using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Shared.Plugin;

namespace Shared.Patches;

// Client + Server
//
// Keeps the entity creation thread from changing a loaded grid's set of mechanical blocks waiting
// on attach. See Docs/mechanical-reattach-thread.md.
//
// A client initializes the grids the server streams to it on MyEntityCreationThread, while the
// game goes on updating on the main thread. MyAttachableTopBlockBase.Init looks up its rotor,
// hinge or piston base by ParentEntityId, which also finds a base already in the world, and calls
// its MarkForReattach. That sets NeedReattach, whose setter adds the base to its grid's
// BlocksWaitingOnAttachUpdate. The main thread changes the same HashSet in the bases'
// UpdateOnceBeforeFrame (UpdateAttachState, Reattach). Two threads changing a HashSet at once
// corrupt it, and the next Add throws a NullReferenceException in HashSet.AddIfNotPresent
// (SE-1822, se-performance-improvements issue 161).
//
// The prefix runs MarkForReattach on the update thread: a call from another thread is queued with
// MySandboxGame.Invoke and done at the start of the next update. The top block's grid is not in
// the world before that anyway, so the base cannot attach it any sooner.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(
    typeof(MyMechanicalConnectionBlockBase),
    nameof(MyMechanicalConnectionBlockBase.MarkForReattach)
)]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class MechanicalReattachThreadPatch
{
    private static bool Prefix(MyMechanicalConnectionBlockBase __instance)
    {
        var game = MySandboxGame.Static;
        if (
            game == null
            || Thread.CurrentThread == game.UpdateThread
            || Common.Config?.Enabled == false
        )
            return true;

        game.Invoke(
            () =>
            {
                if (!__instance.MarkedForClose)
                    __instance.MarkForReattach();
            },
            "Bugfixes: MarkForReattach"
        );
        return false;
    }
}
