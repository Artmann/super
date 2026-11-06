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
    public const uint AllLayers = 0xFFFF;

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
    private Dictionary<Entity, Tracked> _bodies = [];
    private Dictionary<Entity, Tracked> _next = [];
    private readonly Dictionary<uint, Entity> _owners = [];
    private bool _disposed;

    private struct Tracked
    {
        public BodyID Body;
        public Collider Collider;
        public Body Settings;
        public bool Trigger;
        public bool Character;
        public uint Mask;
        public Vector3 Scale;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Moving;
    }

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
        _aliveFilter = new AliveFilter(this);
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
        if (seconds > 0f && _dynamics.Count + KinematicsMoving() > 0)
        {
            _jobs ??= new JobSystemThreadPool();
            int steps = Math.Max(1, (int)MathF.Ceiling((seconds / LongestStep) - 0.001f));
            PhysicsUpdateError error;
            lock (Scratch)
            {
                error = _system.Update(seconds, steps, _jobs);
            }

            if (error != PhysicsUpdateError.None)
            {
                throw new InvalidOperationException($"The physics step ran out of room ({error}); there are too many bodies touching at once.");
            }

            WriteDynamics();
        }

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
        DisposeQueryShapes();
        FreeHandle();
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
            // After a throw, what wasn't reached stays as it was, rather than counting as removed.
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

        DropCharacters();
        _triggers.Sort(EntityOrder.Comparison);
    }

    private void Update(Entity entity, ref Tracked tracked, Collider collider, uint mask, Vector3 scale, Vector3 position, Quaternion rotation, float seconds)
    {
        var bodies = _system.BodyInterface;
        bool dynamic = tracked.Settings.Kind == BodyKind.Dynamic;
        if (tracked.Collider != collider || tracked.Scale != scale)
        {
            Check($"Entity {entity.Id}'s Collider", collider);
            using var shape = Shape(collider, scale);
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

    private Tracked Create(Entity entity, Collider collider, Body body, bool trigger, bool character, uint mask, Vector3 scale, Vector3 position, Quaternion rotation)
    {
        Check($"Entity {entity.Id}'s Collider", collider);
        CheckBody(entity, body);
        using var shape = Shape(collider, scale);
        var motion = body.Kind switch
        {
            BodyKind.Dynamic => MotionType.Dynamic,
            BodyKind.Static => MotionType.Static,
            _ => MotionType.Kinematic,
        };
        using var settings = new BodyCreationSettings(shape, in position, in rotation, motion, Layer(collider.Layers, mask))
        {
            Friction = body.Friction,
            Restitution = body.Bounciness,
            IsSensor = trigger,
        };
        if (body.Kind == BodyKind.Dynamic)
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

        var id = _system.BodyInterface.CreateAndAddBody(settings, body.Kind == BodyKind.Dynamic ? Activation.Activate : Activation.DontActivate);
        if (id.IsInvalid)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Collider couldn't be added: there are already {_owners.Count} colliders, and {MaxBodies} is the most.");
        }

        _owners[id.ID] = entity;
        return new Tracked
        {
            Body = id,
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

    private void Destroy(in Tracked tracked)
    {
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

    private static void CheckBody(Entity entity, Body body)
    {
        if ((body.CollidesWith & ~AllLayers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.CollidesWith, $"Entity {entity.Id}'s Body collides with layers {body.CollidesWith}; layers are bits 0 to 15.");
        }

        if (!(body.Friction >= 0f) || !float.IsFinite(body.Friction))
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

        if (!(body.Mass > 0f) || !float.IsFinite(body.Mass))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Mass, $"Entity {entity.Id}'s Body has mass {body.Mass}; it must be above 0.");
        }

        if (!(body.Drag >= 0f) || !float.IsFinite(body.Drag) || !(body.AngularDrag >= 0f) || !float.IsFinite(body.AngularDrag))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.Drag, $"Entity {entity.Id}'s Body has drag {body.Drag} and angular drag {body.AngularDrag}; both must be 0 or more.");
        }

        if (!float.IsFinite(body.GravityScale))
        {
            throw new ArgumentOutOfRangeException(nameof(body), body.GravityScale, $"Entity {entity.Id}'s Body has gravity scale {body.GravityScale}; it must be finite.");
        }
    }

    private static ObjectLayer Layer(uint layers, uint mask) => ObjectLayerPairFilterMask.GetObjectLayer(layers, mask);

    private static void Check(string what, Collider collider)
    {
        if (collider.Layers == 0 || (collider.Layers & ~AllLayers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider.Layers, $"{what} is on layers {collider.Layers}; layers are bits 0 to 15, and it needs at least one.");
        }

        if (!float.IsFinite(collider.Offset.X) || !float.IsFinite(collider.Offset.Y) || !float.IsFinite(collider.Offset.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider.Offset, $"{what} has offset {collider.Offset}, which isn't a finite position.");
        }

        switch (collider.Shape)
        {
            case ColliderShape.Ball when !(collider.Radius > 0f) || !float.IsFinite(collider.Radius):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Radius, $"{what} is a ball of radius {collider.Radius}; it must be above 0.");
            case ColliderShape.Box when !(collider.Size.X > 0f && collider.Size.Y > 0f && collider.Size.Z > 0f) || !float.IsFinite(collider.Size.X + collider.Size.Y + collider.Size.Z):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Size, $"{what} is a box of size {collider.Size}; each side must be above 0.");
            case ColliderShape.Capsule when !(collider.Radius > 0f) || !(collider.Size.Y >= collider.Radius * 2f) || !float.IsFinite(collider.Size.Y):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Size.Y, $"{what} is a capsule {collider.Size.Y} high with radius {collider.Radius}; the radius must be above 0 and the height at least twice it.");
        }
    }

    /// <summary>The collider's shape in its entity's space, scaled with it.</summary>
    private static Shape Shape(Collider collider, Vector3 scale)
    {
        Shape shape;
        Vector3 centre = collider.Offset;
        switch (collider.Shape)
        {
            case ColliderShape.Ball:
                shape = new SphereShape(collider.Radius);
                break;
            case ColliderShape.Box:
                var half = collider.Size / 2f;
                shape = new BoxShape(in half, MathF.Min(Foundation.DefaultConvexRadius, MathF.Min(half.X, MathF.Min(half.Y, half.Z)) / 2f));
                break;
            default:
                float cylinder = (collider.Size.Y / 2f) - collider.Radius;
                shape = cylinder > 0f ? new CapsuleShape(cylinder, collider.Radius) : new SphereShape(collider.Radius);
                centre += new Vector3(0f, collider.Size.Y / 2f, 0f);
                break;
        }

        if (centre != Vector3.Zero)
        {
            var rotation = Quaternion.Identity;
            var moved = new RotatedTranslatedShape(in centre, in rotation, shape);
            shape.Dispose();
            shape = moved;
        }

        if (scale != Vector3.One)
        {
            var valid = shape.MakeScaleValid(in scale);
            var scaled = new ScaledShape(shape, in valid);
            shape.Dispose();
            shape = scaled;
        }

        return shape;
    }

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
            && physics._world.Has<Collider>(entity)
            && !physics._world.Has<Trigger>(entity);

        protected override bool ShouldCollideLocked(JoltPhysicsSharp.Body body) => true;
    }
}
