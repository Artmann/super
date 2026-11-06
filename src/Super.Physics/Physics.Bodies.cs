using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

public sealed partial class Physics
{
    /// <summary>How fast an entity's body moves, in metres per second. 0 for one that isn't moving or has no body yet.</summary>
    public Vector3 Velocity(Entity entity) =>
        _bodies.TryGetValue(entity, out var tracked) ? _system.BodyInterface.GetLinearVelocity(in tracked.Body) : Vector3.Zero;

    /// <summary>How fast an entity's body turns, as an axis whose length is radians per second.</summary>
    public Vector3 AngularVelocity(Entity entity) =>
        _bodies.TryGetValue(entity, out var tracked) ? _system.BodyInterface.GetAngularVelocity(in tracked.Body) : Vector3.Zero;

    /// <summary>Sets a dynamic body's velocity, in metres per second, such as for a jump or a throw.</summary>
    public void SetVelocity(Entity entity, Vector3 velocity)
    {
        var body = DynamicBody(entity, velocity, nameof(velocity));
        _system.BodyInterface.SetLinearVelocity(in body, in velocity);
        _system.BodyInterface.ActivateBody(in body);
    }

    /// <summary>Sets how fast a dynamic body turns, as an axis whose length is radians per second.</summary>
    public void SetAngularVelocity(Entity entity, Vector3 spin)
    {
        var body = DynamicBody(entity, spin, nameof(spin));
        _system.BodyInterface.SetAngularVelocity(in body, in spin);
        _system.BodyInterface.ActivateBody(in body);
    }

    /// <summary>
    /// Gives a dynamic body a sudden push, in newton-seconds, through its centre: the change in velocity is the
    /// impulse over the mass. Use it for a kick, a hit or an explosion.
    /// </summary>
    public void Push(Entity entity, Vector3 impulse)
    {
        var body = DynamicBody(entity, impulse, nameof(impulse));
        _system.BodyInterface.AddImpulse(in body, in impulse);
    }

    /// <summary>Pushes a dynamic body at a point in the world, so it turns as well as moves when the point is off centre.</summary>
    public void Push(Entity entity, Vector3 impulse, Vector3 point)
    {
        var body = DynamicBody(entity, impulse, nameof(impulse));
        CheckFinite(point, nameof(point));
        _system.BodyInterface.AddImpulse(in body, in impulse, in point);
    }

    /// <summary>
    /// Pushes a dynamic body steadily through the next step, in newtons, such as for a thruster or wind. Call it every
    /// tick it should keep pushing.
    /// </summary>
    public void AddForce(Entity entity, Vector3 force)
    {
        var body = DynamicBody(entity, force, nameof(force));
        _system.BodyInterface.AddForce(in body, in force);
    }

    /// <summary>Twists a dynamic body through the next step, in newton-metres, around the torque's axis.</summary>
    public void AddTorque(Entity entity, Vector3 torque)
    {
        var body = DynamicBody(entity, torque, nameof(torque));
        _system.BodyInterface.AddTorque(in body, in torque);
    }

    /// <summary>The entity's dynamic body, syncing first if it was only just given one.</summary>
    private BodyID DynamicBody(Entity entity, Vector3 value, string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckFinite(value, name);
        if (!_bodies.ContainsKey(entity) && _world.IsAlive(entity) && _world.Has<Collider>(entity))
        {
            Sync();
        }

        if (!_bodies.TryGetValue(entity, out var tracked) || tracked.Settings.Kind != BodyKind.Dynamic)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has no dynamic Body; only dynamic bodies can be pushed or given a velocity.");
        }

        return tracked.Body;
    }

    private static void CheckFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(name, value, $"The {name} must be finite.");
        }
    }
}
