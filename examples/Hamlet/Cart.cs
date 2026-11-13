using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// A hand cart that rolls round the ring road all day and night, following the ground up
/// and down, its wheels turning as it goes. At night a lantern hangs from its front.
/// </summary>
internal sealed class Cart
{
    /// <summary>How fast the cart rolls, in metres a second.</summary>
    public const float Speed = 2.6f;

    private const float WheelRadius = 0.45f;
    private const float Track = 0.8f;

    private readonly ModelRef _body;
    private readonly ModelRef _sides;
    private readonly ModelRef _wheel;

    public Cart(Content content, Palette palette)
    {
        var body = new MeshBuilder();
        // The bed, the shafts out front, and the posts the lantern hangs from.
        body.Span(new Vector3(-0.65f, 0.45f, -0.9f), new Vector3(0.65f, 0.55f, 0.9f), Matrix4x4.Identity);
        foreach (float x in (ReadOnlySpan<float>)[-0.5f, 0.5f])
        {
            body.Span(new Vector3(x - 0.04f, 0.5f, 0.9f), new Vector3(x + 0.04f, 0.58f, 2.3f), Matrix4x4.Identity);
        }

        body.Box(new Vector3(0.1f, 0.1f, 1.8f), Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 0.42f, 0f));
        body.Span(new Vector3(0.48f, 0.55f, 0.82f), new Vector3(0.56f, 1.7f, 0.9f), Matrix4x4.Identity);
        body.Span(new Vector3(0.2f, 1.62f, 0.82f), new Vector3(0.56f, 1.7f, 0.9f), Matrix4x4.Identity);
        _body = content.Mesh(body.Build(palette.Wood));

        var sides = new MeshBuilder();
        sides.Span(new Vector3(-0.65f, 0.55f, -0.9f), new Vector3(-0.58f, 1.0f, 0.9f), Matrix4x4.Identity);
        sides.Span(new Vector3(0.58f, 0.55f, -0.9f), new Vector3(0.65f, 1.0f, 0.9f), Matrix4x4.Identity);
        sides.Span(new Vector3(-0.58f, 0.55f, -0.9f), new Vector3(0.58f, 1.0f, -0.83f), Matrix4x4.Identity);
        sides.Span(new Vector3(-0.58f, 0.55f, 0.83f), new Vector3(0.58f, 0.85f, 0.9f), Matrix4x4.Identity);
        _sides = content.Mesh(sides.Build(palette.Accent));

        // A wheel: a rim with spokes, turning about x.
        var wheel = new MeshBuilder();
        wheel.Add(Shapes.Cylinder(WheelRadius, WheelRadius, 0.08f, 20), Matrix4x4.CreateRotationZ(MathF.PI / 2f));
        for (int i = 0; i < 3; i++)
        {
            var spoke = Matrix4x4.CreateRotationX(i * MathF.PI / 3f);
            wheel.Box(new Vector3(0.1f, 0.05f, WheelRadius * 2.1f), spoke);
        }

        _wheel = content.Mesh(wheel.Build(palette.Wood with { Color = new Vector3(0.2f, 0.13f, 0.08f) }));
    }

    /// <summary>Where the cart is at a moment, its frame on the ground.</summary>
    public static Matrix4x4 Placement(Village village, float seconds)
    {
        var ring = village.Ring;
        float distance = seconds * Speed % ring.Length;
        var at = ring.PointAtDistance(distance);
        var ahead = ring.PointAtDistance((distance + 1f) % ring.Length);
        var behind = ring.PointAtDistance((distance - 1f + ring.Length) % ring.Length);
        ahead.Y = village.Height(ahead.X, ahead.Z);
        behind.Y = village.Height(behind.X, behind.Z);
        at.Y = (ahead.Y + behind.Y) * 0.5f;
        var forward = Vector3.Normalize(ahead - behind);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        // Local +x is to the left of +z, matching a building's frame.
        var left = -right;
        return new Matrix4x4(
            left.X, left.Y, left.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            at.X, at.Y, at.Z, 1f);
    }

    /// <summary>The lantern's place on the cart, in its own space.</summary>
    public static Vector3 Lantern => new(0.38f, 1.35f, 0.86f);

    public void Draw(View view, int key, Matrix4x4 placement, float seconds, Vector3 accent)
    {
        view.Model(key, _body, placement);
        view.Model(key + 1, _sides, placement, accent);
        float turn = -seconds * Speed / WheelRadius;
        foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
        {
            var wheel = Matrix4x4.CreateRotationX(turn) * Matrix4x4.CreateTranslation(side * Track, WheelRadius, 0f) * placement;
            view.Model(key + (side < 0f ? 2 : 3), _wheel, wheel);
        }
    }
}
