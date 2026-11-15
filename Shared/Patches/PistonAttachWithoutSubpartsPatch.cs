using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Shared.Plugin;

namespace Shared.Patches;

// Client + Server
//
// Keeps a piston base without its subparts from crashing the game when it attaches its top. See
// Docs/piston-attach-without-subparts.md.
//
// MyPistonBase finds its moving subparts (PistonSubpart1 > 2 > 3) on its current model. A base
// that is not fully built shows a construction stage model, and a model that failed to load is
// Error.mwm; neither has the subparts, so Subpart3 stays null. When such a base has a top to
// attach, as one streamed to a joining client or loaded from a save does, Attach goes on to
// CreateConstraint > FillFixedData > GetTopMatrixLocal, which reads Subpart3.WorldMatrix and
// throws a NullReferenceException (SE-1587).
//
// The prefix refuses the attach while Subpart3 is null. The base keeps the top's id and retries
// every frame, as it does while the top grid is not in the world yet, so the top attaches once the
// base is welded up and its full model brings the subparts.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyPistonBase), "Attach")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class PistonAttachWithoutSubpartsPatch
{
    private static bool Prefix(MyPistonBase __instance, ref bool __result)
    {
        if (__instance.Subpart3 != null || Common.Config?.Enabled == false)
            return true;

        __result = false;
        return false;
    }
}
