using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>Every <see cref="Joint"/>'s constraint, made, remade and dropped to match the entities.</summary>
internal sealed class JointSet(World world, PhysicsSystem system)
{
    private readonly Dictionary<Entity, LiveJoint> _joints = [];
    private readonly List<Entity> _gone = [];
    private int _syncs;

    // Joints to the world hold on to this: a body that never moves and collides with nothing.
    private JoltPhysicsSharp.Body? _anchor;

    private sealed class LiveJoint(Joint joint, BodyID first, BodyID second, TwoBodyConstraint constraint)
    {
        public Joint Joint { get; } = joint;

        public BodyID First { get; } = first;

        public BodyID Second { get; } = second;

        public TwoBodyConstraint Constraint { get; } = constraint;

        public int Seen { get; set; }
    }

    /// <summary>Makes each joint that's new, changed or whose bodies were rebuilt, and drops those that are gone.</summary>
    public void Sync(Dictionary<Entity, TrackedBody> bodies)
    {
        _syncs++;
        foreach (var row in world.Query<Joint>())
        {
            Sync(bodies, row.Entity, row.First);
        }

        _gone.Clear();
        foreach (var (entity, live) in _joints)
        {
            if (live.Seen != _syncs)
            {
                _gone.Add(entity);
            }
        }

        foreach (var entity in _gone)
        {
            Remove(entity, _joints[entity]);
        }
    }

    /// <summary>Drops every joint on a body that's about to be destroyed, so none holds on to it.</summary>
    public void LetGoOf(BodyID body)
    {
        _gone.Clear();
        foreach (var (entity, live) in _joints)
        {
            if (live.First == body || live.Second == body)
            {
                _gone.Add(entity);
            }
        }

        foreach (var entity in _gone)
        {
            Remove(entity, _joints[entity]);
        }
    }

    public void Dispose()
    {
        foreach (var (_, live) in _joints)
        {
            system.RemoveConstraint(live.Constraint);
            live.Constraint.Dispose();
        }

        _joints.Clear();
        if (_anchor is not null)
        {
            var id = _anchor.ID;
            system.BodyInterface.RemoveAndDestroyBody(in id);
            _anchor = null;
        }
    }

    private void Sync(Dictionary<Entity, TrackedBody> bodies, Entity entity, Joint joint)
    {
        JointChecks.Check(entity, joint);
        if (!bodies.TryGetValue(entity, out var mine))
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Joint needs a Collider on the entity to hold on to.");
        }

        TrackedBody? other = null;
        if (joint.To is { } to)
        {
            if (!world.IsAlive(to) || !bodies.TryGetValue(to, out var theirs))
            {
                // What it held on to is gone, so it lets go.
                return;
            }

            other = theirs;
        }

        var second = other?.Body ?? Anchor().ID;
        if (_joints.TryGetValue(entity, out var live))
        {
            if (live.Joint == joint && live.First == mine.Body && live.Second == second)
            {
                live.Seen = _syncs;
                return;
            }

            Remove(entity, live);
        }

        var constraint = Make(joint, mine, other);
        system.AddConstraint(constraint);
        system.BodyInterface.ActivateBody(in mine.Body);
        _joints[entity] = new LiveJoint(joint, mine.Body, second, constraint) { Seen = _syncs };
    }

    private TwoBodyConstraint Make(Joint joint, in TrackedBody mine, TrackedBody? other)
    {
        var point = Place(mine, joint.Anchor);
        TwoBodyConstraintSettings settings = joint.Kind switch
        {
            JointKind.Fixed => new FixedConstraintSettings
            {
                Space = ConstraintSpace.WorldSpace,
                AutoDetectPoint = true,
                AxisX1 = Vector3.UnitX,
                AxisY1 = Vector3.UnitY,
                AxisX2 = Vector3.UnitX,
                AxisY2 = Vector3.UnitY,
            },
            JointKind.Ball => new PointConstraintSettings
            {
                Space = ConstraintSpace.WorldSpace,
                Point1 = point,
                Point2 = point,
            },
            JointKind.Hinge => Hinge(joint, point, Vector3.Normalize(Vector3.Transform(joint.Axis, mine.Rotation))),
            _ => new DistanceConstraintSettings
            {
                Space = ConstraintSpace.WorldSpace,
                Point1 = point,
                Point2 = other is { } theirs ? Place(theirs, joint.OtherAnchor) : joint.OtherAnchor,
                MinDistance = joint.MinLength,
                MaxDistance = joint.MaxLength,
            },
        };

        var otherHandle = other?.Handle ?? Anchor();
        return settings.CreateConstraint(in mine.Handle, in otherHandle);
    }

    private static HingeConstraintSettings Hinge(Joint joint, Vector3 point, Vector3 axis)
    {
        var across = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        return new HingeConstraintSettings
        {
            Space = ConstraintSpace.WorldSpace,
            Point1 = point,
            Point2 = point,
            HingeAxis1 = axis,
            HingeAxis2 = axis,
            NormalAxis1 = across,
            NormalAxis2 = across,
            LimitsMin = joint.MinAngle * MathF.PI / 180f,
            LimitsMax = joint.MaxAngle * MathF.PI / 180f,
        };
    }

    /// <summary>A point in a body's own space, placed in the world where the body is now.</summary>
    private static Vector3 Place(in TrackedBody tracked, Vector3 local) =>
        tracked.Position + Vector3.Transform(local * tracked.Scale, tracked.Rotation);

    private JoltPhysicsSharp.Body Anchor()
    {
        if (_anchor is null)
        {
            using var shape = new SphereShape(0.01f);
            var layer = ObjectLayerPairFilterMask.GetObjectLayer(0u, 0u);
            using var settings = new BodyCreationSettings(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, layer);
            _anchor = system.BodyInterface.CreateBody(settings);
            var id = _anchor.ID;
            system.BodyInterface.AddBody(in id, Activation.DontActivate);
        }

        return _anchor;
    }

    private void Remove(Entity entity, LiveJoint live)
    {
        // A body hanging still has gone to sleep; it should fall once let go.
        var first = live.First;
        var second = live.Second;
        system.BodyInterface.ActivateBody(in first);
        system.BodyInterface.ActivateBody(in second);
        system.RemoveConstraint(live.Constraint);
        live.Constraint.Dispose();
        _joints.Remove(entity);
    }
}
