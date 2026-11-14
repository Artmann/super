using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Shapes for queries and trigger checks, kept by what they're made from, so casting the same collider every tick
/// builds nothing.
/// </summary>
internal sealed class QueryShapeCache
{
    private const int Most = 256;
    private readonly Dictionary<(Collider Collider, Vector3 Scale), Shape> _shapes = [];

    /// <summary>The shape for a collider at a scale, built once. Errors name the entity, or else the query.</summary>
    public Shape Get(Collider collider, Vector3 scale, Entity? owner, string query)
    {
        if (_shapes.TryGetValue((collider, scale), out var shape))
        {
            return shape;
        }

        ColliderShapes.Check(owner is { } entity ? $"Entity {entity.Id}'s Collider" : $"The {query} shape", collider);
        if (_shapes.Count >= Most)
        {
            Dispose();
        }

        shape = ColliderShapes.Make(collider, scale);
        _shapes[(collider, scale)] = shape;
        return shape;
    }

    public void Dispose()
    {
        foreach (var (_, shape) in _shapes)
        {
            shape.Dispose();
        }

        _shapes.Clear();
    }
}
