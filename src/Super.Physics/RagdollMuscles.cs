using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>
/// Turns a powered ragdoll's joints towards the animation's pose for one simulation step: each simulated part's spin
/// against its parent moves part of the way to what would close the gap at <see cref="Rate"/>, shared between the two
/// by how hard each is to turn.
/// </summary>
internal static class RagdollMuscles
{
    // How fast a muscle at full strength closes the gap to the animation's pose, per second, and how much of the
    // difference in spin it takes away each step.
    private const float Rate = 20f;
    private const float Grip = 0.5f;

    public static void Drive(BodyInterface bodies, LiveRagdoll live)
    {
        for (int i = 1; i < live.Parts.Ids.Length; i++)
        {
            if (live.Parts.Dynamic[i])
            {
                DriveJoint(bodies, live, i);
            }
        }
    }

    private static void DriveJoint(BodyInterface bodies, LiveRagdoll live, int part)
    {
        var ids = live.Parts.Ids;
        var targets = live.Targets;
        int parent = live.Body.PartTree.ParentParts[part];
        var parentTurn = bodies.GetRotation(in ids[parent]);
        var turn = bodies.GetRotation(in ids[part]);
        var wanted = parentTurn * (Quaternion.Inverse(targets.Rotations[parent]) * targets.Rotations[part]);
        var gap = Turns.AxisAngle(Quaternion.Normalize(wanted * Quaternion.Inverse(turn)));
        var spin = bodies.GetAngularVelocity(in ids[part]);
        var parentSpin = bodies.GetAngularVelocity(in ids[parent]);
        float rate = Rate * live.Strength;
        float grip = Grip * live.Strength;
        var change = ((gap * rate) - (spin - parentSpin)) * grip;

        // Both parts turn about the joint between them, not their own middles, or the joint would pull the turn back
        // out. Each takes a share by how hard it is to turn about that point.
        var pivot = bodies.GetPosition(in ids[part]);
        var reach = bodies.GetCenterOfMassPosition(in ids[part]) - pivot;
        var parentReach = bodies.GetCenterOfMassPosition(in ids[parent]) - pivot;
        float own = PivotInertia(live, part, reach);
        float other = PivotInertia(live, parent, parentReach);
        bool parentMoves = live.Parts.Dynamic[parent];
        float share = parentMoves ? other / (own + other) : 1f;
        Turn(bodies, ids[part], change * share, reach);
        if (parentMoves)
        {
            Turn(bodies, ids[parent], -change * (1f - share), parentReach);
        }
    }

    /// <summary>How hard a part is to turn about a point <paramref name="reach"/> from its middle, in kilograms times metres squared.</summary>
    private static float PivotInertia(LiveRagdoll live, int part, Vector3 reach) =>
        (live.Body.Fitting.Inertias[part] * live.Scale * live.Scale) + (live.Body.Fitting.Masses[part] * reach.LengthSquared());

    /// <summary>Spins a body faster by <paramref name="spin"/> about a point <paramref name="reach"/> back from its middle.</summary>
    private static void Turn(BodyInterface bodies, BodyID body, Vector3 spin, Vector3 reach)
    {
        bodies.GetLinearAndAngularVelocity(in body, out var velocity, out var angular);
        var newVelocity = velocity + Vector3.Cross(spin, reach);
        var newAngular = angular + spin;
        bodies.SetLinearAndAngularVelocity(in body, in newVelocity, in newAngular);
    }
}
