using HarmonyLib;
using Havok;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Shared.Plugin;
using VRage.Game;
using VRageMath;

namespace ServerPlugin.Patches;

[HarmonyPatch(typeof(MyCharacter), "RigidBody_ContactPointCallback")]
public static class CharacterGridPushPatch
{
    private static void Prefix(MyCharacter __instance, ref HkContactPointEvent value)
    {
        if (
            Common.Config?.Enabled != true
            || __instance.IsDead
            || value.ContactProperties.IsDisabled
            || value.SeparatingVelocity >= -0.1f
            || value.GetOtherEntity(__instance, out bool characterIsA)
                is not MyCubeGrid { IsStatic: false, GridSizeEnum: MyCubeSize.Large } grid
            || grid.NaturalGravity != Vector3.Zero
            || grid.Physics is not { Enabled: true, RigidBody.IsFixedOrKeyframed: false } physics
            || !AtRest(physics)
            || __instance.Physics is not { Mass: > 0 } character
        )
            return;

        // Havok's contact normal points from body B to body A. Use only the closing speed
        // along it and the reduced mass of the suit and grid.
        float mass = physics.Mass;
        if (mass <= 0f)
            return;
        var normal = value.ContactPoint.Normal;
        var direction = characterIsA ? -normal : normal;
        var impulse =
            direction * -value.SeparatingVelocity * (character.Mass / (1f + character.Mass / mass));
        var point = physics.ClusterToWorld(value.ContactPoint.Position);
        PushAfterSimulation(grid, physics, physics.RigidBody, impulse, point);
    }

    private static bool AtRest(MyGridPhysics physics) =>
        physics.LinearVelocity == Vector3.Zero && physics.AngularVelocity == Vector3.Zero;

    private static void PushAfterSimulation(
        MyCubeGrid grid,
        MyGridPhysics physics,
        HkRigidBody body,
        Vector3 impulse,
        Vector3D point
    )
    {
        // Havok can report several points for one impact. Give the solver the first chance to
        // move the grid, then apply the missing impulse once if the same body is still at rest.
        MySandboxGame.Static.Invoke(
            () =>
            {
                if (
                    !grid.MarkedForClose
                    && grid.Physics == physics
                    && physics.Enabled
                    && physics.RigidBody == body
                    && body is { IsDisposed: false, InWorld: true, IsFixedOrKeyframed: false }
                    && AtRest(physics)
                    && !MyFixedGrids.IsRooted(grid)
                )
                    physics.ApplyImpulse(impulse, point);
            },
            nameof(CharacterGridPushPatch)
        );
    }
}
