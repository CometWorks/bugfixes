using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Debris;
using Shared.Plugin;
using VRage;
using VRage.Game.Entity;

namespace Shared.Patches;

// Client + Server
//
// Gives a debris piece a new entity id when its id is already taken, instead of crashing
// with a DuplicateIdException. See Docs/debris-entity-id.md.
//
// MyDebris.CreateDirectedDebris builds the debris pieces on a worker thread
// (MyCreateDebrisWork.DoWork), where each piece allocates a random entity id into the
// worker's own registry, which is then thrown away. OnWorkCompleted registers the pieces on
// the main thread with AddEntityWithId, which throws when the id is already there. Nothing
// checks it in between, so any entity that holds the same id takes the game down. A player
// hit it with an interior light (DIS-0013).
//
// Debris is local to each game instance: it is never saved, replicated or looked up by id,
// so a different id changes nothing else. The id is written to the field: the EntityId
// setter would move the registration of the old id, which belongs to the other entity.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class DebrisEntityIdPatch
{
    private static readonly AccessTools.FieldRef<MyEntity, long> EntityIdField =
        AccessTools.FieldRefAccess<MyEntity, long>("m_entityId");

    private static FieldInfo piecesField;
    private static FieldInfo pieceObjectField;

    private static MethodBase TargetMethod()
    {
        var work = AccessTools.Inner(typeof(MyDebris), "MyCreateDebrisWork");
        piecesField = AccessTools.Field(work, "m_pieces");
        pieceObjectField = AccessTools.Field(AccessTools.Inner(work, "DebrisData"), "Object");
        return AccessTools.Method(work, "OnWorkCompleted");
    }

    private static void Prefix(object __instance)
    {
        if (Common.Config?.Enabled != true)
            return;

        foreach (var piece in (IList)piecesField.GetValue(__instance))
        {
            var debris = (MyEntity)pieceObjectField.GetValue(piece);
            var id = debris.EntityId;
            if (!MyEntityIdentifier.ExistsById(id))
                continue;

            do EntityIdField(debris) = MyEntityIdentifier.AllocateId();
            while (MyEntityIdentifier.ExistsById(debris.EntityId));

            Common.Logger.Info(
                "Debris entity id {0} was already taken, the debris got {1} instead",
                id,
                debris.EntityId
            );
        }
    }
}
