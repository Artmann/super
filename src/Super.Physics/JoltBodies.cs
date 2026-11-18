using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Makes, changes, moves and destroys the Jolt bodies behind entities' colliders, and knows which entity owns each.
/// <see cref="BodySync"/> decides what each entity needs; this does it.
/// </summary>
internal sealed class JoltBodies(World world, PhysicsSystem system)
{
    /// <summary>The entity each body belongs to, by body ID. Ragdoll parts are listed under their entity too.</summary>
    public Dictionary<uint, Entity> Owners { get; } = [];

    /// <summary>The joints between bodies, which let go of a body when it is destroyed.</summary>
    public JointSet Joints { get; } = new(world, system);

    public TrackedBody Create(in WantedBody wanted)
    {
        var body = wanted.Settings;
        BodySetup.Check(wanted.Entity, wanted.Collider, body);
        using var shape = ColliderShapes.Make(wanted.Collider, wanted.Scale);
        using var settings = new BodyCreationSettings(
            shape, wanted.Position, wanted.Rotation, BodySetup.Motion(body.Kind), JoltSystem.Layer(wanted.Collider.Layers, wanted.Mask))
        {
            Friction = body.Friction,
            Restitution = body.Bounciness,
            IsSensor = wanted.Trigger,
        };
        if (body.Kind == BodyKind.Dynamic)
        {
            BodySetup.Simulate(settings, shape, body);
        }

        var handle = system.BodyInterface.CreateBody(settings);
        if (handle is null || handle.ID.IsInvalid)
        {
            throw new InvalidOperationException($"Entity {wanted.Entity.Id}'s Collider couldn't be added: there are already {Owners.Count} colliders, and {JoltSystem.MaxBodies} is the most.");
        }

        var id = handle.ID;
        system.BodyInterface.AddBody(in id, body.Kind == BodyKind.Dynamic ? Activation.Activate : Activation.DontActivate);
        Owners[id.ID] = wanted.Entity;
        return new TrackedBody
        {
            Body = id,
            Handle = handle,
            Collider = wanted.Collider,
            Settings = body,
            Trigger = wanted.Trigger,
            Character = wanted.Character,
            Mask = wanted.Mask,
            Scale = wanted.Scale,
            Position = wanted.Position,
            Rotation = wanted.Rotation,
        };
    }

    /// <summary>Builds the body again, keeping a dynamic body's speed. The caller forgets the old one first.</summary>
    public TrackedBody Rebuild(in TrackedBody old, in WantedBody wanted)
    {
        var bodies = system.BodyInterface;
        var velocity = Vector3.Zero;
        var spin = Vector3.Zero;
        if (old.Settings.Kind == BodyKind.Dynamic && wanted.Settings.Kind == BodyKind.Dynamic)
        {
            bodies.GetLinearAndAngularVelocity(in old.Body, out velocity, out spin);
        }

        Destroy(old);
        var tracked = Create(wanted);
        bodies.SetLinearAndAngularVelocity(in tracked.Body, in velocity, in spin);
        return tracked;
    }

    /// <summary>Gives the body the entity's new shape, layers and place.</summary>
    public void Update(ref TrackedBody tracked, in WantedBody wanted, float seconds)
    {
        var bodies = system.BodyInterface;
        var collider = wanted.Collider;
        if (tracked.Collider != collider || tracked.Scale != wanted.Scale)
        {
            ColliderShapes.Check($"Entity {wanted.Entity.Id}'s Collider", collider);
            using var shape = ColliderShapes.Make(collider, wanted.Scale);
            bodies.SetShape(in tracked.Body, shape, false, Activation.DontActivate);

            tracked.Collider = collider;
            tracked.Scale = wanted.Scale;
        }

        if (tracked.Collider.Layers != collider.Layers || tracked.Mask != wanted.Mask)
        {
            var layer = JoltSystem.Layer(collider.Layers, wanted.Mask);
            bodies.SetObjectLayer(in tracked.Body, in layer);
            tracked.Mask = wanted.Mask;
        }

        Place(ref tracked, wanted.Position, wanted.Rotation, seconds);
    }

    public void Destroy(in TrackedBody tracked)
    {
        Joints.LetGoOf(tracked.Body);
        system.BodyInterface.RemoveAndDestroyBody(in tracked.Body);
        Owners.Remove(tracked.Body.ID);
    }

    /// <summary>
    /// Moves the body to the entity's place. A kinematic body sweeps there over <paramref name="seconds"/> so it
    /// pushes what's in its way; any other body jumps there.
    /// </summary>
    private void Place(ref TrackedBody tracked, Vector3 position, Quaternion rotation, float seconds)
    {
        var bodies = system.BodyInterface;
        bool moved = tracked.Position != position || tracked.Rotation != rotation;
        if (tracked.Settings.Kind == BodyKind.Dynamic)
        {
            // Physics wrote this Transform last step; if the game changed it since, the body jumps there.
            if (moved)
            {
                bodies.SetPositionAndRotation(in tracked.Body, in position, in rotation, Activation.Activate);
            }
        }
        else if (tracked.Settings.Kind == BodyKind.Kinematic && seconds > 0f && !tracked.Trigger)
        {
            if (moved)
            {
                bodies.MoveKinematic(in tracked.Body, in position, in rotation, seconds);
                tracked.Moving = true;
            }
            else
            {
                Stop(ref tracked);
            }
        }
        else if (moved)
        {
            bodies.SetPositionAndRotation(in tracked.Body, in position, in rotation, Activation.DontActivate);
            Stop(ref tracked);
        }

        tracked.Position = position;
        tracked.Rotation = rotation;
    }

    /// <summary>Stops a kinematic body that was sweeping last sync.</summary>
    private void Stop(ref TrackedBody tracked)
    {
        if (!tracked.Moving)
        {
            return;
        }

        var still = Vector3.Zero;
        system.BodyInterface.SetLinearAndAngularVelocity(in tracked.Body, in still, in still);
        tracked.Moving = false;
    }
}
