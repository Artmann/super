using System.Numerics;
using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using Super.Ecs;
using JoltRay = JoltPhysicsSharp.Ray;

namespace Super.Physics;

public sealed partial class Physics
{
    // Query shapes are kept by what they're made from, so casting the same collider every tick builds nothing.
    private const int MostQueryShapes = 256;
    private readonly Dictionary<(Collider Collider, Vector3 Scale), Shape> _queryShapes = [];
    private readonly List<RayCastResult> _rayResults = [];
    private readonly List<ShapeCastResult> _castResults = [];
    private readonly List<uint> _collided = [];
    private GCHandle _self;
    private readonly List<RayHit> _rayScratch = [];

    /// <summary>
    /// The nearest collider on the given layers that the ray meets within <paramref name="reach"/> metres, or
    /// nothing. A ray starting inside a collider hits it at distance 0. Triggers, and entities destroyed since the
    /// last sync, are skipped.
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
        _rayResults.Clear();
        _system.NarrowPhaseQuery.CastRay(in cast, new RayCastSettings(), CollisionCollectorType.AllHit, _rayResults, objectLayerFilter: _layerFilter, bodyFilter: _aliveFilter);
        foreach (var result in _rayResults)
        {
            hits.Add(Hit(ray, reach, result));
        }

        // A body can be hit more than once (in and out of a box the ray starts inside); keep its nearest.
        hits.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Entity.Id.CompareTo(b.Entity.Id));
        _rayScratch.Clear();
        foreach (var hit in hits)
        {
            if (!_rayScratch.Exists(kept => kept.Entity == hit.Entity))
            {
                _rayScratch.Add(hit);
            }
        }

        hits.Clear();
        hits.AddRange(_rayScratch);
    }

    /// <summary>
    /// Sweeps <paramref name="shape"/> from <paramref name="from"/> along <paramref name="direction"/>, and gives the
    /// first collider on the given layers it meets within <paramref name="reach"/> metres, or nothing. It's a thick
    /// ray: will a ball fit down this corridor, where does a falling crate land. The shape's offset counts from
    /// <paramref name="from"/>, as on an entity, and <paramref name="rotation"/> turns it. A shape that starts
    /// touching something hits it at distance 0. Triggers are skipped.
    /// </summary>
    public ShapeHit? ShapeCast(
        Collider shape,
        Vector3 from,
        Vector3 direction,
        float reach = DefaultReach,
        Quaternion? rotation = null,
        uint layers = AllLayers)
    {
        CastShape(shape, from, direction, reach, rotation, layers);
        ShapeHit? nearest = null;
        foreach (var result in _castResults)
        {
            var hit = CastHit(direction, reach, result);
            if (nearest is not { } best || hit.Distance < best.Distance || (hit.Distance == best.Distance && hit.Entity.Id < best.Entity.Id))
            {
                nearest = hit;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Every collider on the given layers that <paramref name="shape"/> meets, swept as <see cref="ShapeCast"/> does,
    /// nearest first and once each, into <paramref name="hits"/>, which is cleared first.
    /// </summary>
    public void ShapeCastAll(
        Collider shape,
        Vector3 from,
        Vector3 direction,
        List<ShapeHit> hits,
        float reach = DefaultReach,
        Quaternion? rotation = null,
        uint layers = AllLayers)
    {
        hits.Clear();
        CastShape(shape, from, direction, reach, rotation, layers);
        foreach (var result in _castResults)
        {
            hits.Add(CastHit(direction, reach, result));
        }

        // Every collider shape is convex, so Jolt reports each body once.
        hits.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Entity.Id.CompareTo(b.Entity.Id));
    }

    /// <summary>
    /// Every entity on the given layers whose collider touches <paramref name="shape"/> placed at
    /// <paramref name="position"/>, into <paramref name="hits"/>, which is cleared first, in entity order. Use it for
    /// an explosion's reach, or whether there's room to stand up. Triggers are skipped.
    /// </summary>
    public void Overlap(Collider shape, Vector3 position, List<Entity> hits, Quaternion? rotation = null, uint layers = AllLayers)
    {
        hits.Clear();
        var query = QueryShape(shape, Vector3.One, null, "Overlap");
        var turn = Turn(rotation);
        Filter(layers);
        var transform = CentreOfMass(query, position, turn);
        Collide(query, transform, hits);
    }

    private unsafe void Collide(Shape shape, Matrix4x4 centreOfMass, List<Entity> hits)
    {
        hits.Clear();
        var scale = Vector3.One;
        var origin = Vector3.Zero;
        _collided.Clear();

        // The overload that fills a list allocates on every call; a callback that adds body IDs doesn't.
        if (!_self.IsAllocated)
        {
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
        }

        _system.NarrowPhaseQuery.CollideShape(shape, in scale, in centreOfMass, in origin, &Collected, GCHandle.ToIntPtr(_self), null!, _layerFilter, _aliveFilter, null!);
        foreach (var body in _collided)
        {
            if (_owners.TryGetValue(body, out var entity) && !hits.Contains(entity))
            {
                hits.Add(entity);
            }
        }

        hits.Sort(EntityOrder.Comparison);
    }

    [UnmanagedCallersOnly]
    private static unsafe float Collected(IntPtr context, CollideShapeResult* result)
    {
        ((Physics)GCHandle.FromIntPtr(context).Target!)._collided.Add(result->BodyID2.ID);

        // Keep looking: no hit is good enough to stop at.
        return float.MaxValue;
    }

    private void CastShape(Collider shape, Vector3 from, Vector3 direction, float reach, Quaternion? rotation, uint layers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckReach(reach);
        if (!(direction.LengthSquared() > 0f) || !float.IsFinite(direction.X + direction.Y + direction.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "A shape cast needs a direction that isn't zero.");
        }

        var query = QueryShape(shape, Vector3.One, null, "ShapeCast");
        var turn = Turn(rotation);
        Filter(layers);
        // Unlike CollideShape, Jolt's CastShape takes where the shape's origin is, and finds its centre itself.
        var transform = Placed(from, turn);
        var sweep = Vector3.Normalize(direction) * reach;
        var origin = Vector3.Zero;
        var settings = new ShapeCastSettings { ReturnDeepestPoint = true };
        _castResults.Clear();
        _system.NarrowPhaseQuery.CastShape(query, in transform, in sweep, settings, in origin, CollisionCollectorType.AllHit, _castResults, objectLayerFilter: _layerFilter, bodyFilter: _aliveFilter);
    }

    private ShapeHit CastHit(Vector3 direction, float reach, in ShapeCastResult result)
    {
        var axis = result.PenetrationAxis;
        var normal = axis.LengthSquared() > 0f ? -Vector3.Normalize(axis) : -Vector3.Normalize(direction);
        return new ShapeHit(_owners[result.BodyID2.ID], result.Fraction * reach, result.ContactPointOn2, normal);
    }

    private JoltRay Cast(Super.Ray ray, float reach, uint layers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckReach(reach);
        Filter(layers);
        return new JoltRay(ray.Origin, ray.Direction * reach);
    }

    private RayHit Hit(Super.Ray ray, float reach, in RayCastResult result)
    {
        float distance = result.Fraction * reach;
        return new RayHit(_owners[result.BodyID.ID], distance, ray.Origin + ray.Direction * distance);
    }

    private void Filter(uint layers)
    {
        if (layers == 0 || (layers & ~AllLayers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layers), layers, "Layers are bits 0 to 15, and a query needs at least one.");
        }

        _layerFilter.Layers = layers;
        _aliveFilter.Skip = null;
    }

    private static void CheckReach(float reach)
    {
        if (!(reach > 0f) || !float.IsFinite(reach))
        {
            throw new ArgumentOutOfRangeException(nameof(reach), reach, "A query's reach must be a finite distance above 0.");
        }
    }

    private static Quaternion Turn(Quaternion? rotation)
    {
        if (rotation is not { } turn)
        {
            return Quaternion.Identity;
        }

        if (!(turn.LengthSquared() > 0f) || !float.IsFinite(turn.X + turn.Y + turn.Z + turn.W))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "A query's rotation must be a finite quaternion that isn't zero.");
        }

        return Quaternion.Normalize(turn);
    }

    /// <summary>
    /// Where a shape's centre of mass is, turned, with its origin at <paramref name="position"/>: how Jolt places
    /// shapes. Jolt's matrices act on column vectors, with the move in the last column, so this is the transpose of
    /// System.Numerics' own.
    /// </summary>
    private static Matrix4x4 CentreOfMass(Shape shape, Vector3 position, Quaternion rotation) =>
        Placed(position + Vector3.Transform(shape.CenterOfMass, rotation), rotation);

    /// <summary>A turn and a move, as a Jolt matrix.</summary>
    private static Matrix4x4 Placed(Vector3 position, Quaternion rotation)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        matrix.Translation = position;
        return Matrix4x4.Transpose(matrix);
    }

    /// <summary>The shape for a query, or a trigger's check, built once. Errors name the entity, or else the query.</summary>
    private Shape QueryShape(Collider collider, Vector3 scale, Entity? owner, string query)
    {
        if (_queryShapes.TryGetValue((collider, scale), out var shape))
        {
            return shape;
        }

        Check(owner is { } entity ? $"Entity {entity.Id}'s Collider" : $"The {query} shape", collider);
        if (_queryShapes.Count >= MostQueryShapes)
        {
            DisposeQueryShapes();
        }

        shape = Shape(collider, scale);
        _queryShapes[(collider, scale)] = shape;
        return shape;
    }

    private void FreeHandle()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void DisposeQueryShapes()
    {
        foreach (var (_, shape) in _queryShapes)
        {
            shape.Dispose();
        }

        _queryShapes.Clear();
    }
}
