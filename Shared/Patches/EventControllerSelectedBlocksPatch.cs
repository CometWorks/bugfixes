using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Shared.Plugin;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.ObjectBuilder;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace Shared.Patches;

// Client + Server
//
// Keeps an event controller whose saved block selection lists a block twice from crashing the
// game. See Docs/event-controller-selected-blocks.md.
//
// MyEventControllerBlock.Init keeps the SelectedBlocks entity id list of its object builder as
// m_selectedBlockIds. ProcessSelectedBlocks, run every 10th frame after Init and every 100th
// while some blocks are still missing, looks each id up and calls m_selectedBlocks.Add for the
// blocks it finds. That Add throws ArgumentException on the main thread when the list holds an
// id twice, or holds an id that m_selectedBlocks already has (the AddBlocks event adds blocks
// without taking their ids off the pending list). The client or server stops (DIS-0075).
//
// The prefix drops the repeated ids and the ids already selected from the pending list before
// the game walks it. Those blocks stay selected once, which is what the list meant.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyEventControllerBlock), "ProcessSelectedBlocks")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class EventControllerSelectedBlocksPatch
{
    private static readonly AccessTools.FieldRef<
        MyEventControllerBlock,
        MySerializableList<long>
    > SelectedBlockIds = AccessTools.FieldRefAccess<
        MyEventControllerBlock,
        MySerializableList<long>
    >("m_selectedBlockIds");

    private static readonly AccessTools.FieldRef<
        MyEventControllerBlock,
        Dictionary<long, IMyTerminalBlock>
    > SelectedBlocks = AccessTools.FieldRefAccess<
        MyEventControllerBlock,
        Dictionary<long, IMyTerminalBlock>
    >("m_selectedBlocks");

    // Entity updates and the terminal call ProcessSelectedBlocks on the main thread only
    private static readonly HashSet<long> Seen = new();

    private static void Prefix(MyEventControllerBlock __instance)
    {
        if (Common.Config?.Enabled != true)
            return;

        var ids = SelectedBlockIds(__instance);
        if (ids == null || ids.Count == 0)
            return;

        var selected = SelectedBlocks(__instance);
        Seen.Clear();
        var removed = ids.RemoveAll(id => !Seen.Add(id) || selected.ContainsKey(id));
        Seen.Clear();

        if (removed != 0)
            Common.Logger.Info(
                "EventControllerSelectedBlocksPatch: dropped {0} repeated block id(s) from the selection of event controller {1} on grid {2}",
                removed,
                __instance.EntityId,
                __instance.CubeGrid?.DisplayName
            );
    }
}
