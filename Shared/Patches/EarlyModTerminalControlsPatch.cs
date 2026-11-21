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
using Sandbox.Game.World;
using Shared.Plugin;
using VRage;
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
// vanilla ones. Non-generic MyTerminalControlFactory entry points also synchronize on
// m_createControlsLock when called from parallel entity-loading threads outside
// BeforeGameLogicInit (such as MyGameLogicComponent.Init).
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

    private static readonly FastResourceLock CreateControlsLock = (FastResourceLock)
        AccessTools.Field(typeof(MyTerminalBlock), "m_createControlsLock")?.GetValue(null);

    private static readonly IDictionary ControlsDict = (IDictionary)
        AccessTools.Field(typeof(MyTerminalControlFactory), "m_controls")?.GetValue(null);

    private static readonly FastResourceLock ControlsLock = (FastResourceLock)
        AccessTools.Field(typeof(MyTerminalControlFactory), "m_controlsLock")?.GetValue(null);

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
    private static readonly HashSet<Type> InitializedTypes = new();

    // Tracks whether the current thread already holds non-reentrant CreateControlsLock.
    [ThreadStatic]
    private static int createControlsLockDepth;

    private struct SavedBlockControls
    {
        public Type BlockType;
        public bool HadEntry;
        public List<ITerminalControl> Controls;
        public List<ITerminalAction> Actions;
    }

    private static void EnterCreateControlsLock(ref bool lockTaken)
    {
        if (createControlsLockDepth > 0 || CreateControlsLock == null)
        {
            lockTaken = false;
            return;
        }

        CreateControlsLock.AcquireExclusive();
        createControlsLockDepth = 1;
        lockTaken = true;
    }

    private static void ExitCreateControlsLock(bool lockTaken)
    {
        if (!lockTaken)
            return;

        createControlsLockDepth = 0;
        CreateControlsLock.ReleaseExclusive();
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
        if (block == null)
            return;

        createControlsLockDepth++;
        try
        {
            CreateTerminalControlsCore(block);
        }
        finally
        {
            createControlsLockDepth--;
        }
    }

    private static void CreateTerminalControlsCore(MyTerminalBlock block)
    {
        if (
            CreateBlockTerminalControls == null
            || ControlsDict == null
            || ControlsLock == null
            || GetListMethod == null
            || ControlsField == null
            || ActionsField == null
        )
        {
            CreateBlockTerminalControls?.Invoke(block);
            return;
        }

        var concreteType = block.GetType();
        bool alreadyInitialized;
        lock (InitializedTypes)
        {
            alreadyInitialized = InitializedTypes.Contains(concreteType);
        }

        if (alreadyInitialized)
        {
            CreateBlockTerminalControls(block);
            return;
        }

        var chain = new List<Type>(4);
        lock (InitializedTypes)
        {
            for (
                var t = concreteType;
                t != null && typeof(MyTerminalBlock).IsAssignableFrom(t);
                t = t.BaseType
            )
            {
                if (!InitializedTypes.Contains(t))
                    chain.Add(t);
            }
        }

        // Always record the chain even when the plugin is disabled, so enabling it mid-session
        // does not re-create vanilla base controls on the next block type and duplicate them.
        if (chain.Count == 0 || Common.Config?.Enabled != true)
        {
            try
            {
                CreateBlockTerminalControls(block);
            }
            finally
            {
                lock (InitializedTypes)
                {
                    foreach (var t in chain)
                        InitializedTypes.Add(t);
                }
            }

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
            var data = TryGetBlockData(t);
            if (data == null)
                continue;

            if (ControlsField.GetValue(data) is MyUniqueList<ITerminalControl> ctrls)
            {
                foreach (var c in ctrls.Items)
                {
                    if (c != null)
                        ancestorControls.Add(c);
                }
            }

            if (ActionsField.GetValue(data) is MyUniqueList<ITerminalAction> acts)
            {
                foreach (var a in acts.Items)
                {
                    if (a != null)
                        ancestorActions.Add(a);
                }
            }
        }

        var saved = new List<SavedBlockControls>(chain.Count);
        foreach (var t in chain)
        {
            var item = new SavedBlockControls { BlockType = t };
            var data = TryGetBlockData(t);
            if (data != null)
            {
                item.HadEntry = true;
                if (ControlsField.GetValue(data) is MyUniqueList<ITerminalControl> ctrls)
                {
                    foreach (var c in ctrls.Items)
                    {
                        if (c != null && ancestorControls.Add(c))
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
                        if (a != null && ancestorActions.Add(a))
                        {
                            item.Actions ??= new List<ITerminalAction>();
                            item.Actions.Add(a);
                        }
                    }
                }

                using (ControlsLock.AcquireExclusiveUsing())
                {
                    ControlsDict.Remove(t);
                }
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
                lock (InitializedTypes)
                {
                    InitializedTypes.Add(item.BlockType);
                }

                if (!item.HadEntry)
                    continue;

                var hasControls = item.Controls is { Count: > 0 };
                var hasActions = item.Actions is { Count: > 0 };
                if (hasControls || hasActions)
                {
                    var blockData = GetListMethod.Invoke(null, new object[] { item.BlockType });
                    AppendModItems(blockData, item.Controls, item.Actions);

                    var subTargets = SnapshotMatchingSubtypeBlockData(item.BlockType);
                    if (subTargets != null)
                    {
                        foreach (var subData in subTargets)
                            AppendModItems(subData, item.Controls, item.Actions);
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

    private static object TryGetBlockData(Type blockType)
    {
        using (ControlsLock.AcquireSharedUsing())
        {
            return ControlsDict.Contains(blockType) ? ControlsDict[blockType] : null;
        }
    }

    private static List<object> SnapshotMatchingSubtypeBlockData(Type baseType)
    {
        List<object> subTargets = null;
        using (ControlsLock.AcquireSharedUsing())
        {
            foreach (DictionaryEntry entry in ControlsDict)
            {
                // MyRadioAntenna.CreateTerminalControls removes the MyTerminalBlock base
                // controls with RemoveBaseClass<MyRadioAntenna, MyTerminalBlock> and adds
                // its own replacements.
                if (
                    entry.Key is Type subType
                    && subType != baseType
                    && baseType.IsAssignableFrom(subType)
                    && !(
                        baseType == typeof(MyTerminalBlock)
                        && typeof(MyRadioAntenna).IsAssignableFrom(subType)
                    )
                    && entry.Value != null
                )
                {
                    subTargets ??= new List<object>();
                    subTargets.Add(entry.Value);
                }
            }
        }

        return subTargets;
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

    // MyEntities.Load initializes grids in parallel via Parallel.For. After a block finishes
    // BeforeGameLogicInit (releasing m_createControlsLock), its MyGameLogicComponent.Init runs on
    // the same worker thread and may call MyAPIGateway.TerminalControls while another worker
    // thread is removing/rebuilding m_controls entries inside CreateTerminalControls. Acquire
    // m_createControlsLock on non-generic MyTerminalControlFactory entry points when not already
    // held on the current thread.
    [HarmonyPatch]
    private static class FactoryConcurrencyPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = typeof(MyTerminalControlFactory).GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            );

            foreach (var m in methods)
            {
                if (m.IsGenericMethod || m.IsGenericMethodDefinition)
                    continue;

                switch (m.Name)
                {
                    case "InitializeControls":
                    case "GetList":
                    case nameof(MyTerminalControlFactory.AddControl):
                    case nameof(MyTerminalControlFactory.RemoveControl):
                    case nameof(MyTerminalControlFactory.AddAction):
                    case nameof(MyTerminalControlFactory.AddActions):
                        yield return m;
                        break;
                    case nameof(MyTerminalControlFactory.GetControls):
                    case nameof(MyTerminalControlFactory.GetValueControls):
                    case nameof(MyTerminalControlFactory.GetActions):
                        if (m.GetParameters().Length == 2)
                            yield return m;
                        break;
                }
            }
        }

        private static void Prefix(ref bool __state)
        {
            __state = false;
            if (Common.Config?.Enabled != true)
                return;

            EnterCreateControlsLock(ref __state);
        }

        private static void Finalizer(bool __state)
        {
            ExitCreateControlsLock(__state);
        }
    }

    [HarmonyPatch(typeof(MySession), nameof(MySession.Unload))]
    private static class SessionUnloadPatch
    {
        private static void Finalizer()
        {
            lock (InitializedTypes)
            {
                InitializedTypes.Clear();
            }
        }
    }
}
