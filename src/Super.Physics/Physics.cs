using System.Numerics;
using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Simulates and queries the entities that have a <see cref="Collider"/>. Before each Update the game loop calls
/// <see cref="Step"/>: colliders move to where their entities are, dynamic <see cref="Body"/>s fall and collide and
/// their Transforms are written, and <see cref="Trigger"/> areas list who came and went. Queries in Update see that
/// world. <see cref="Move"/> walks a <see cref="Character"/> at once. Built on Jolt Physics, which gives the same
/// answers for the same calls in the same build.
/// </summary>
public sealed partial class Physics : ITickHook, IDisposable
{
    /// <summary>Every layer, for queries that skip none.</summary>
    public const uint AllLayers = LayerBits.All;

    /// <summary>How far a ray or shape cast reaches when no distance is given, in metres.</summary>
    public const float DefaultReach = 1000f;

    /// <summary>Earth's gravity, which <see cref="Gravity"/> starts as, in metres per second squared.</summary>
    public static readonly Vector3 EarthGravity = new(0f, -9.81f, 0f);

    // A step longer than this is split, so a slow tick rate doesn't let things tunnel or jitter.
    private const float LongestStep = 1f / 60f;
    private const int MaxBodies = 65536;
    private static readonly Lock Starting = new();

    // joltc gives every physics system one shared scratch allocator, which isn't safe to use from two threads at once.
    // Stepping and walking characters use it, so games running side by side, such as in tests, take turns.
    private static readonly Lock Scratch = new();
    private static bool _started;

    private readonly World _world;
    private readonly GameTime? _time;
    private readonly ObjectLayerPairFilterMask _pairs;
    private readonly BroadPhaseLayerInterfaceMask _broadPhase;
    private readonly ObjectVsBroadPhaseLayerFilterMask _objectVsBroadPhase;
    private readonly PhysicsSystem _system;
    private readonly LayerFilter _layerFilter = new();
    private readonly AliveFilter _aliveFilter;
    private readonly List<Entity> _dynamics = [];
    private JobSystem? _jobs;

    // Each sync takes entities out of _bodies as it meets them and puts them in _next, then swaps the two. Whatever is
    // left in _bodies lost its collider, or its entity is gone, so its body goes.
    private Dictionary<Entity, TrackedBody> _bodies = [];
    private Dictionary<Entity, TrackedBody> _next = [];
    private readonly Dictionary<uint, Entity> _owners = [];
    private readonly JointSet _joints;
    private bool _disposed;

    /// <summary>A world that steps only when <see cref="Step"/> is called, such as in a test.</summary>
    public Physics(World world)
        : this(world, null)
    {
    }

    /// <summary>A world the game loop steps by <paramref name="time"/>'s <see cref="GameTime.DeltaSeconds"/> before each Update.</summary>
    public Physics(World world, GameTime? time)
    {
        _world = world;
        _time = time;
        lock (Starting)
        {
            if (!_started)
            {
                if (!Foundation.Init())
                {
                    throw new InvalidOperationException("Jolt Physics didn't start. Check that joltc.dll is beside the game.");
                }

                _started = true;
            }
        }

        _pairs = new ObjectLayerPairFilterMask();
        _broadPhase = new BroadPhaseLayerInterfaceMask(1);
        _broadPhase.ConfigureLayer(new BroadPhaseLayer(0), uint.MaxValue, 0);
        _objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterMask(_broadPhase);
        _system = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = MaxBodies,
            MaxBodyPairs = MaxBodies,
            MaxContactConstraints = MaxBodies,
            ObjectLayerPairFilter = _pairs,
            BroadPhaseLayerInterface = _broadPhase,
            ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhase,
        });
        _system.Gravity = EarthGravity;
        _contacts = new ContactSet(_owners);
        _system.OnContactAdded += _contacts.Added;
        _joints = new JointSet(world, _system);
        _aliveFilter = new AliveFilter(this);
        _ragdolls = new RagdollSet(world, _system, _owners, (from, reach, layers) => Raycast(new Super.Ray(from, -Vector3.UnitY), reach, layers)?.Point.Y);
    }

    /// <summary>How many colliders there are, as of the last sync, triggers and characters included.</summary>
    public int Count => _bodies.Count;

    /// <summary>The pull on every dynamic body, in metres per second squared. <see cref="EarthGravity"/> to start.</summary>
    public Vector3 Gravity
    {
        get => _system.Gravity;
        set
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Gravity must be finite.");
            }

            _system.Gravity = value;
        }
    }

    void ITickHook.BeforeUpdate()
    {
        if (_time is null)
        {
            Sync();
            return;
        }

        Step(_time.DeltaSeconds);
    }

    /// <summary>
    /// Moves the world on by <paramref name="seconds"/>: syncs the colliders, pushing dynamic bodies out of the way of
    /// kinematic ones that moved, simulates, writes each dynamic body's Transform, and finds what entered and left
    /// each trigger. The game loop does this before each Update with the tick's <see cref="GameTime.DeltaSeconds"/>;
    /// call it yourself only for a Physics you made. 0 simulates nothing but still syncs and checks triggers.
    /// </summary>
    public void Step(float seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!(seconds >= 0f) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A step must be a finite number of seconds, 0 or more.");
        }

        Sync(seconds);
        if (seconds > 0f && (_dynamics.Count + KinematicsMoving() > 0 || _ragdolls.AnyActive()))
        {
            _jobs ??= new JobSystemThreadPool();
            int steps = Math.Max(1, (int)MathF.Ceiling((seconds / LongestStep) - 0.001f));
            PhysicsUpdateError error;
            lock (Scratch)
            {
                error = _ragdolls.Update(seconds, steps, _jobs);
            }

            if (error != PhysicsUpdateError.None)
            {
                throw new InvalidOperationException($"The physics step ran out of room ({error}); there are too many bodies touching at once.");
            }

            WriteDynamics();
            _ragdolls.Write(seconds);
        }

        _contacts.Gather();
        FindTriggerEvents();
    }

    /// <summary>
    /// Moves every collider to where its entity is now, adds new ones and drops those whose entity or
    /// <see cref="Collider"/> is gone. <see cref="Step"/> does this first. Call it in Update to make queries see what
    /// you moved this tick; colliders moved this way jump rather than push.
    /// </summary>
    public void Sync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Sync(0f);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCharacters();
        _ragdolls.Dispose();
        _joints.Dispose();
        _queryShapes.Dispose();
        FreeHandle();
        _system.OnContactAdded -= _contacts.Added;
        _system.Dispose();
        _jobs?.Dispose();
        _aliveFilter.Dispose();
        _layerFilter.Dispose();
        _objectVsBroadPhase.Dispose();
        _broadPhase.Dispose();
        _pairs.Dispose();
    }

    /// <summary>Syncs; with <paramref name="seconds"/> above 0, moved kinematic bodies sweep there over that time.</summary>
    private void Sync(float seconds)
    {
        bool completed = false;
        _dynamics.Clear();
        _triggers.Clear();
        try
        {
            foreach (var row in _world.Query<Collider, Transform>())
            {
                var wanted = Want(row.Entity, row.First, row.Second);
                _next[wanted.Entity] = SyncBody(wanted, seconds);
                if (wanted.Settings.Kind == BodyKind.Dynamic)
                {
                    _dynamics.Add(wanted.Entity);
                }

                if (wanted.Trigger)
                {
                    _triggers.Add(wanted.Entity);
                }
            }

            completed = true;
        }
        finally
        {
            SwapBodies(completed);
        }

        DropCharacters();
        _ragdolls.Sync(seconds);
        _joints.Sync(_bodies);
        _triggers.Sort(EntityOrder.Comparison);
    }

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
                Destroy(left);
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
        var settings = _world.Has<Body>(entity) ? _world.Get<Body>(entity) : Body.Kinematic;
        bool trigger = _world.Has<Trigger>(entity);
        bool character = _world.Has<Character>(entity);
        uint mask = trigger ? 0u : character ? _world.Get<Character>(entity).CollidesWith : settings.CollidesWith;
        if (settings.Kind == BodyKind.Dynamic)
        {
            CheckDynamic(entity, trigger, character);
            return new WantedBody(entity, collider, settings, trigger, character, mask, transform.Scale, transform.Position, transform.Rotation);
        }

        var matrix = ViewExtensions.WorldMatrix(_world, entity, 1f, depth: 0);
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
            return Create(wanted);
        }

        if (wanted.NeedsRebuild(tracked))
        {
            return Rebuild(tracked, wanted);
        }

        Update(ref tracked, wanted, seconds);
        _bodies.Remove(wanted.Entity);
        return tracked;
    }

    /// <summary>Builds the body again, keeping a dynamic body's speed.</summary>
    private TrackedBody Rebuild(in TrackedBody old, in WantedBody wanted)
    {
        var bodies = _system.BodyInterface;
        var velocity = Vector3.Zero;
        var spin = Vector3.Zero;
        if (old.Settings.Kind == BodyKind.Dynamic && wanted.Settings.Kind == BodyKind.Dynamic)
        {
            bodies.GetLinearAndAngularVelocity(in old.Body, out velocity, out spin);
        }

        Destroy(old);
        _bodies.Remove(wanted.Entity);
        var tracked = Create(wanted);
        bodies.SetLinearAndAngularVelocity(in tracked.Body, in velocity, in spin);
        return tracked;
    }

    private void Update(ref TrackedBody tracked, in WantedBody wanted, float seconds)
    {
        var bodies = _system.BodyInterface;
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
            var layer = Layer(collider.Layers, wanted.Mask);
            bodies.SetObjectLayer(in tracked.Body, in layer);
            tracked.Mask = wanted.Mask;
        }

        Place(ref tracked, wanted.Position, wanted.Rotation, seconds);
    }

    /// <summary>
    /// Moves the body to the entity's place. A kinematic body sweeps there over <paramref name="seconds"/> so it
    /// pushes what's in its way; any other body jumps there.
    /// </summary>
    private void Place(ref TrackedBody tracked, Vector3 position, Quaternion rotation, float seconds)
    {
        var bodies = _system.BodyInterface;
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
        _system.BodyInterface.SetLinearAndAngularVelocity(in tracked.Body, in still, in still);
        tracked.Moving = false;
    }

    private TrackedBody Create(in WantedBody wanted)
    {
        var body = wanted.Settings;
        BodySetup.Check(wanted.Entity, wanted.Collider, body);
        using var shape = ColliderShapes.Make(wanted.Collider, wanted.Scale);
        using var settings = new BodyCreationSettings(
            shape, wanted.Position, wanted.Rotation, BodySetup.Motion(body.Kind), Layer(wanted.Collider.Layers, wanted.Mask))
        {
            Friction = body.Friction,
            Restitution = body.Bounciness,
            IsSensor = wanted.Trigger,
        };
        if (body.Kind == BodyKind.Dynamic)
        {
            BodySetup.Simulate(settings, shape, body);
        }

        var handle = _system.BodyInterface.CreateBody(settings);
        if (handle is null || handle.ID.IsInvalid)
        {
            throw new InvalidOperationException($"Entity {wanted.Entity.Id}'s Collider couldn't be added: there are already {_owners.Count} colliders, and {MaxBodies} is the most.");
        }

        var id = handle.ID;
        _system.BodyInterface.AddBody(in id, body.Kind == BodyKind.Dynamic ? Activation.Activate : Activation.DontActivate);
        _owners[id.ID] = wanted.Entity;
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

    private void Destroy(in TrackedBody tracked)
    {
        _joints.LetGoOf(tracked.Body);
        _system.BodyInterface.RemoveAndDestroyBody(in tracked.Body);
        _owners.Remove(tracked.Body.ID);
    }

    private int KinematicsMoving()
    {
        foreach (var (_, tracked) in _bodies)
        {
            if (tracked.Moving)
            {
                return 1;
            }
        }

        return 0;
    }

    /// <summary>Copies each dynamic body's place into its entity's Transform, and remembers it to spot the game moving it.</summary>
    private void WriteDynamics()
    {
        var bodies = _system.BodyInterface;
        foreach (var entity in _dynamics)
        {
            ref var tracked = ref CollectionsMarshal.GetValueRefOrNullRef(_bodies, entity);
            bodies.GetPosition(in tracked.Body, out var position);
            var rotation = Quaternion.Normalize(bodies.GetRotation(in tracked.Body));
            ref var transform = ref _world.Get<Transform>(entity);
            transform.Position = position;
            transform.Rotation = rotation;
            tracked.Position = position;
            tracked.Rotation = rotation;
        }
    }

    private void CheckDynamic(Entity entity, bool trigger, bool character)
    {
        if (_world.Has<Parent>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Parent; physics moves a dynamic body in the world, so it can't follow a parent.");
        }

        if (trigger)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Trigger; a trigger area is kinematic or static.");
        }

        if (character)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Character; a character is moved by Physics.Move, not simulated.");
        }
    }

    private static ObjectLayer Layer(uint layers, uint mask) => ObjectLayerPairFilterMask.GetObjectLayer(layers, mask);

    /// <summary>Lets a query see only colliders on some layers.</summary>
    private sealed class LayerFilter : ObjectLayerFilter
    {
        public uint Layers { get; set; } = AllLayers;

        protected override bool ShouldCollide(ObjectLayer layer) => (ObjectLayerPairFilterMask.GetGroup(in layer) & Layers) != 0;
    }

    /// <summary>
    /// Skips triggers, one entity a query is made for (<see cref="Skip"/>), and the bodies of entities destroyed, or
    /// stripped of their collider, since the last sync.
    /// </summary>
    private sealed class AliveFilter(Physics physics) : BodyFilter
    {
        public Entity? Skip { get; set; }

        protected override bool ShouldCollide(BodyID bodyID) =>
            physics._owners.TryGetValue(bodyID.ID, out var entity)
            && entity != Skip
            && physics._world.IsAlive(entity)
            && (physics._world.Has<Collider>(entity) || physics._world.Has<Ragdoll>(entity))
            && !physics._world.Has<Trigger>(entity);

        protected override bool ShouldCollideLocked(JoltPhysicsSharp.Body body) => true;
    }
}
