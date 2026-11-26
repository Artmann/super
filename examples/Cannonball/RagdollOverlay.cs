using System.Numerics;
using Super;
using Super.Physics;

namespace Cannonball;

/// <summary>
/// Draws a ragdoll's bodies as debug wireframes in the "bodies" channel: orange where physics moves them, blue where
/// the animation does. They show through the world, so a body inside a crate still shows.
/// </summary>
public sealed class RagdollOverlay(Physics physics)
{
    public const string Channel = "bodies";

    private static readonly Color Simulated = new(1f, 0.45f, 0.1f);
    private static readonly Color Animated = new(0.2f, 0.5f, 1f);
    private static readonly DebugLook Look = new(Channel, OnTop: true);

    private readonly List<RagdollShapeView> _shapes = [];

    public void Draw(View view, Entity entity)
    {
        _shapes.Clear();
        physics.Ragdolls.Shapes(entity, _shapes);
        var top = Vector3.Zero;
        bool simulated = false;
        for (int i = 0; i < _shapes.Count; i++)
        {
            var shape = _shapes[i];
            Draw(view.Debug, shape);
            if (i == 0 || shape.Transform.Translation.Y > top.Y)
            {
                top = shape.Transform.Translation;
            }

            simulated |= shape.Simulated;
        }

        if (_shapes.Count > 0)
        {
            view.Debug.Text(top + new Vector3(0f, 0.4f, 0f), simulated ? "ragdoll" : "animated", simulated ? Simulated : Animated, Look);
        }
    }

    private static void Draw(ViewDebug debug, in RagdollShapeView shape)
    {
        var color = shape.Simulated ? Simulated : Animated;
        switch (shape.Shape)
        {
            case RagdollShape.Box:
                debug.Box(Matrix4x4.CreateScale(shape.HalfSize * 2f) * shape.Transform, color, Look);
                break;
            case RagdollShape.Ball:
                debug.Sphere(shape.Transform.Translation, shape.Radius, color, Look);
                break;
            default:
                // A capsule runs along its own y, with half its straight middle on each side.
                var along = Vector3.TransformNormal(new Vector3(0f, shape.HalfSize.Y, 0f), shape.Transform);
                var middle = shape.Transform.Translation;
                debug.Capsule(middle - along, middle + along, shape.Radius, color, Look);
                break;
        }
    }
}
