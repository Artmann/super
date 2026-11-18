using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>Checks an entity's <see cref="Body"/> and turns it into Jolt's settings.</summary>
internal static class BodySetup
{
    /// <summary>Checks the entity's collider and body, saying what to fix when they can't be built.</summary>
    public static void Check(Entity entity, Collider collider, Body body)
    {
        ColliderShapes.Check($"Entity {entity.Id}'s Collider", collider);
        CheckBody(entity, body);
    }

    public static MotionType Motion(BodyKind kind) => kind switch
    {
        BodyKind.Dynamic => MotionType.Dynamic,
        BodyKind.Static => MotionType.Static,
        _ => MotionType.Kinematic,
    };

    /// <summary>Gives a dynamic body the mass, drag and gravity asked for, spread over its shape.</summary>
    public static void Simulate(BodyCreationSettings settings, Shape shape, Body body)
    {
        var mass = shape.MassProperties;
        mass.ScaleToMass(body.Mass);
        settings.OverrideMassProperties = OverrideMassProperties.MassAndInertiaProvided;
        settings.MassPropertiesOverride = mass;
        settings.LinearDamping = body.Drag;
        settings.AngularDamping = body.AngularDrag;
        settings.GravityFactor = body.GravityScale;
        settings.MotionQuality = body.Continuous ? MotionQuality.LinearCast : MotionQuality.Discrete;
        if (body.LockRotation)
        {
            settings.AllowedDOFs = AllowedDOFs.TranslationX | AllowedDOFs.TranslationY | AllowedDOFs.TranslationZ;
        }
    }

    private static void CheckBody(Entity entity, Body body)
    {
        if ((body.CollidesWith & ~LayerBits.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.CollidesWith, $"Entity {entity.Id}'s Body collides with layers {body.CollidesWith}; layers are bits 0 to 15.");
        }

        if (!Numbers.IsZeroOrMore(body.Friction))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Friction, $"Entity {entity.Id}'s Body has friction {body.Friction}; it must be 0 or more.");
        }

        if (!(body.Bounciness >= 0f && body.Bounciness <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Bounciness, $"Entity {entity.Id}'s Body has bounciness {body.Bounciness}; it must be from 0 to 1.");
        }

        if (body.Kind != BodyKind.Dynamic)
        {
            return;
        }

        if (!Numbers.IsAboveZero(body.Mass))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Mass, $"Entity {entity.Id}'s Body has mass {body.Mass}; it must be above 0.");
        }

        if (!Numbers.IsZeroOrMore(body.Drag) || !Numbers.IsZeroOrMore(body.AngularDrag))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Drag, $"Entity {entity.Id}'s Body has drag {body.Drag} and angular drag {body.AngularDrag}; both must be 0 or more.");
        }

        if (!float.IsFinite(body.GravityScale))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.GravityScale, $"Entity {entity.Id}'s Body has gravity scale {body.GravityScale}; it must be finite.");
        }
    }
}
