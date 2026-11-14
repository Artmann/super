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
        var bodies = _system.BodyInterface;
        bool completed = false;
        _dynamics.Clear();
        _triggers.Clear();
        try
        {
            foreach (var row in _world.Query<Collider, Transform>())
            {
                Entity entity = row.Entity;
                Collider collider = row.First;
                var settings = _world.Has<Body>(entity) ? _world.Get<Body>(entity) : Body.Kinematic;
                bool trigger = _world.Has<Trigger>(entity);
                bool character = _world.Has<Character>(entity);
                uint mask = trigger ? 0u : character ? _world.Get<Character>(entity).CollidesWith : settings.CollidesWith;
                Vector3 scale;
                Vector3 position;
                Quaternion rotation;
                if (settings.Kind == BodyKind.Dynamic)
                {
                    CheckDynamic(entity, trigger, character);
                    var transform = row.Second;
                    (position, rotation, scale) = (transform.Position, transform.Rotation, transform.Scale);
                }
                else
                {
                    var matrix = ViewExtensions.WorldMatrix(_world, entity, 1f, depth: 0);
                    if (!Matrix4x4.Decompose(matrix, out scale, out rotation, out position))
                    {
                        throw new InvalidOperationException($"Entity {entity.Id}'s Collider can't be placed: its Transform, or a parent's, is sheared or flattened. Check their scales.");
                    }
                }

                if (_bodies.TryGetValue(entity, out var tracked)
                    && (tracked.Settings != settings
                        || tracked.Trigger != trigger
                        || tracked.Character != character
                        || (settings.Kind == BodyKind.Dynamic && (tracked.Collider != collider || tracked.Scale != scale))))
                {
                    // A different kind of body, or a dynamic body's new shape, which must keep the mass asked for: build
                    // it again, keeping a dynamic body's speed.
                    var velocity = Vector3.Zero;
                    var spin = Vector3.Zero;
                    if (tracked.Settings.Kind == BodyKind.Dynamic && settings.Kind == BodyKind.Dynamic)
                    {
                        bodies.GetLinearAndAngularVelocity(in tracked.Body, out velocity, out spin);
                    }

                    Destroy(tracked);
                    _bodies.Remove(entity);
                    tracked = Create(entity, collider, settings, trigger, character, mask, scale, position, rotation);
                    bodies.SetLinearAndAngularVelocity(in tracked.Body, in velocity, in spin);
                }
                else if (!_bodies.ContainsKey(entity))
                {
                    tracked = Create(entity, collider, settings, trigger, character, mask, scale, position, rotation);
                }
                else
                {
                    Update(entity, ref tracked, collider, mask, scale, position, rotation, seconds);
                    _bodies.Remove(entity);
                }

                _next[entity] = tracked;
                if (settings.Kind == BodyKind.Dynamic)
                {
                    _dynamics.Add(entity);
                }

                if (trigger)
                {
                    _triggers.Add(entity);
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

    private void Update(Entity entity, ref TrackedBody tracked, Collider collider, uint mask, Vector3 scale, Vector3 position, Quaternion rotation, float seconds)
    {
        var bodies = _system.BodyInterface;
        bool dynamic = tracked.Settings.Kind == BodyKind.Dynamic;
        if (tracked.Collider != collider || tracked.Scale != scale)
        {
            ColliderShapes.Check($"Entity {entity.Id}'s Collider", collider);
            using var shape = ColliderShapes.Make(collider, scale);
            bodies.SetShape(in tracked.Body, shape, false, Activation.DontActivate);

            tracked.Collider = collider;
            tracked.Scale = scale;
        }

        if (tracked.Collider.Layers != collider.Layers || tracked.Mask != mask)
        {
            var layer = Layer(collider.Layers, mask);
            bodies.SetObjectLayer(in tracked.Body, in layer);
            tracked.Mask = mask;
        }

        bool moved = tracked.Position != position || tracked.Rotation != rotation;
        if (dynamic)
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
            else if (tracked.Moving)
            {
                var still = Vector3.Zero;
                bodies.SetLinearAndAngularVelocity(in tracked.Body, in still, in still);
                tracked.Moving = false;
            }
        }
        else if (moved)
        {
            bodies.SetPositionAndRotation(in tracked.Body, in position, in rotation, Activation.DontActivate);
            if (tracked.Moving)
            {
                var still = Vector3.Zero;
                bodies.SetLinearAndAngularVelocity(in tracked.Body, in still, in still);
                tracked.Moving = false;
            }
        }

        tracked.Position = position;
        tracked.Rotation = rotation;
    }

    private TrackedBody Create(Entity entity, Collider collider, Body body, bool trigger, bool character, uint mask, Vector3 scale, Vector3 position, Quaternion rotation)
    {
        BodySetup.Check(entity, collider, body);
        using var shape = ColliderShapes.Make(collider, scale);
        using var settings = new BodyCreationSettings(shape, in position, in rotation, BodySetup.Motion(body.Kind), Layer(collider.Layers, mask))
        {
            Friction = body.Friction,
            Restitution = body.Bounciness,
            IsSensor = trigger,
        };
        if (body.Kind == BodyKind.Dynamic)
        {
            BodySetup.Simulate(settings, shape, body);
        }

        var handle = _system.BodyInterface.CreateBody(settings);
        if (handle is null || handle.ID.IsInvalid)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Collider couldn't be added: there are already {_owners.Count} colliders, and {MaxBodies} is the most.");
        }

        var id = handle.ID;
        _system.BodyInterface.AddBody(in id, body.Kind == BodyKind.Dynamic ? Activation.Activate : Activation.DontActivate);
        _owners[id.ID] = entity;
        return new TrackedBody
        {
            Body = id,
            Handle = handle,
            Collider = collider,
            Settings = body,
            Trigger = trigger,
            Character = character,
            Mask = mask,
            Scale = scale,
            Position = position,
            Rotation = rotation,
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
