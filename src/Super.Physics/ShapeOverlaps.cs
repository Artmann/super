using System.Numerics;
using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Finds the entities a shape touches, for <see cref="PhysicsQueries.Overlap"/> and trigger areas, through
/// <see cref="QueryFilters"/>, without allocating.
/// </summary>
internal sealed class ShapeOverlaps(PhysicsSystem system, Dictionary<uint, Entity> owners, QueryFilters filters) : IDisposable
{
    private readonly List<uint> _collided = [];
    private GCHandle _self;

    /// <summary>The entities touching <paramref name="shape"/> where a body's centre of mass is now.</summary>
    public void CollideAt(Shape shape, in BodyID body, List<Entity> hits) =>
        Collide(shape, system.BodyInterface.GetCenterOfMassTransform(in body), hits);

    /// <summary>
    /// The entities touching <paramref name="shape"/> with its centre of mass at <paramref name="centreOfMass"/>, into
    /// <paramref name="hits"/>, which is cleared first, in entity order.
    /// </summary>
    public unsafe void Collide(Shape shape, Matrix4x4 centreOfMass, List<Entity> hits)
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

        system.NarrowPhaseQuery.CollideShape(shape, in scale, in centreOfMass, in origin, &Collected, GCHandle.ToIntPtr(_self), null!, filters.Layers, filters.Alive, null!);
        foreach (var body in _collided)
        {
            if (owners.TryGetValue(body, out var entity) && !hits.Contains(entity))
            {
                hits.Add(entity);
            }
        }

        hits.Sort(EntityOrder.Comparison);
    }

    public void Dispose()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe float Collected(IntPtr context, CollideShapeResult* result)
    {
        ((ShapeOverlaps)GCHandle.FromIntPtr(context).Target!)._collided.Add(result->BodyID2.ID);

        // Keep looking: no hit is good enough to stop at.
        return float.MaxValue;
    }
}
