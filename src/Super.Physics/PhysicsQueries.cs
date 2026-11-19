using System.Numerics;
using JoltPhysicsSharp;
using JoltRay = JoltPhysicsSharp.Ray;

namespace Super.Physics;

/// <summary>
/// Asks the physics world what is where: rays, swept shapes and overlaps, as of the last sync. Reach it through
/// <c>physics.Queries</c>. Triggers, and entities destroyed since the last sync, are never found.
/// </summary>
public sealed class PhysicsQueries
{
    /// <summary>How far a ray or shape cast reaches when no distance is given, in metres.</summary>
    public const float DefaultReach = 1000f;

    private readonly DisposalGuard _guard;
    private readonly PhysicsSystem _system;
    private readonly Dictionary<uint, Entity> _owners;
    private readonly QueryShapeCache _shapes;
    private readonly QueryFilters _filters;
    private readonly ShapeOverlaps _overlaps;
    private readonly List<RayCastResult> _rayResults = [];
    private readonly List<ShapeCastResult> _castResults = [];
    private readonly List<RayHit> _rayScratch = [];

    internal PhysicsQueries(DisposalGuard guard, PhysicsSystem system, Dictionary<uint, Entity> owners, QueryParts parts)
    {
        _guard = guard;
        _system = system;
        _owners = owners;
        (_shapes, _filters, _overlaps) = parts;
    }

    /// <summary>
    /// The nearest collider on the given layers that the ray meets within <paramref name="reach"/> metres, or
    /// nothing. A ray starting inside a collider hits it at distance 0. Triggers, and entities destroyed since the
    /// last sync, are skipped.
    /// </summary>
    public RayHit? Raycast(Super.Ray ray, float reach = DefaultReach, uint layers = LayerBits.All)
    {
        var cast = Cast(ray, reach, layers);
        if (!_system.NarrowPhaseQuery.CastRay(in cast, out var result, objectLayerFilter: _filters.Layers, bodyFilter: _filters.Alive))
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
    public void RaycastAll(Super.Ray ray, List<RayHit> hits, float reach = DefaultReach, uint layers = LayerBits.All)
    {
        hits.Clear();
        var cast = Cast(ray, reach, layers);
        _rayResults.Clear();
        _system.NarrowPhaseQuery.CastRay(in cast, new RayCastSettings(), CollisionCollectorType.AllHit, _rayResults, objectLayerFilter: _filters.Layers, bodyFilter: _filters.Alive);
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
        uint layers = LayerBits.All)
    {
        CastShape(new ShapeSweep(shape, from, direction, reach, rotation), layers);
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
        uint layers = LayerBits.All)
    {
        hits.Clear();
        CastShape(new ShapeSweep(shape, from, direction, reach, rotation), layers);
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
    public void Overlap(Collider shape, Vector3 position, List<Entity> hits, Quaternion? rotation = null, uint layers = LayerBits.All)
    {
        hits.Clear();
        var query = _shapes.Get(shape, Vector3.One, null, "Overlap");
        var turn = QueryMath.Turn(rotation);
        _filters.Only(layers);
        var transform = QueryMath.CentreOfMass(query, position, turn);
        _overlaps.Collide(query, transform, hits);
    }

    /// <summary>Frees the shapes and filters the queries and trigger checks share.</summary>
    internal void Release()
    {
        _shapes.Dispose();
        _overlaps.Dispose();
        _filters.Dispose();
    }

    private void CastShape(ShapeSweep sweep, uint layers)
    {
        var (shape, from, direction, reach, rotation) = sweep;
        _guard.ThrowIfDisposed();
        QueryMath.CheckReach(reach);
        if (!(direction.LengthSquared() > 0f) || !float.IsFinite(direction.X + direction.Y + direction.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "A shape cast needs a direction that isn't zero.");
        }

        var query = _shapes.Get(shape, Vector3.One, null, "ShapeCast");
        var turn = QueryMath.Turn(rotation);
        _filters.Only(layers);
        // Unlike CollideShape, Jolt's CastShape takes where the shape's origin is, and finds its centre itself.
        var transform = QueryMath.Placed(from, turn);
        var path = Vector3.Normalize(direction) * reach;
        var origin = Vector3.Zero;
        var settings = new ShapeCastSettings { ReturnDeepestPoint = true };
        _castResults.Clear();
        _system.NarrowPhaseQuery.CastShape(query, in transform, in path, settings, in origin, CollisionCollectorType.AllHit, _castResults, objectLayerFilter: _filters.Layers, bodyFilter: _filters.Alive);
    }

    private ShapeHit CastHit(Vector3 direction, float reach, in ShapeCastResult result)
    {
        var axis = result.PenetrationAxis;
        var normal = axis.LengthSquared() > 0f ? -Vector3.Normalize(axis) : -Vector3.Normalize(direction);
        return new ShapeHit(_owners[result.BodyID2.ID], result.Fraction * reach, result.ContactPointOn2, normal);
    }

    private JoltRay Cast(Super.Ray ray, float reach, uint layers)
    {
        _guard.ThrowIfDisposed();
        QueryMath.CheckReach(reach);
        _filters.Only(layers);
        return new JoltRay(ray.Origin, ray.Direction * reach);
    }

    private RayHit Hit(Super.Ray ray, float reach, in RayCastResult result)
    {
        float distance = result.Fraction * reach;
        return new RayHit(_owners[result.BodyID.ID], distance, ray.Origin + ray.Direction * distance);
    }

    /// <summary>A shape swept from a place along a direction, as a shape cast asks for.</summary>
    private readonly record struct ShapeSweep(Collider Shape, Vector3 From, Vector3 Direction, float Reach, Quaternion? Rotation);
}

/// <summary>The parts <see cref="PhysicsQueries"/> shares with the trigger checks.</summary>
internal readonly record struct QueryParts(QueryShapeCache Shapes, QueryFilters Filters, ShapeOverlaps Overlaps);
