using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>Builds a ragdoll's bodies where the animation has its parts, moving as fast as they were, and joins them.</summary>
internal static class RagdollBuilder
{
    // Joints rub a little, scaled by the part's mass, so a limp body settles rather than wobbling like jelly.
    private const float JointFriction = 0.5f;

    /// <summary>Builds the parts and joins them.</summary>
    public static void Build(PhysicsSystem system, Entity entity, LiveRagdoll live, Ragdoll ragdoll, Dictionary<uint, Entity> owners)
    {
        var parts = live.Parts;
        parts.Filter = NeighbourFilter(live.Body);
        parts.Handles = new JoltPhysicsSharp.Body[live.Body.PartTree.Joints.Length];
        for (int i = 0; i < parts.Ids.Length; i++)
        {
            // Each entity is its own group, so parts of different ragdolls still hit each other.
            var body = AddPart(system, live, ragdoll, i, new CollisionGroupID((uint)entity.Id));
            if (body is null || body.ID.IsInvalid)
            {
                parts.Destroy();
                throw new InvalidOperationException($"Entity {entity.Id}'s ragdoll couldn't be added: the world is full, with {owners.Count} colliders already.");
            }

            parts.Handles[i] = body;
            parts.Ids[i] = body.ID;
            owners[body.ID.ID] = entity;

            // A body is in the world before it's given speed, since that wakes it.
            system.BodyInterface.AddBody(in parts.Ids[i], Activation.Activate);
            if (live.Targets.HasPrevious)
            {
                var (velocity, spin) = live.Targets.Velocity(i, live.Body.Fitting.Fits[i].Centre * live.Scale);
                system.BodyInterface.SetLinearAndAngularVelocity(in parts.Ids[i], in velocity, in spin);
            }
        }

        Join(system, live);
    }

    /// <summary>A filter that keeps parts from colliding with their parent, and with parts they touch at rest.</summary>
    private static GroupFilterTable NeighbourFilter(RagdollBody body)
    {
        var filter = new GroupFilterTable((uint)body.Parts.Count);
        foreach (var (a, b) in body.Touching)
        {
            filter.DisableCollision(new CollisionSubGroupID((uint)a), new CollisionSubGroupID((uint)b));
        }

        return filter;
    }

    private static JoltPhysicsSharp.Body? AddPart(PhysicsSystem system, LiveRagdoll live, Ragdoll ragdoll, int part, CollisionGroupID group)
    {
        var settings = live.Body;
        bool dynamic = ragdoll.Simulates(part);
        live.Parts.Dynamic[part] = dynamic;
        using var shape = PartShape(settings, part, live.Scale);
        var position = live.Targets.Positions[part];
        var rotation = live.Targets.Rotations[part];
        var layer = ObjectLayerPairFilterMask.GetObjectLayer(settings.Layers, settings.CollidesWith);
        using var creation = new BodyCreationSettings(shape, in position, in rotation, dynamic ? MotionType.Dynamic : MotionType.Kinematic, layer)
        {
            Friction = settings.Friction,
            Restitution = settings.Bounciness,
            CollisionGroup = new CollisionGroup(live.Parts.Filter, group, new CollisionSubGroupID((uint)part)),
            MotionQuality = settings.Continuous ? MotionQuality.LinearCast : MotionQuality.Discrete,
        };
        var mass = shape.MassProperties;
        mass.ScaleToMass(settings.Fitting.Masses[part]);
        creation.OverrideMassProperties = OverrideMassProperties.MassAndInertiaProvided;
        creation.MassPropertiesOverride = mass;
        return system.BodyInterface.CreateBody(creation);
    }

    /// <summary>A part's shape in its joint's space, in metres.</summary>
    private static Shape PartShape(RagdollBody body, int part, float scale)
    {
        var (centre, turn, kind, half, radius) = body.ShapeOf(part);
        half *= scale;
        radius *= scale;
        Shape inner = kind switch
        {
            RagdollShape.Box => new BoxShape(in half, MathF.Min(Foundation.DefaultConvexRadius, MathF.Min(half.X, MathF.Min(half.Y, half.Z)) / 2f)),
            RagdollShape.Ball => new SphereShape(radius),
            _ => half.Y > 0f ? new CapsuleShape(half.Y, radius) : new SphereShape(radius),
        };
        var offset = centre * scale;
        var shape = new RotatedTranslatedShape(in offset, in turn, inner);
        inner.Dispose();
        return shape;
    }

    /// <summary>Joins each part to its parent where the two meet, limited to how far that joint bends and twists.</summary>
    private static void Join(PhysicsSystem system, LiveRagdoll live)
    {
        var settings = live.Body;
        var targets = live.Targets;
        var handles = live.Parts.Handles!;
        for (int i = 1; i < handles.Length; i++)
        {
            int parent = settings.PartTree.ParentParts[i];
            var frame = settings.Fitting.Frames[i];
            var at = targets.Positions[i];
            var joint = new SwingTwistConstraintSettings
            {
                Space = ConstraintSpace.WorldSpace,
                Position1 = at,
                Position2 = at,
                TwistAxis1 = Vector3.Transform(frame.Twist1, targets.Rotations[parent]),
                PlaneAxis1 = Vector3.Transform(frame.Plane1, targets.Rotations[parent]),
                TwistAxis2 = Vector3.Transform(frame.Twist2, targets.Rotations[i]),
                PlaneAxis2 = Vector3.Transform(frame.Plane2, targets.Rotations[i]),
                NormalHalfConeAngle = frame.Cone,
                PlaneHalfConeAngle = frame.Cone,
                TwistMinAngle = frame.TwistMin,
                TwistMaxAngle = frame.TwistMax,
                MaxFrictionTorque = settings.Fitting.Masses[i] * JointFriction,
            };
            var constraint = joint.CreateConstraint(in handles[parent], in handles[i]);
            system.AddConstraint(constraint);
            live.Parts.Constraints[i] = constraint;
        }
    }
}
