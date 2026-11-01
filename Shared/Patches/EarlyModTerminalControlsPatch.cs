using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Shared.Plugin;
using VRage.Collections;

namespace Shared.Patches;

// Client + Server
//
// Creates a block type's own terminal controls and actions on the first initialized block even
// when a mod or plugin accessed MyAPIGateway.TerminalControls for that type or a base type
// before any block of that type was initialized. See Docs/early-mod-terminal-controls.md.
//
// Each MyTerminalBlock class creates its terminal controls and toolbar actions in its virtual
// CreateTerminalControls method, called from MyTerminalBlock.BeforeGameLogicInit when the first
// block of that type initializes, and guarded by MyTerminalControlFactory.AreControlsCreated,
// which checks whether MyTerminalControlFactory.m_controls has an entry for the block type.
//
// When a mod calls MyAPIGateway.TerminalControls.AddControl, GetControls, AddAction or
// GetActions from a session component's LoadData or BeforeStart (or on a multiplayer client
// before entities stream in), MyTerminalControlFactory.GetList creates the m_controls entry for
// the target type before any block entity exists. MyTerminalControls.CreateControl and
// MyTerminalControlFactory.AddBaseClass try to run the block's own CreateTerminalControls first
// through EnsureControlsAreCreated, but both look for a non-public static CreateTerminalControls
// method, whereas CreateTerminalControls is a protected virtual instance method on
// MyTerminalBlock and its subclasses. The lookup finds nothing, and when the first block entity
// later runs BeforeGameLogicInit, AreControlsCreated returns true and CreateTerminalControls
// returns without creating the block's vanilla controls or actions.
//
// If a mod targets IMyTerminalBlock or IMyFunctionalBlock early, every block in the world loses
// its shared base controls (Name, On/Off, Show in terminal, Custom Data). If a mod targets a
// derived block type early (such as IMyTimerBlock, IMyEventControllerBlock or IMyConveyorSorter),
// that block type loses its own controls and toolbar actions.
//
// The transpiler routes BeforeGameLogicInit's CreateTerminalControls call (inside
// m_createControlsLock) through a helper that stashes any controls and actions added to
// m_controls before the block's own CreateTerminalControls has run, removes the premature
// m_controls entries so every uninitialized class in the inheritance chain runs its
// CreateTerminalControls, and then appends the stashed mod controls and actions after the
// vanilla ones.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyTerminalBlock), "BeforeGameLogicInit")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class EarlyModTerminalControlsPatch
{
    private static readonly Action<MyTerminalBlock> CreateBlockTerminalControls =
        AccessTools.MethodDelegate<Action<MyTerminalBlock>>(
            AccessTools.Method(typeof(MyTerminalBlock), "CreateTerminalControls")
        );

    private static readonly IDictionary ControlsDict = (IDictionary)
        AccessTools.Field(typeof(MyTerminalControlFactory), "m_controls")?.GetValue(null);

    private static readonly MethodInfo GetListMethod = AccessTools.Method(
        typeof(MyTerminalControlFactory),
        "GetList",
        new[] { typeof(Type) }
    );

    private static readonly Type BlockDataType = AccessTools.Inner(
        typeof(MyTerminalControlFactory),
        "BlockData"
    );

    private static readonly FieldInfo ControlsField = AccessTools.Field(BlockDataType, "Controls");
    private static readonly FieldInfo ActionsField = AccessTools.Field(BlockDataType, "Actions");

    // Tracks block types whose own CreateTerminalControls has executed in the current session.
    // Guarded by MyTerminalBlock.m_createControlsLock, which BeforeGameLogicInit holds around
    // the CreateTerminalControls call.
    private static readonly HashSet<Type> InitializedTypes = new();

    private struct SavedBlockControls
    {
        public Type BlockType;
        public bool HadEntry;
        public List<ITerminalControl> Controls;
        public List<ITerminalAction> Actions;
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var target = AccessTools.Method(typeof(MyTerminalBlock), "CreateTerminalControls");
        var replacement = AccessTools.Method(
            typeof(EarlyModTerminalControlsPatch),
            nameof(CreateTerminalControls)
        );
        var il = instructions.ToList();
        var replaced = 0;
        foreach (var instruction in il.Where(i => i.Calls(target)))
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "EarlyModTerminalControlsPatch: expected one CreateTerminalControls call in MyTerminalBlock.BeforeGameLogicInit, replaced {0}",
                replaced
            );

        return il;
    }

    private static void CreateTerminalControls(MyTerminalBlock block)
    {
        if (
            Common.Config?.Enabled != true
            || block == null
            || ControlsDict == null
            || GetListMethod == null
            || ControlsField == null
            || ActionsField == null
        )
        {
            CreateBlockTerminalControls(block);
            return;
        }

        // MyTerminalControlFactory.Unload is tiny and gets inlined into MySession.Unload.
        // Detect session unload by checking whether MyTerminalBlock is absent from m_controls.
        if (InitializedTypes.Count > 0 && !ControlsDict.Contains(typeof(MyTerminalBlock)))
            InitializedTypes.Clear();

        var concreteType = block.GetType();
        if (InitializedTypes.Contains(concreteType))
        {
            CreateBlockTerminalControls(block);
            return;
        }

        var chain = new List<Type>(4);
        for (
            var t = concreteType;
            t != null && typeof(MyTerminalBlock).IsAssignableFrom(t);
            t = t.BaseType
        )
        {
            if (!InitializedTypes.Contains(t))
                chain.Add(t);
        }

        if (chain.Count == 0)
        {
            CreateBlockTerminalControls(block);
            return;
        }

        // Walk from MyTerminalBlock down to the concrete block type.
        chain.Reverse();

        var ancestorControls = new HashSet<ITerminalControl>();
        var ancestorActions = new HashSet<ITerminalAction>();
        for (
            var t = chain[0].BaseType;
            t != null && typeof(MyTerminalBlock).IsAssignableFrom(t);
            t = t.BaseType
        )
        {
            if (!ControlsDict.Contains(t))
                continue;

            var data = ControlsDict[t];
            if (ControlsField.GetValue(data) is MyUniqueList<ITerminalControl> ctrls)
            {
                foreach (var c in ctrls.Items)
                    ancestorControls.Add(c);
            }

            if (ActionsField.GetValue(data) is MyUniqueList<ITerminalAction> acts)
            {
                foreach (var a in acts.Items)
                    ancestorActions.Add(a);
            }
        }

        var saved = new List<SavedBlockControls>(chain.Count);
        foreach (var t in chain)
        {
            var item = new SavedBlockControls { BlockType = t };
            if (ControlsDict.Contains(t))
            {
                item.HadEntry = true;
                var data = ControlsDict[t];
                if (ControlsField.GetValue(data) is MyUniqueList<ITerminalControl> ctrls)
                {
                    foreach (var c in ctrls.Items)
                    {
                        if (ancestorControls.Add(c))
                        {
                            item.Controls ??= new List<ITerminalControl>();
                            item.Controls.Add(c);
                        }
                    }
                }

                if (ActionsField.GetValue(data) is MyUniqueList<ITerminalAction> acts)
                {
                    foreach (var a in acts.Items)
                    {
                        if (ancestorActions.Add(a))
                        {
                            item.Actions ??= new List<ITerminalAction>();
                            item.Actions.Add(a);
                        }
                    }
                }

                ControlsDict.Remove(t);
            }

            saved.Add(item);
        }

        try
        {
            CreateBlockTerminalControls(block);
        }
        finally
        {
            foreach (var item in saved)
            {
                InitializedTypes.Add(item.BlockType);
                if (!item.HadEntry)
                    continue;

                var hasControls = item.Controls is { Count: > 0 };
                var hasActions = item.Actions is { Count: > 0 };
                if (hasControls || hasActions)
                {
                    var blockData = GetListMethod.Invoke(null, new object[] { item.BlockType });
                    AppendModItems(blockData, item.Controls, item.Actions);

                    foreach (DictionaryEntry entry in ControlsDict)
                    {
                        if (
                            entry.Key is Type subType
                            && subType != item.BlockType
                            && item.BlockType.IsAssignableFrom(subType)
                        )
                            AppendModItems(entry.Value, item.Controls, item.Actions);
                    }
                }

                Common.Logger.Info(
                    "Created vanilla terminal controls for {0} after a mod or plugin accessed its controls early (merged {1} mod control(s) and {2} action(s))",
                    item.BlockType.Name,
                    item.Controls?.Count ?? 0,
                    item.Actions?.Count ?? 0
                );
            }
        }
    }

    private static void AppendModItems(
        object blockData,
        List<ITerminalControl> controls,
        List<ITerminalAction> actions
    )
    {
        if (blockData == null)
            return;

        if (
            controls != null
            && ControlsField.GetValue(blockData) is MyUniqueList<ITerminalControl> targetControls
        )
        {
            foreach (var c in controls)
                targetControls.Add(c);
        }

        if (
            actions != null
            && ActionsField.GetValue(blockData) is MyUniqueList<ITerminalAction> targetActions
        )
        {
            foreach (var a in actions)
                targetActions.Add(a);
        }
    }
}
