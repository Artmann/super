using System.Numerics;

namespace Super.Physics;

/// <summary>Checks that an entity's <see cref="Joint"/> can be made, saying what to fix when it can't.</summary>
internal static class JointChecks
{
    public static void Check(Entity entity, Joint joint)
    {
        if (joint.To == entity)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Joint is joined to itself; use another entity, or null for the world.");
        }

        if (!Numbers.IsFinite(joint.Anchor) || !Numbers.IsFinite(joint.OtherAnchor))
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Joint has an anchor that isn't a finite point.");
        }

        if (joint.Kind == JointKind.Hinge)
        {
            CheckHinge(entity, joint);
        }

        if (joint.Kind == JointKind.Distance && !(joint.MinLength >= 0f && joint.MaxLength >= joint.MinLength && joint.MaxLength > 0f && float.IsFinite(joint.MaxLength)))
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s distance Joint is from {joint.MinLength} to {joint.MaxLength} metres long; it needs 0 or more, up to a longest above 0.");
        }
    }

    private static void CheckHinge(Entity entity, Joint joint)
    {
        if (!Numbers.IsFinite(joint.Axis) || joint.Axis.LengthSquared() < 1e-12f)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s hinge Joint needs an axis to turn about, not {joint.Axis}.");
        }

        if (!(joint.MinAngle is >= -180f and <= 0f) || !(joint.MaxAngle is >= 0f and <= 180f))
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s hinge Joint turns from {joint.MinAngle} to {joint.MaxAngle} degrees; the least must be from -180 to 0, and the most from 0 to 180.");
        }
    }
}
