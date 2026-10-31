using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using Shared.Plugin;
using VRageMath;

namespace Shared.Patches;

// Client + Server
//
// Keeps ships joined by a locked connector from fighting each other: of the ships that are not
// flown, only the strongest keeps slowing the group down. See Docs/connected-ships.md.
//
// A pilot's input only reaches the thrusters and gyros of the grid with the cockpit. The dampeners and
// gyros of every other connected ship see the group speed up and work against it, so most of what the
// pilot's ship puts out is lost to them.
//
// Nothing here changes a ship's settings. Every second the rule is worked out from what is connected
// and flown at that moment, and the two sets below only change how the thrust and gyro code reads
// those settings. So there is nothing to restore after a disconnect, a save and load, a crash or a
// handover to another server, and a value a player or script sets is kept as they set it.
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ConnectedShipsPatch
{
    private const int UpdateTicks = 60;

    // A ship without dampeners keeps a small rest speed in the solver: the bodies stay 2 mm apart but
    // the group never settles and stays awake for minutes. So a group nobody pushes and slower than
    // these (m/s, rad/s) gets all of its ships back to finish the stop, where there's no fight anyway.
    private const float RestSpeed = 1f;
    private const float RestSpin = 0.05f;

    // A ship stays in charge of holding the group up only with this much thrust to spare, since its
    // nominal thrust can drop with atmosphere or fuel
    private const float LiftMargin = 1.5f;

    // The connector of each connection that did the welding. Game thread only.
    private static readonly HashSet<MyShipConnector> Links = new HashSet<MyShipConnector>();
    private static bool dirty;

    // Replaced, never changed, on the game thread outside of the parallel grid updates, which read them
    private static HashSet<MyCubeGrid> quietThrusters = new HashSet<MyCubeGrid>();
    private static HashSet<MyCubeGrid> quietGyros = new HashSet<MyCubeGrid>();

    public static bool IsThrustQuiet(MyCubeGrid grid) => quietThrusters.Contains(grid);

    public static bool IsGyroQuiet(MyCubeGrid grid) => quietGyros.Contains(grid);

    private static readonly Func<MyEntityThrustComponent, Vector3> MaxPositiveThrust =
        AccessTools.MethodDelegate<Func<MyEntityThrustComponent, Vector3>>(
            AccessTools.PropertyGetter(typeof(MyEntityThrustComponent), "TotalMaxPositiveThrust")
        );
    private static readonly Func<MyEntityThrustComponent, Vector3> MaxNegativeThrust =
        AccessTools.MethodDelegate<Func<MyEntityThrustComponent, Vector3>>(
            AccessTools.PropertyGetter(typeof(MyEntityThrustComponent), "TotalMaxNegativeThrust")
        );
    private static readonly AccessTools.FieldRef<MyGridGyroSystem, float> MaxGyroForce =
        AccessTools.FieldRefAccess<MyGridGyroSystem, float>("m_maxGyroForce");
    private static readonly Func<MyGridGyroSystem, MyCubeGrid> Grid = AccessTools.MethodDelegate<
        Func<MyGridGyroSystem, MyCubeGrid>
    >(AccessTools.PropertyGetter(typeof(MyUpdateableGridSystem), "Grid"));
    private static readonly Action<MyGridGyroSystem, Vector3> SetTorque =
        AccessTools.MethodDelegate<Action<MyGridGyroSystem, Vector3>>(
            AccessTools.PropertySetter(typeof(MyGridGyroSystem), nameof(MyGridGyroSystem.Torque))
        );

    private static readonly Type BlockThrusters = AccessTools.TypeByName(
        "Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent"
    );
    private static readonly Func<Vector3, float, float, Vector3, Vector3, float> ResidualMass =
        AccessTools.MethodDelegate<Func<Vector3, float, float, Vector3, Vector3, float>>(
            AccessTools.Method(BlockThrusters, "ComputeResidualMass")
        );

    private sealed class Ship
    {
        public readonly List<MyCubeGrid> Grids;
        public readonly bool Flown;

        public Ship(List<MyCubeGrid> grids)
        {
            Grids = grids;
            Flown = grids.Any(IsFlown);
        }

        public float Thrust => Grids.Sum(TotalThrust);

        public float Torque => Grids.Sum(g => Torque(g));

        public long Id => Grids.Min(g => g.EntityId);

        public bool Dampens => Grids.Any(g => Thrusters(g)?.DampenersEnabled == true);

        public bool Pushed => Grids.Any(HasInput);

        public bool AtRest
        {
            get
            {
                var physics = Grids.OrderByDescending(g => g.BlocksCount).First().Physics;
                return physics == null
                    || (
                        physics.LinearVelocity.Length() < RestSpeed
                        && physics.AngularVelocity.Length() < RestSpin
                    );
            }
        }
    }

    private static float Torque(MyCubeGrid grid)
    {
        var gyros = grid.GridSystems.GyroSystem;
        return gyros == null ? 0f : MaxGyroForce(gyros);
    }

    private static MyEntityThrustComponent Thrusters(MyCubeGrid grid) =>
        grid.Components.Get<MyEntityThrustComponent>();

    // A pilot or an autopilot: either makes the group's control system point at the ship's block
    private static bool IsFlown(MyCubeGrid grid) =>
        grid.GridSystems.ControlSystem?.GetShipController()?.CubeGrid == grid;

    // A pilot's input is only in ControlThrust and ControlTorque within a frame: the cockpit clears
    // them after the simulation, before the plugin update. Its indicators stay while the input lasts.
    private static bool HasInput(MyCubeGrid grid)
    {
        var thrusters = Thrusters(grid);
        var gyros = grid.GridSystems.GyroSystem;
        return grid.GridSystems.ControlSystem?.GetShipController() is IMyShipController pilot
                && ((MyCubeBlock)pilot).CubeGrid == grid
                && (
                    pilot.MoveIndicator != Vector3.Zero
                    || pilot.RotationIndicator != Vector2.Zero
                    || pilot.RollIndicator != 0f
                )
            || thrusters != null
                && (
                    thrusters.ControlThrust != Vector3.Zero
                    || thrusters.AutoPilotControlThrust != Vector3.Zero
                )
            || gyros != null && gyros.ControlTorque != Vector3.Zero;
    }

    private static float TotalThrust(MyCubeGrid grid)
    {
        var thrusters = Thrusters(grid);
        if (thrusters == null || !thrusters.Enabled)
            return 0f;

        var total = MaxPositiveThrust(thrusters) + MaxNegativeThrust(thrusters);
        return total.X + total.Y + total.Z;
    }

    public static void Update(long tick)
    {
        if (!dirty && tick % UpdateTicks != 0)
            return;

        dirty = false;
        Links.RemoveWhere(c => c.Closed || !c.Connected);

        if (
            MySession.Static == null
            || !Sync.IsServer
            || !Common.Config.Enabled
            || !Common.Config.CalmConnectedShips
        )
        {
            Apply(new HashSet<MyCubeGrid>(), new HashSet<MyCubeGrid>());
            return;
        }

        var thrusters = new HashSet<MyCubeGrid>();
        var gyros = new HashSet<MyCubeGrid>();

        foreach (var ships in ConnectedShips())
        {
            if (ships.All(s => s.AtRest && !s.Pushed))
                continue;

            Choose(ships, s => s.Dampens, s => s.Thrust, thrusters, CanHoldGroup);
            Choose(ships, _ => true, s => s.Torque, gyros, (_, _) => true);
        }

        Apply(thrusters, gyros);
    }

    private static void Apply(HashSet<MyCubeGrid> thrusters, HashSet<MyCubeGrid> gyros)
    {
        if (!quietThrusters.SetEquals(thrusters))
            quietThrusters = thrusters;
        if (!quietGyros.SetEquals(gyros))
            quietGyros = gyros;
    }

    // Everything in a ship goes quiet or nothing does, except that a ship being flown stays as it is
    private static void Choose(
        List<Ship> ships,
        Func<Ship, bool> counts,
        Func<Ship, float> strength,
        HashSet<MyCubeGrid> quiet,
        Func<Ship, List<Ship>, bool> mayCarry
    )
    {
        var strongest = ships
            .Where(counts)
            .OrderByDescending(strength)
            .ThenByDescending(s => s.Flown || s.Pushed)
            .ThenBy(s => s.Id)
            .FirstOrDefault();
        if (strongest == null || strength(strongest) <= 0f || !mayCarry(strongest, ships))
            return;

        foreach (var ship in ships)
            if (ship != strongest && !ship.Flown)
                quiet.UnionWith(ship.Grids);
    }

    // In gravity the dampeners hold the group up, and one ship may not be able to hold all of them.
    // Same check as MyThrusterBlockThrustComponent.GetResidualMassInGravity, for the group's mass.
    private static bool CanHoldGroup(Ship strongest, List<Ship> ships)
    {
        var gravity = strongest.Grids[0].Physics?.Gravity ?? Vector3.Zero;
        var strength = gravity.Length();
        if (strength < 0.001f)
            return true;

        var mass = LiftMargin * ships.Sum(s => s.Grids.Sum(g => g.Physics?.Mass ?? 0f));
        foreach (var grid in strongest.Grids)
        {
            var thrusters = Thrusters(grid);
            if (thrusters == null)
                continue;

            var matrix = grid.PositionComp.WorldMatrixNormalizedInv.GetOrientation();
            var up = Vector3.TransformNormal(-gravity / strength, matrix);
            if (
                ResidualMass(
                    up,
                    strength,
                    mass,
                    MaxPositiveThrust(thrusters),
                    MaxNegativeThrust(thrusters)
                ) <= 0f
            )
                return true;
        }

        return false;
    }

    // The sets of two or more ships which are connected to each other and free to move. The game
    // makes a group held by a static grid a fixed body, whose dampeners and gyros do nothing, so
    // those are skipped first: most connections on most servers are ships docked at stations.
    private static IEnumerable<List<Ship>> ConnectedShips()
    {
        var neighbors = new Dictionary<MyCubeGrid, List<MyCubeGrid>>();
        foreach (var link in Links)
        {
            var other = link.Other;
            if (other == null || MyFixedGrids.IsRooted(link.CubeGrid))
                continue;

            AddNeighbor(neighbors, link.CubeGrid, other.CubeGrid);
            AddNeighbor(neighbors, other.CubeGrid, link.CubeGrid);
        }

        var visited = new HashSet<MyCubeGrid>();
        foreach (var start in neighbors.Keys)
        {
            if (visited.Contains(start))
                continue;

            var ships = new List<Ship>();
            var anchored = false;
            var open = new Stack<MyCubeGrid>();
            open.Push(start);
            while (open.Count > 0)
            {
                var grid = open.Pop();
                if (visited.Contains(grid))
                    continue;

                var members =
                    MyCubeGridGroups
                        .Static.Mechanical.GetGroup(grid)
                        ?.Nodes.Select(n => n.NodeData)
                        .ToList()
                    ?? new List<MyCubeGrid> { grid };
                visited.UnionWith(members);
                anchored |= members.Any(g => g.IsStatic || g.Physics == null);
                ships.Add(new Ship(members));

                foreach (var member in members)
                    if (neighbors.TryGetValue(member, out var next))
                        next.ForEach(open.Push);
            }

            if (!anchored && ships.Count > 1)
                yield return ships;
        }
    }

    private static void AddNeighbor(
        Dictionary<MyCubeGrid, List<MyCubeGrid>> neighbors,
        MyCubeGrid grid,
        MyCubeGrid neighbor
    )
    {
        if (!neighbors.TryGetValue(grid, out var list))
            neighbors[grid] = list = new List<MyCubeGrid>();
        list.Add(neighbor);
    }

    [HarmonyPatch(typeof(MyShipConnector), "WeldInternal")]
    private static class ConnectPatch
    {
        private static void Postfix(MyShipConnector __instance)
        {
            Links.Add(__instance);
            dirty = true;
        }
    }

    [HarmonyPatch(typeof(MyShipConnector), "RemoveLinks")]
    private static class DisconnectPatch
    {
        private static void Postfix() => dirty = true;
    }

    private static bool DampenersActive(MyEntityThrustComponent thrusters) =>
        thrusters.DampenersEnabled && !(thrusters.Entity is MyCubeGrid grid && IsThrustQuiet(grid));

    private static IEnumerable<CodeInstruction> ReadDampenersActive(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var getter = AccessTools.PropertyGetter(
            typeof(MyEntityThrustComponent),
            nameof(MyEntityThrustComponent.DampenersEnabled)
        );
        var replacement = AccessTools.Method(typeof(ConnectedShipsPatch), nameof(DampenersActive));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }

        if (replaced == 0)
            throw new InvalidOperationException("No DampenersEnabled read found");
    }

    // The slowdown thrust of the ship itself
    [HarmonyPatch(typeof(MyEntityThrustComponent), "ComputeBaseThrust")]
    private static class SlowdownThrustPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        ) => ReadDampenersActive(instructions);
    }

    // The game hands the mass of a ship without dampeners to the largest ship with dampeners, which
    // is how the ship that keeps its dampeners ends up stopping all of the group
    [HarmonyPatch]
    private static class SharedMassPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(BlockThrusters, "CalculateMass");

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        ) => ReadDampenersActive(instructions);
    }

    [HarmonyPatch(typeof(MyGridGyroSystem), "UpdateGyros")]
    private static class GyroPatch
    {
        // Control input and gyro override keep working, only the slowdown stops. The original still
        // runs when nothing is turning, to take the gyro system off the update list.
        private static bool Prefix(MyGridGyroSystem __instance)
        {
            var grid = Grid(__instance);
            var physics = grid.Physics;
            if (
                !IsGyroQuiet(grid)
                || physics == null
                || Vector3.IsZero(physics.AngularVelocity, 0.001f)
                || !Vector3.IsZero(__instance.ControlTorque, 0.001f)
            )
                return true;

            __instance.SlowdownTorque = Vector3.Zero;
            SetTorque(__instance, Vector3.Zero);
            return false;
        }
    }
}
