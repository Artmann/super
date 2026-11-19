using System.Numerics;
using Rendition;
using Super;

namespace Terrain;

/// <summary>
/// A road that loops round the start over the hills, raised on an embankment, with posts
/// along both sides and a cart driving round it. All of it is made from one spline.
/// </summary>
internal sealed class Road
{
    internal const float Radius = 70f;
    internal const float CartSpeed = 14f;
    private const int Points = 48;
    private const float HalfWidth = 3f;
    private const float Lift = 0.5f;
    private const float PostSpacing = 10f;
    private const float PostOffset = HalfWidth + 0.6f;

    private readonly World _world;
    private readonly Entity _cart;
    private SplineFollower _follower;

    public Road(World world, Content content, Func<float, float, float> height)
    {
        Path = Spline.Through(Ground(height), closed: true);

        // Listed from right to left as seen driving along it, so every face points outwards.
        Vector2[] embankment = [new(HalfWidth + 0.8f, -1.6f), new(HalfWidth, 0f), new(-HalfWidth, 0f), new(-HalfWidth - 0.8f, -1.6f)];
        _world = world;
        world.Spawn(Transform.Identity, new Model(content.Mesh(Shapes.Extrude(Path, embankment, step: 1f, surface: new Surface { Color = new Vector3(0.16f, 0.16f, 0.17f), Roughness = 0.85f }))));
        world.Spawn(Transform.Identity, new Model(content.Mesh(Shapes.Ribbon(Lifted(Path, 0.02f), 0.18f, step: 1f, surface: new Surface { Color = new Vector3(0.95f, 0.85f, 0.35f), Roughness = 0.6f }))));

        var post = content.Mesh(Shapes.Box(new Vector3(0.18f, 1.1f, 0.18f), new Surface { Color = new Vector3(0.92f, 0.92f, 0.9f), Roughness = 0.7f }));
        foreach (var pose in Path.Scatter(PostSpacing))
        {
            world.Spawn(Transform.At(pose.Position + pose.Right * PostOffset + new Vector3(0f, 0.55f, 0f)), new Model(post));
            world.Spawn(Transform.At(pose.Position - pose.Right * PostOffset + new Vector3(0f, 0.55f, 0f)), new Model(post));
        }

        // The cart rides the road, and its box sits on it, half its height above the middle.
        _follower = new SplineFollower(Path, CartSpeed, SplineLoop.Loop);
        var cart = content.Mesh(Shapes.Box(new Vector3(1.8f, 1.3f, 3.2f), new Surface { Color = new Vector3(0.8f, 0.12f, 0.08f), Roughness = 0.4f, Metallic = 0.3f }));
        _cart = world.Spawn(Transform.From(_follower.Pose));
        world.Spawn(Transform.At(new Vector3(0f, 0.65f, 0f)), new Model(cart), new Parent(_cart));
    }

    /// <summary>The middle of the road.</summary>
    public Spline Path { get; }

    /// <summary>Where the cart is now.</summary>
    public Vector3 Cart => _follower.Position;

    /// <summary>How far round the cart has driven, in metres.</summary>
    public float CartDistance => _follower.Distance;

    public void Update(GameTime time)
    {
        _follower.Update(time);
        _world.Get<Transform>(_cart) = Transform.From(_follower.Pose);
    }

    /// <summary>
    /// A wobbly ring of points round the start, each lifted clear of the ground under the
    /// road's whole width and eased towards its neighbours so the road doesn't jolt.
    /// </summary>
    private static Vector3[] Ground(Func<float, float, float> height)
    {
        var flat = new Vector2[Points];
        var clear = new float[Points];
        for (int i = 0; i < Points; i++)
        {
            float angle = 2f * MathF.PI * i / Points;
            float radius = Radius + 14f * MathF.Sin(3f * angle);
            flat[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        for (int i = 0; i < Points; i++)
        {
            var along = Vector2.Normalize(flat[(i + 1) % Points] - flat[(i + Points - 1) % Points]);
            var across = new Vector2(-along.Y, along.X) * HalfWidth;
            clear[i] = MathF.Max(height(flat[i].X, flat[i].Y), MathF.Max(
                height(flat[i].X + across.X, flat[i].Y + across.Y),
                height(flat[i].X - across.X, flat[i].Y - across.Y))) + Lift;
        }

        var points = new Vector3[Points];
        for (int i = 0; i < Points; i++)
        {
            float eased = (clear[(i + Points - 1) % Points] + 2f * clear[i] + clear[(i + 1) % Points]) / 4f;
            points[i] = new Vector3(flat[i].X, MathF.Max(eased, clear[i]), flat[i].Y);
        }

        return points;
    }

    private static Spline Lifted(Spline path, float by) =>
        Spline.Through([.. path.Points!.Select(point => point + new Vector3(0f, by, 0f))], closed: true);
}
