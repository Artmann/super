using System.Numerics;
using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Keeps a body for each entity with a <see cref="Collider"/>: each sync adds new ones, moves or rebuilds changed
/// ones and drops those whose entity or collider is gone, and after a step it writes dynamic bodies' places back.
/// </summary>
internal sealed class BodySync(World world, PhysicsSystem system)
{
    private readonly JoltBodies _jolt = new(world, system);
    private readonly List<Entity> _dynamics = [];

    // Each sync takes entities out of _bodies as it meets them and puts them in _next, then swaps the two. Whatever is
    // left in _bodies lost its collider, or its entity is gone, so its body goes.
    private Dictionary<Entity, TrackedBody> _bodies = [];
    private Dictionary<Entity, TrackedBody> _next = [];

    /// <summary>How many colliders there are, as of the last sync, triggers and characters included.</summary>
    public int Count => _bodies.Count;

    /// <summary>The Jolt system the bodies are in.</summary>
    public PhysicsSystem Simulation => system;

    /// <summary>The entity each body belongs to, by body ID.</summary>
    public Dictionary<uint, Entity> Owners => _jolt.Owners;

    /// <summary>The entities with a <see cref="Trigger"/> the last sync met, in entity order.</summary>
    public List<Entity> Triggers { get; } = [];

    /// <summary>
    /// Moves every collider to where its entity is now, adds new ones and drops those whose entity or collider is
    /// gone. With <paramref name="seconds"/> above 0, moved kinematic bodies sweep there over that time.
    /// </summary>
    public void Sync(float seconds)
    {
        bool completed = false;
        _dynamics.Clear();
        Triggers.Clear();
        try
        {
            foreach (var row in world.Query<Collider, Transform>())
            {
                var wanted = Want(row.Entity, row.First, row.Second);
                _next[wanted.Entity] = SyncBody(wanted, seconds);
                if (wanted.Settings.Kind == BodyKind.Dynamic)
                {
                    _dynamics.Add(wanted.Entity);
                }

                if (wanted.Trigger)
                {
                    Triggers.Add(wanted.Entity);
                }
            }

            completed = true;
        }
        finally
        {
            SwapBodies(completed);
        }

        Triggers.Sort(EntityOrder.Comparison);
    }

    /// <summary>Makes, remakes and drops the joints to match the bodies there are now.</summary>
    public void SyncJoints() => _jolt.Joints.Sync(_bodies);

    /// <summary>Whether a step has anything to move: a dynamic body, or a kinematic body sweeping.</summary>
    public bool AnyMoving()
    {
        if (_dynamics.Count > 0)
        {
            return true;
        }

        foreach (var (_, tracked) in _bodies)
        {
            if (tracked.Moving)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Copies each dynamic body's place into its entity's Transform, and remembers it to spot the game moving it.</summary>
    public void WriteDynamics()
    {
        var bodies = system.BodyInterface;
        foreach (var entity in _dynamics)
        {
            ref var tracked = ref Find(entity);
            bodies.GetPosition(in tracked.Body, out var position);
            var rotation = Quaternion.Normalize(bodies.GetRotation(in tracked.Body));
            ref var transform = ref world.Get<Transform>(entity);
            transform.Position = position;
            transform.Rotation = rotation;
            tracked.Position = position;
            tracked.Rotation = rotation;
        }
    }

    public bool TryGet(Entity entity, out TrackedBody tracked) => _bodies.TryGetValue(entity, out tracked);

    /// <summary>The entity's body, by reference. Only for an entity the last sync met.</summary>
    public ref TrackedBody Find(Entity entity) => ref CollectionsMarshal.GetValueRefOrNullRef(_bodies, entity);

    public void Dispose() => _jolt.Joints.Dispose();

    /// <summary>
    /// Ends a sync: destroys the bodies it didn't meet, then makes the bodies it met the ones there are. After a
    /// throw, what wasn't reached stays as it was, rather than counting as removed.
    /// </summary>
    private void SwapBodies(bool completed)
    {
        foreach (var (entity, left) in _bodies)
        {
            if (completed)
            {
                _jolt.Destroy(left);
            }
            else
            {
                _next[entity] = left;
            }
        }

        _bodies.Clear();
        (_next, _bodies) = (_bodies, _next);
    }

    /// <summary>
    /// The body an entity asks for this sync. A dynamic body is placed by its own Transform, since physics moves it in
    /// the world; any other body is placed where its Transform and its parents' put it.
    /// </summary>
    private WantedBody Want(Entity entity, Collider collider, Transform transform)
    {
        var settings = world.Has<Body>(entity) ? world.Get<Body>(entity) : Body.Kinematic;
        bool trigger = world.Has<Trigger>(entity);
        bool character = world.Has<Character>(entity);
        uint mask = trigger ? 0u : character ? world.Get<Character>(entity).CollidesWith : settings.CollidesWith;
        if (settings.Kind == BodyKind.Dynamic)
        {
            CheckDynamic(entity, trigger, character);
            return new WantedBody(entity, collider, settings, trigger, character, mask, transform.Scale, transform.Position, transform.Rotation);
        }

        var matrix = ViewExtensions.WorldMatrix(world, entity, 1f, depth: 0);
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position))
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Collider can't be placed: its Transform, or a parent's, is sheared or flattened. Check their scales.");
        }

        return new WantedBody(entity, collider, settings, trigger, character, mask, scale, position, rotation);
    }

    /// <summary>Creates, rebuilds or updates the entity's body, and takes it out of the bodies the sync hasn't met yet.</summary>
    private TrackedBody SyncBody(in WantedBody wanted, float seconds)
    {
        if (!_bodies.TryGetValue(wanted.Entity, out var tracked))
        {
            return _jolt.Create(wanted);
        }

        if (wanted.NeedsRebuild(tracked))
        {
            _bodies.Remove(wanted.Entity);
            return _jolt.Rebuild(tracked, wanted);
        }

        _jolt.Update(ref tracked, wanted, seconds);
        _bodies.Remove(wanted.Entity);
        return tracked;
    }

    private void CheckDynamic(Entity entity, bool trigger, bool character)
    {
        if (world.Has<Parent>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Parent; physics moves a dynamic body in the world, so it can't follow a parent.");
        }

        if (trigger)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Trigger; a trigger area is kinematic or static.");
        }

        if (character)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Character; a character is moved by Physics.Characters.Move, not simulated.");
        }
    }
}
