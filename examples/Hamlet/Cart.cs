using System.Numerics;
using Rendition;
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

    /// <summary>The bed, the shafts and the lantern's posts.</summary>
    public ModelRef Body { get; }

    /// <summary>The sides, painted in the village's accent.</summary>
    public ModelRef Sides { get; }

    /// <summary>A wheel, which turns about x.</summary>
    public ModelRef Wheel { get; }

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
        Body = content.Mesh(body.Build(palette.Wood));

        var sides = new MeshBuilder();
        sides.Span(new Vector3(-0.65f, 0.55f, -0.9f), new Vector3(-0.58f, 1.0f, 0.9f), Matrix4x4.Identity);
        sides.Span(new Vector3(0.58f, 0.55f, -0.9f), new Vector3(0.65f, 1.0f, 0.9f), Matrix4x4.Identity);
        sides.Span(new Vector3(-0.58f, 0.55f, -0.9f), new Vector3(0.58f, 1.0f, -0.83f), Matrix4x4.Identity);
        sides.Span(new Vector3(-0.58f, 0.55f, 0.83f), new Vector3(0.58f, 0.85f, 0.9f), Matrix4x4.Identity);
        Sides = content.Mesh(sides.Build(palette.Accent));

        // A wheel: a rim with spokes, turning about x.
        var wheel = new MeshBuilder();
        wheel.Add(Shapes.Cylinder(WheelRadius, WheelRadius, 0.08f, 20), Matrix4x4.CreateRotationZ(MathF.PI / 2f));
        for (int i = 0; i < 3; i++)
        {
            var spoke = Matrix4x4.CreateRotationX(i * MathF.PI / 3f);
            wheel.Box(new Vector3(0.1f, 0.05f, WheelRadius * 2.1f), spoke);
        }

        Wheel = content.Mesh(wheel.Build(palette.Wood with { Color = new Vector3(0.2f, 0.13f, 0.08f) }));
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

    /// <summary>A wheel on the cart, on the left (-1) or the right (1) side, turned as far as it has rolled.</summary>
    public static Transform WheelAt(float side, float seconds) => new(
        new Vector3(side * Track, WheelRadius, 0f),
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, -seconds * Speed / WheelRadius),
        Vector3.One);

    /// <summary>The lantern's light, hanging from the cart, at <paramref name="lit"/> of its strength.</summary>
    public static SpotLight LanternLight(Village village, float seconds, float lit)
    {
        var lantern = Vector3.Transform(Lantern, Placement(village, seconds));
        float flicker = BuildingGlow.Wavering(seconds, 7f);
        return new SpotLight(lantern, -Vector3.UnitY, Palette.Lamplight * 3f * lit * flicker, 8f)
        {
            InnerAngle = 0.6f,
            OuterAngle = 1.35f,
            Mode = LightMode.Realtime,
        };
    }
}

/// <summary>The cart's entities in one village: its body, which its sides and wheels hang from, and its lantern's light.</summary>
internal readonly record struct CartEntities(Entity Body, Entity LeftWheel, Entity RightWheel, Entity Lantern)
{
    /// <summary>
    /// Spawns <paramref name="cart"/> where it is at <paramref name="seconds"/>: its body, with its sides and wheels as
    /// children, and its lantern's light. The cart moves, so it stays out of the bakes and is lit by the probes like the
    /// rest of what moves.
    /// </summary>
    public static CartEntities Spawn(World world, Cart cart, Village village, float seconds, Vector3 accent)
    {
        var body = world.Spawn(Transform.From(Cart.Placement(village, seconds)), new Model(cart.Body));
        world.Spawn(Transform.Identity, new Model(cart.Sides, accent), new Parent(body));
        var left = world.Spawn(Cart.WheelAt(-1f, seconds), new Model(cart.Wheel), new Parent(body));
        var right = world.Spawn(Cart.WheelAt(1f, seconds), new Model(cart.Wheel), new Parent(body));
        var lantern = world.Spawn(Cart.LanternLight(village, seconds, 1f), new Hidden());
        var entities = new CartEntities(body, left, right, lantern);
        entities.Roll(world, village, seconds);
        return entities;
    }

    /// <summary>Moves the cart to where it is at <paramref name="seconds"/>, and lights its lantern at dusk.</summary>
    public void Roll(World world, Village village, float seconds)
    {
        world.Get<Transform>(Body) = Transform.From(Cart.Placement(village, seconds));
        world.Get<Transform>(LeftWheel) = Cart.WheelAt(-1f, seconds);
        world.Get<Transform>(RightWheel) = Cart.WheelAt(1f, seconds);

        float lit = 1f - DayCycle.Smooth(-0.06f, 0.02f, DayCycle.Elevation(seconds));
        world.Get<SpotLight>(Lantern) = Cart.LanternLight(village, seconds, lit);
        Spawning.Show(world, Lantern, lit > 0.01f);
    }

    /// <summary>Takes the cart away; its sides and wheels go with its body.</summary>
    public void Destroy(World world)
    {
        world.Destroy(Body);
        world.Destroy(Lantern);
    }
}
