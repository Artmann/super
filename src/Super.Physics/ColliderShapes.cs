using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>Checks a <see cref="Collider"/> and builds its Jolt shape.</summary>
internal static class ColliderShapes
{
    /// <summary>Checks the collider's layers, offset and size, naming it as <paramref name="what"/> in the error.</summary>
    public static void Check(string what, Collider collider)
    {
        if (collider.Layers == 0 || (collider.Layers & ~LayerBits.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider.Layers, $"{what} is on layers {collider.Layers}; layers are bits 0 to 15, and it needs at least one.");
        }

        if (!Numbers.IsFinite(collider.Offset))
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider.Offset, $"{what} has offset {collider.Offset}, which isn't a finite position.");
        }

        switch (collider.Shape)
        {
            case ColliderShape.Ball when !Numbers.IsAboveZero(collider.Radius):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Radius, $"{what} is a ball of radius {collider.Radius}; it must be above 0.");
            case ColliderShape.Box when !IsBoxSize(collider.Size):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Size, $"{what} is a box of size {collider.Size}; each side must be above 0.");
            case ColliderShape.Capsule when !IsCapsule(collider.Radius, collider.Size.Y):
                throw new ArgumentOutOfRangeException(nameof(collider), collider.Size.Y, $"{what} is a capsule {collider.Size.Y} high with radius {collider.Radius}; the radius must be above 0 and the height at least twice it.");
        }
    }

    private static bool IsBoxSize(Vector3 size) =>
        size.X > 0f && size.Y > 0f && size.Z > 0f && float.IsFinite(size.X + size.Y + size.Z);

    private static bool IsCapsule(float radius, float height) =>
        radius > 0f && height >= radius * 2f && float.IsFinite(height);

    /// <summary>The collider's shape in its entity's space, scaled with it.</summary>
    public static Shape Make(Collider collider, Vector3 scale)
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
}
