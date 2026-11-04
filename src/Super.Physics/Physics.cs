using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;
using JoltRay = JoltPhysicsSharp.Ray;

namespace Super.Physics;

/// <summary>
/// Asks which entities' <see cref="Collider"/>s a ray meets. Before each Update, the colliders are moved to where
/// their entities are, so a query in Update sees the world as it was last drawn. Call <see cref="Sync"/> to bring
/// them up to date after moving things within a tick, or before the first tick. Built on Jolt Physics, which gives
/// the same answers for the same calls in the same build.
/// </summary>
public sealed class Physics : ITickHook, IDisposable
{
    /// <summary>Every layer, for queries that skip none.</summary>
    public const uint AllLayers = 0xFFFF;

    /// <summary>How far a ray reaches when no distance is given, in metres.</summary>
    public const float DefaultReach = 1000f;

    private const int MaxBodies = 65536;
    private static readonly Lock Starting = new();
    private static bool _started;

    private readonly World _world;
    private readonly ObjectLayerPairFilterMask _pairs;
    private readonly BroadPhaseLayerInterfaceMask _broadPhase;
    private readonly ObjectVsBroadPhaseLayerFilterMask _objectVsBroadPhase;
    private readonly PhysicsSystem _system;
    private readonly LayerFilter _layerFilter = new();
    private readonly AliveFilter _aliveFilter;
    private readonly List<RayCastResult> _results = [];
    private readonly List<RayHit> _scratch = [];

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
        public Vector3 Scale;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public Physics(World world)
    {
        _world = world;
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
            ObjectLayerPairFilter = _pairs,
            BroadPhaseLayerInterface = _broadPhase,
            ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhase,
        });
        _aliveFilter = new AliveFilter(this);
    }

    /// <summary>How many colliders there are, as of the last sync.</summary>
    public int Count => _bodies.Count;

    void ITickHook.BeforeUpdate() => Sync();

    /// <summary>
    /// The nearest collider on the given layers that the ray meets within <paramref name="reach"/> metres, or
    /// nothing. A ray starting inside a collider hits it at distance 0. Entities destroyed since the last sync are
    /// skipped.
    /// </summary>
    public RayHit? Raycast(Super.Ray ray, float reach = DefaultReach, uint layers = AllLayers)
    {
        var cast = Cast(ray, reach, layers);
        if (!_system.NarrowPhaseQuery.CastRay(in cast, out var result, objectLayerFilter: _layerFilter, bodyFilter: _aliveFilter))
        {
            return null;
        }

        return Hit(ray, reach, result);
    }

    /// <summary>
    /// Every collider on the given layers that the ray meets within <paramref name="reach"/> metres, nearest first,
    /// into <paramref name="hits"/>, which is cleared first. Hits at the same distance come in entity order, so a
    /// seeded run repeats.
    /// </summary>
    public void RaycastAll(Super.Ray ray, List<RayHit> hits, float reach = DefaultReach, uint layers = AllLayers)
    {
        hits.Clear();
        var cast = Cast(ray, reach, layers);
        _results.Clear();
        _system.NarrowPhaseQuery.CastRay(in cast, new RayCastSettings(), CollisionCollectorType.AllHit, _results, objectLayerFilter: _layerFilter, bodyFilter: _aliveFilter);
        foreach (var result in _results)
        {
            hits.Add(Hit(ray, reach, result));
        }

        // A body can be hit more than once (in and out of a box the ray starts inside); keep its nearest.
        hits.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Entity.Id.CompareTo(b.Entity.Id));
        _scratch.Clear();
        foreach (var hit in hits)
        {
            if (_scratch.Count == 0 || !_scratch.Exists(kept => kept.Entity == hit.Entity))
            {
                _scratch.Add(hit);
            }
        }

        hits.Clear();
        hits.AddRange(_scratch);
    }

    /// <summary>
    /// Moves every collider to where its entity is now, adds new ones and drops those whose entity or
    /// <see cref="Collider"/> is gone. The game loop does this before each Update.
    /// </summary>
    public void Sync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bodies = _system.BodyInterface;
        bool completed = false;
        try
        {
            foreach (var row in _world.Query<Collider, Transform>())
            {
                Entity entity = row.Entity;
                Collider collider = row.First;
                var matrix = ViewExtensions.WorldMatrix(_world, entity, 1f, depth: 0);
                if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position))
                {
                    throw new InvalidOperationException($"Entity {entity.Id}'s Collider can't be placed: its Transform, or a parent's, is sheared or flattened. Check their scales.");
                }

                if (!_bodies.TryGetValue(entity, out var tracked))
                {
                    tracked = Create(entity, collider, scale, position, rotation);
                }
                else
                {
                    if (tracked.Collider != collider || tracked.Scale != scale)
                    {
                        Check(entity, collider);
                        using var shape = Shape(collider, scale);
                        bodies.SetShape(in tracked.Body, shape, false, Activation.DontActivate);
                        if (tracked.Collider.Layers != collider.Layers)
                        {
                            var layer = Layer(collider.Layers);
                            bodies.SetObjectLayer(in tracked.Body, in layer);
                        }

                        tracked.Collider = collider;
                        tracked.Scale = scale;
                    }

                    if (tracked.Position != position || tracked.Rotation != rotation)
                    {
                        bodies.SetPositionAndRotation(in tracked.Body, in position, in rotation, Activation.DontActivate);
                        tracked.Position = position;
                        tracked.Rotation = rotation;
                    }

                    _bodies.Remove(entity);
                }

                _next[entity] = tracked;
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
                    bodies.RemoveAndDestroyBody(in left.Body);
                    _owners.Remove(left.Body.ID);
                }
                else
                {
                    _next[entity] = left;
                }
            }

            _bodies.Clear();
            (_next, _bodies) = (_bodies, _next);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _system.Dispose();
        _aliveFilter.Dispose();
        _layerFilter.Dispose();
        _objectVsBroadPhase.Dispose();
        _broadPhase.Dispose();
        _pairs.Dispose();
    }

    private Tracked Create(Entity entity, Collider collider, Vector3 scale, Vector3 position, Quaternion rotation)
    {
        Check(entity, collider);
        using var shape = Shape(collider, scale);
        using var settings = new BodyCreationSettings(shape, in position, in rotation, MotionType.Kinematic, Layer(collider.Layers));
        var body = _system.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate);
        if (body.IsInvalid)
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s Collider couldn't be added: there are already {_owners.Count} colliders, and {MaxBodies} is the most.");
        }

        _owners[body.ID] = entity;
        return new Tracked { Body = body, Collider = collider, Scale = scale, Position = position, Rotation = rotation };
    }

    private JoltRay Cast(Super.Ray ray, float reach, uint layers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!(reach > 0f) || !float.IsFinite(reach))
        {
            throw new ArgumentOutOfRangeException(nameof(reach), reach, "A ray's reach must be a finite distance above 0.");
        }

        if (layers == 0 || (layers & ~AllLayers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layers), layers, "Layers are bits 0 to 15, and a query needs at least one.");
        }

        _layerFilter.Layers = layers;
        return new JoltRay(ray.Origin, ray.Direction * reach);
    }

    private RayHit Hit(Super.Ray ray, float reach, in RayCastResult result)
    {
        float distance = result.Fraction * reach;
        return new RayHit(_owners[result.BodyID.ID], distance, ray.Origin + ray.Direction * distance);
    }

    private static ObjectLayer Layer(uint layers) => ObjectLayerPairFilterMask.GetObjectLayer(layers, 0);

    private static void Check(Entity entity, Collider collider)
    {
        if (collider.Layers == 0 || (collider.Layers & ~AllLayers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider.Layers, $"Entity {entity.Id}'s Collider is on layers {collider.Layers}; layers are bits 0 to 15, and it needs at least one.");
        }

        if (!float.IsFinite(collider.Offset.X) || !float.IsFinite(collider.Offset.Y) || !float.IsFinite(collider.Offset.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider.Offset, $"Entity {entity.Id}'s Collider has offset {collider.Offset}, which isn't a finite position.");
        }

        switch (collider.Shape)
        {
            case ColliderShape.Ball when !(collider.Radius > 0f) || !float.IsFinite(collider.Radius):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Radius, $"Entity {entity.Id}'s Collider is a ball of radius {collider.Radius}; it must be above 0.");
            case ColliderShape.Box when !(collider.Size.X > 0f && collider.Size.Y > 0f && collider.Size.Z > 0f) || !float.IsFinite(collider.Size.X + collider.Size.Y + collider.Size.Z):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Size, $"Entity {entity.Id}'s Collider is a box of size {collider.Size}; each side must be above 0.");
            case ColliderShape.Capsule when !(collider.Radius > 0f) || !(collider.Size.Y >= collider.Radius * 2f) || !float.IsFinite(collider.Size.Y):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Size.Y, $"Entity {entity.Id}'s Collider is a capsule {collider.Size.Y} high with radius {collider.Radius}; the radius must be above 0 and the height at least twice it.");
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

    /// <summary>Skips the bodies of entities destroyed, or stripped of their collider, since the last sync.</summary>
    private sealed class AliveFilter(Physics physics) : BodyFilter
    {
        protected override bool ShouldCollide(BodyID bodyID) =>
            physics._owners.TryGetValue(bodyID.ID, out var entity) && physics._world.IsAlive(entity) && physics._world.Has<Collider>(entity);

        protected override bool ShouldCollideLocked(Body body) => true;
    }
}
