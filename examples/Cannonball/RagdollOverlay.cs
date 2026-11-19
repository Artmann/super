using System.Numerics;
using Super;
using Super.Physics;

namespace Cannonball;

/// <summary>Draws a ragdoll's bodies in their places: orange where physics moves them, blue where the animation does.</summary>
public sealed class RagdollOverlay(Physics physics)
{
    // Above any entity's ID, so these keys never meet the ones the world gives entities.
    private const int BodiesKey = 1_000_100;

    private readonly List<RagdollShapeView> _shapes = [];

    public void Draw(View view, Entity entity)
    {
        _shapes.Clear();
        physics.Ragdolls.Shapes(entity, _shapes);
        for (int i = 0; i < _shapes.Count; i++)
        {
            var shape = _shapes[i];
            var size = shape.Shape switch
            {
                RagdollShape.Box => shape.HalfSize * 2f,
                RagdollShape.Ball => new Vector3(shape.Radius * 2f),
                _ => new Vector3(shape.Radius * 2f, shape.HalfSize.Y + shape.Radius, shape.Radius * 2f),
            };
            var model = shape.Shape switch
            {
                RagdollShape.Box => ModelRef.Cube,
                RagdollShape.Ball => ModelRef.Sphere,
                _ => ModelRef.Capsule,
            };
            var color = shape.Simulated ? new Vector3(1f, 0.45f, 0.1f) : new Vector3(0.2f, 0.5f, 1f);
            view.Model(BodiesKey + i, model, Matrix4x4.CreateScale(size) * shape.Transform, color);
        }
    }
}
