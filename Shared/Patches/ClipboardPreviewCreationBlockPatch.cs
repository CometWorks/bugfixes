using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Shared.Plugin;
using VRage;
using VRage.Utils;

namespace Shared.Patches;

// Client + Server
//
// Closes the entity creation block that MyGridClipboard.ChangeClipboardPreview leaves open
// when it gives up on a preview. See Docs/clipboard-preview-creation-block.md.
//
// ChangeClipboardPreview builds the preview grids of a clipboard or projector inside an
// entity creation block: LazyInitPerThreadStorage gives the worker thread its own entity id
// registry, and at the end GetPerThreadEntities and ClearPerThreadEntities hand the new
// entities over and empty it. When a copied grid comes out with no blocks (all of them from
// a missing mod, for example) or is not a grid at all, the method hides the preview with a
// recursive call and returns from the middle of the block. The registry keeps every entity
// of the grids built so far, and InEntityCreationBlock stays true on that thread.
//
// The next creation block that runs on the same worker collects those entities as its own.
// MyForageableEnvironmentProxy casts everything it collects to MyForageableEntity, so a
// projector whose blueprint has such a grid crashes the server with an InvalidCastException
// (SE1-0052). MyCreateDebrisWork just drops them, and prefab spawns register them as if they
// were part of the new prefab.
//
// The finalizer empties the registry and closes the block when this call opened it and left
// it open. The entities belong to a preview that has just been hidden, and the game registers
// preview entities only for a visible preview. On the main thread the "per-thread" registry is
// the one holding every entity of the world, so it is left alone there.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyGridClipboard), "ChangeClipboardPreview")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ClipboardPreviewCreationBlockPatch
{
    private static void Prefix(out bool __state)
    {
        __state = MyEntityIdentifier.InEntityCreationBlock;
    }

    private static void Finalizer(bool __state)
    {
        if (Common.Config?.Enabled != true)
            return;

        if (__state || !MyEntityIdentifier.InEntityCreationBlock)
            return;

        if (Thread.CurrentThread == MyUtils.MainThread)
            return;

        MyEntityIdentifier.ClearPerThreadEntities();
        MyEntityIdentifier.InEntityCreationBlock = false;
    }
}
