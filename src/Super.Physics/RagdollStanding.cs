using System.Numerics;

namespace Super.Physics;

/// <summary>Turns a ragdoll off, standing its entity where the body lies so a get-up clip starts from there.</summary>
internal static class RagdollStanding
{
    /// <summary>
    /// Takes the bodies away. A ragdoll lying with a simulated pelvis stands its entity on the ground under it, facing
    /// the way it would get up; whatever was simulated then holds its last pose and blends back into the clips.
    /// <paramref name="floor"/> finds the ground: the height a ray down from a point hits within a reach, or null.
    /// </summary>
    public static void TurnOff(World world, Entity entity, LiveRagdoll live, float blendSeconds, Func<Vector3, float, uint, float?> floor)
    {
        var parts = live.Parts;
        bool simulated = parts.Simulating;
        bool pelvis = parts.Dynamic[0];
        bool faceUp = parts.IsFaceUp();
        parts.Read();
        parts.Destroy();
        if (!simulated)
        {
            live.Pose.Clear(live.Animator);
            return;
        }

        ref var transform = ref world.Get<Transform>(entity);
        if (pelvis)
        {
            Stand(live, ref transform, faceUp, floor);

            // The pose is held where it was, so the entity mustn't be seen sliding to its new place.
            world.Teleport(entity, transform);
        }

        live.Pose.Freeze(live.Body, parts, live.Scale, transform, blendSeconds);
        if (blendSeconds <= 0f)
        {
            live.Pose.Clear(live.Animator);
            return;
        }

        live.Pose.Show(live.Animator, 0f);
    }

    /// <summary>
    /// Stands the entity on the ground under the pelvis, turned to face the way a body lying like this gets up. When the
    /// clip playing now lies down too, such as a get-up started as the ragdoll turns off, the entity is placed so that
    /// clip's pelvis lies where the ragdoll's does, pointing the same way.
    /// </summary>
    private static void Stand(LiveRagdoll live, ref Transform transform, bool faceUp, Func<Vector3, float, uint, float?> floor)
    {
        var parts = live.Parts;
        var pelvis = parts.Positions[0];
        var up = Vector3.Transform(live.Body.Fitting.PelvisUp, parts.Rotations[0]);
        float reach = live.Body.Fitting.PelvisHeight * live.Scale;
        float ground = floor(pelvis, reach * 2f, live.Body.CollidesWith) ?? pelvis.Y - reach;

        var (clipPelvis, clipUp) = live.Targets.Pelvis(live.Body, live.Animator);
        var lyingUp = up with { Y = 0f };
        clipUp.Y = 0f;
        if (clipUp.LengthSquared() > 0.5f && lyingUp.LengthSquared() > 0.25f)
        {
            float yaw = MathF.Atan2(lyingUp.X, lyingUp.Z) - MathF.Atan2(clipUp.X, clipUp.Z);
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            var offset = Vector3.Transform(clipPelvis * transform.Scale, turn);
            transform = new Transform(new Vector3(pelvis.X - offset.X, ground, pelvis.Z - offset.Z), turn, transform.Scale);
            return;
        }

        var forward = Vector3.Transform(live.Body.Fitting.PelvisForward, parts.Rotations[0]);
        transform = Transform.Facing(new Vector3(pelvis.X, ground, pelvis.Z), Facing(up, forward, faceUp, transform), Vector3.UnitY, transform.Scale);
    }

    /// <summary>Which way a body lying with this up and forward faces once it stands.</summary>
    private static Vector3 Facing(Vector3 up, Vector3 forward, bool faceUp, in Transform transform)
    {
        // Lying down, the body's up runs along the ground: face up, it gets up facing its feet; face down, its head.
        var facing = faceUp ? -up : up;
        facing.Y = 0f;
        if (facing.LengthSquared() < 0.25f)
        {
            // Sitting or standing, it faces the way its front does.
            facing = forward with { Y = 0f };
        }

        if (facing.LengthSquared() < 1e-6f)
        {
            facing = Vector3.Transform(Vector3.UnitZ, transform.Rotation) with { Y = 0f };
        }

        return facing;
    }
}
