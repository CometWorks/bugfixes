using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Shared.Plugin;
using VRage.Render11.GeometryStage.Voxel;
using VRageRender;

[assembly: IgnoresAccessChecksTo("VRage.Render11")]

namespace ClientPlugin.Patches;

// Fixes the render thread freezing the game for up to a minute after loading or respawning
// next to a planet, worst at the "photo mode" voxel quality.
//
// MeshId.GetHashCode returns MeshId.Comparer.GetHashCode(), which is the hash of the static
// comparer object, not of the mesh, so every MeshId hashes to the same value. The comparers
// of the MyMeshes indexes build on it: MyMeshPart and MyMeshSection shift that constant left
// and OR in the LOD and the part (the section name's hash), and MyLodMesh's comparer takes the
// comparer object's hash directly and truncates it to zero. Every voxel cell and model part
// therefore lands in a handful of buckets of PartIndex, VoxelPartIndex and LodMeshIndex
// (SectionIndex less so, the section names spread it), and each lookup walks a collision
// chain as long as the number of meshes. The cost grows with the
// square of the number of voxel cells: joining a server at photo quality spent 29 s and 66 s
// in MyMeshes.ResizeVoxelParts on the render thread while the game waited for it (SE1-0054).
// MeshVoxelInfo and InterSessionData are keyed with MeshId.Comparer, which hashes the index
// correctly, and are not affected.
//
// Present on Windows: platform independent game code, settled against the IL of
// VRage.Render11 1.210.014.
//
// The game's own hash methods are left alone: patching MeshId.GetHashCode at runtime crashed
// the client with a KeyNotFoundException in SectionIndex, because entries stored under the old
// hash no longer matched. Instead, on the first frame the render thread draws, the four
// indexes get comparers that hash the mesh index, and their entries are added back under the
// new hashes. Equality is unchanged.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyRenderVoxelActor), nameof(MyRenderVoxelActor.UpdateQueued))]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class MeshIndexHashPatch
{
    private static bool done;

    // Runs on the render thread, which owns these indexes
    private static void Prefix()
    {
        if (done || Common.Config?.Enabled != true)
            return;
        done = true;

        var replaced =
            Replace(MyMeshes.PartIndex, new MeshPartComparer())
            && Replace(MyMeshes.VoxelPartIndex, new MeshPartComparer())
            && Replace(MyMeshes.LodMeshIndex, new LodMeshComparer())
            && Replace(MyMeshes.SectionIndex, new MeshSectionComparer());

        if (replaced)
            Common.Logger.Info("Replaced the comparers of the mesh indexes");
        else
            Common.Logger.Warning("Could not replace the comparers of the mesh indexes");
    }

    private static bool Replace<TKey, TValue>(
        Dictionary<TKey, TValue> index,
        IEqualityComparer<TKey> comparer
    )
    {
        // "_comparer" on .NET, "comparer" on .NET Framework
        var field =
            AccessTools.Field(index.GetType(), "_comparer")
            ?? AccessTools.Field(index.GetType(), "comparer");
        if (field == null)
            return false;

        var entries = index.ToArray();
        index.Clear();
        field.SetValue(index, comparer);
        foreach (var entry in entries)
            index.Add(entry.Key, entry.Value);
        return true;
    }

    private class MeshPartComparer : IEqualityComparer<MyMeshPart>
    {
        public bool Equals(MyMeshPart x, MyMeshPart y) =>
            x.Mesh.Index == y.Mesh.Index && x.Lod == y.Lod && x.Part == y.Part;

        public int GetHashCode(MyMeshPart p) =>
            unchecked((p.Mesh.Index * 397 + p.Lod) * 397 + p.Part);
    }

    private class LodMeshComparer : IEqualityComparer<MyLodMesh>
    {
        public bool Equals(MyLodMesh x, MyLodMesh y) =>
            x.Mesh.Index == y.Mesh.Index && x.Lod == y.Lod;

        public int GetHashCode(MyLodMesh m) => unchecked(m.Mesh.Index * 397 + m.Lod);
    }

    private class MeshSectionComparer : IEqualityComparer<MyMeshSection>
    {
        public bool Equals(MyMeshSection x, MyMeshSection y) =>
            x.Mesh.Index == y.Mesh.Index && x.Lod == y.Lod && x.Section == y.Section;

        public int GetHashCode(MyMeshSection s) =>
            unchecked((s.Mesh.Index * 397 + s.Lod) * 397 ^ (s.Section?.GetHashCode() ?? 0));
    }
}
