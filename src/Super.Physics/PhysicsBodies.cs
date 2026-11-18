using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Reads how fast bodies move, and pushes, spins and speeds up dynamic ones. Reach it through <c>physics.Bodies</c>.
/// A simulated ragdoll counts as one body.
/// </summary>
public sealed class PhysicsBodies
{
    private readonly DisposalGuard _guard;
    private readonly World _world;
    private readonly BodySync _bodies;
    private readonly PhysicsRagdolls _ragdolls;
    private readonly Action _sync;

    internal PhysicsBodies(DisposalGuard guard, World world, BodySync bodies, PhysicsRagdolls ragdolls, Action sync)
    {
        _guard = guard;
        _world = world;
        _bodies = bodies;
        _ragdolls = ragdolls;
        _sync = sync;
    }

    private BodyInterface Jolt => _bodies.Simulation.BodyInterface;

    /// <summary>
    /// How fast an entity's body moves, in metres per second. 0 for one that isn't moving or has no body yet. For a
    /// simulated ragdoll, the mean of its parts, weighted by their mass.
    /// </summary>
    public Vector3 Velocity(Entity entity)
    {
        if (_ragdolls.Set.Simulating(entity) is { } live)
        {
            return live.Parts.Velocity();
        }

        return _bodies.TryGet(entity, out var tracked) ? Jolt.GetLinearVelocity(in tracked.Body) : Vector3.Zero;
    }

    /// <summary>How fast an entity's body turns, as an axis whose length is radians per second. For a ragdoll, its pelvis.</summary>
    public Vector3 AngularVelocity(Entity entity)
    {
        if (_ragdolls.Set.Simulating(entity) is { } live)
        {
            return Jolt.GetAngularVelocity(in live.Parts.Ids[0]);
        }

        return _bodies.TryGet(entity, out var tracked) ? Jolt.GetAngularVelocity(in tracked.Body) : Vector3.Zero;
    }

    /// <summary>
    /// Sets a dynamic body's velocity, in metres per second, such as for a jump or a throw. A simulated ragdoll's
    /// parts all take it, as when a cannon fires it.
    /// </summary>
    public void SetVelocity(Entity entity, Vector3 velocity)
    {
        Numbers.RequireFinite(velocity, nameof(velocity));
        if (_ragdolls.Simulated(entity) is { } live)
        {
            live.Parts.SetVelocity(velocity);
            return;
        }

        var body = DynamicBody(entity, velocity, nameof(velocity));
        Jolt.SetLinearVelocity(in body, in velocity);
        Jolt.ActivateBody(in body);
    }

    /// <summary>Sets how fast a dynamic body turns, as an axis whose length is radians per second. Every simulated part of a ragdoll takes it.</summary>
    public void SetAngularVelocity(Entity entity, Vector3 spin)
    {
        Numbers.RequireFinite(spin, nameof(spin));
        if (_ragdolls.Simulated(entity) is { } live)
        {
            for (int i = 0; i < live.Parts.Ids.Length; i++)
            {
                if (live.Parts.Dynamic[i])
                {
                    Jolt.SetAngularVelocity(in live.Parts.Ids[i], in spin);
                    Jolt.ActivateBody(in live.Parts.Ids[i]);
                }
            }

            return;
        }

        var body = DynamicBody(entity, spin, nameof(spin));
        Jolt.SetAngularVelocity(in body, in spin);
        Jolt.ActivateBody(in body);
    }

    /// <summary>
    /// Gives a dynamic body a sudden push, in newton-seconds, through its centre: the change in velocity is the
    /// impulse over the mass. Use it for a kick, a hit or an explosion. A simulated ragdoll's parts share it by mass,
    /// so the whole body moves; <see cref="PhysicsRagdolls.PushPart"/> pushes one limb.
    /// </summary>
    public void Push(Entity entity, Vector3 impulse)
    {
        Numbers.RequireFinite(impulse, nameof(impulse));
        if (_ragdolls.Simulated(entity) is { } live)
        {
            live.Parts.Push(impulse, force: false);
            return;
        }

        var body = DynamicBody(entity, impulse, nameof(impulse));
        Jolt.AddImpulse(in body, in impulse);
    }

    /// <summary>
    /// Pushes a dynamic body at a point in the world, so it turns as well as moves when the point is off centre. On a
    /// simulated ragdoll, the part nearest the point takes it.
    /// </summary>
    public void Push(Entity entity, Vector3 impulse, Vector3 point)
    {
        Numbers.RequireFinite(impulse, nameof(impulse));
        Numbers.RequireFinite(point, nameof(point));
        if (_ragdolls.Simulated(entity) is { } live)
        {
            Jolt.AddImpulse(in live.Parts.Ids[live.Parts.Nearest(point)], in impulse, in point);
            return;
        }

        var body = DynamicBody(entity, impulse, nameof(impulse));
        Jolt.AddImpulse(in body, in impulse, in point);
    }

    /// <summary>
    /// Pushes a dynamic body steadily through the next step, in newtons, such as for a thruster or wind. Call it every
    /// tick it should keep pushing. A simulated ragdoll's parts share it by mass.
    /// </summary>
    public void AddForce(Entity entity, Vector3 force)
    {
        Numbers.RequireFinite(force, nameof(force));
        if (_ragdolls.Simulated(entity) is { } live)
        {
            live.Parts.Push(force, force: true);
            return;
        }

        var body = DynamicBody(entity, force, nameof(force));
        Jolt.AddForce(in body, in force);
    }

    /// <summary>Twists a dynamic body through the next step, in newton-metres, around the torque's axis. On a ragdoll, its pelvis.</summary>
    public void AddTorque(Entity entity, Vector3 torque)
    {
        Numbers.RequireFinite(torque, nameof(torque));
        if (_ragdolls.Simulated(entity) is { } live)
        {
            Jolt.AddTorque(in live.Parts.Ids[0], in torque);
            return;
        }

        var body = DynamicBody(entity, torque, nameof(torque));
        Jolt.AddTorque(in body, in torque);
    }

    /// <summary>The entity's dynamic body, syncing first if it was only just given one.</summary>
    private BodyID DynamicBody(Entity entity, Vector3 value, string name)
    {
        _guard.ThrowIfDisposed();
        Numbers.RequireFinite(value, name);
        if (!_bodies.TryGet(entity, out _) && _world.IsAlive(entity) && _world.Has<Collider>(entity))
        {
            _sync();
        }

        if (!_bodies.TryGet(entity, out var tracked) || tracked.Settings.Kind != BodyKind.Dynamic)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no dynamic Body; only dynamic bodies can be pushed or given a velocity.");
        }

        return tracked.Body;
    }
}
