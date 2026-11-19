using System.Numerics;
using Super;

namespace GlassExample;

/// <summary>
/// Panes of glass standing one behind another in front of coloured posts: a clear one, a
/// frosted one and a green one. Glass balls bend the posts behind them, and dust drifts
/// up between the panes, in front of some and behind others. A shallow pool in front fades
/// out softly where a stone runs into it.
/// </summary>
public sealed class GlassRoom(World world, Content content) : IGame
{
    private const float DustCycleSeconds = 3f;

    /// <summary>Draws all the glass without bending or frosting.</summary>
    public static bool Flat { get; set; }

    /// <summary>Pale dust rising slowly from a patch of floor.</summary>
    private static readonly ParticleEffect Dust = new ParticleEffect().Add(new ParticleEmitter(count: 40, lifetime: 4f)
        .Over(DustCycleSeconds)
        .FromDisc(0.5f)
        .Velocity(new Vector3(0f, 0.35f, 0f))
        .Scatter(0.1f)
        .Wobble(0.15f)
        .Color(new Vector3(0.9f, 0.85f, 0.75f))
        .Opacity(0.5f)
        .Fade(0.2f, 0.5f)
        .Size(0.15f, 0.35f));

    private static readonly Pane[] Panes =
    [
        new(new Vector3(-0.9f, 0.9f, 0.6f), 0.25f, new Surface { Color = new Vector3(0.75f, 0.85f, 0.95f), Roughness = 0.05f, Opacity = 0.12f }),
        new(new Vector3(0.3f, 0.9f, -0.4f), -0.15f, new Surface { Color = new Vector3(0.9f), Roughness = 0.45f, Opacity = 0.2f }),
        new(new Vector3(1.5f, 0.9f, -1.4f), 0.1f, new Surface { Color = new Vector3(0.2f, 0.7f, 0.35f), Roughness = 0.08f, Opacity = 0.35f }),
    ];

    private static readonly (Vector3 Position, Vector3 Color)[] Posts =
    [
        (new Vector3(-2.4f, 0f, -3f), new Vector3(0.8f, 0.15f, 0.1f)),
        (new Vector3(-1.2f, 0f, -3.2f), new Vector3(0.9f, 0.7f, 0.1f)),
        (new Vector3(0f, 0f, -3f), new Vector3(0.15f, 0.35f, 0.85f)),
        (new Vector3(1.2f, 0f, -3.2f), new Vector3(0.9f, 0.9f, 0.85f)),
        (new Vector3(2.4f, 0f, -3f), new Vector3(0.6f, 0.2f, 0.7f)),
    ];

    private readonly record struct Pane(Vector3 Position, float Turn, Surface Surface);

    public void Load()
    {
        world.Spawn(new Camera(new Vector3(0.4f, 1.7f, 5.2f), new Vector3(0.2f, 0.85f, -0.6f), 0.8f));
        world.Spawn(
            new Sun(new Vector3(0.45f, 0.9f, -1.1f), new Vector3(3f, 2.85f, 2.6f)),
            new Sky(new SkySettings
            {
                Zenith = new Vector3(0.3f, 0.45f, 0.75f),
                Horizon = new Vector3(0.75f, 0.75f, 0.7f),
                Ground = new Vector3(0.3f, 0.28f, 0.25f),
                Brightness = 0.6f,
            }),
            new Shadows(10f, 2));
        Room();

        // A new cloud of dust every cycle, each rising for longer than a cycle, so dust is always rising.
        world.Spawn(Transform.At(new Vector3(-0.3f, 0.05f, 0.1f)), new Emitter(Dust, DustCycleSeconds));
    }

    public void Update(GameTime time)
    {
    }

    /// <summary>The floor, the posts, the panes, the balls and the pool.</summary>
    private void Room()
    {
        var chequer = content.Texture(Chequer(64, 16));
        var floor = content.Mesh(Shapes.Plane(new Vector2(20f, 20f), surface: new Surface { ColorTexture = chequer, Color = Vector3.One, Roughness = 0.7f }, uvPerMetre: 0.5f));
        world.Spawn(Transform.Identity, new Model(floor));

        foreach (var (position, color) in Posts)
        {
            world.Spawn(new Transform(position + new Vector3(0f, 1.1f, 0f), Quaternion.Identity, new Vector3(0.45f, 2.2f, 0.45f)), new Model(ModelRef.Cube, color));
        }

        foreach (var pane in Panes)
        {
            var surface = Flat ? pane.Surface with { Roughness = 0.05f } : pane.Surface;
            var model = content.Mesh(Shapes.Box(new Vector3(1.6f, 1.8f, 0.03f), surface));
            world.Spawn(new Transform(pane.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, pane.Turn), Vector3.One), new Model(model));
        }

        float ior = Flat ? 1f : 1.5f;
        var clear = new Surface { Color = new Vector3(0.95f), Roughness = 0.02f, Opacity = 0.08f, Ior = ior };
        world.Spawn(Transform.At(new Vector3(-1.7f, 0.35f, 1.6f)), new Model(content.Mesh(Shapes.Sphere(0.35f, 48, 24, clear))));
        world.Spawn(Transform.At(new Vector3(1.9f, 0.6f, 0.6f)), new Model(content.Mesh(Shapes.Sphere(0.6f, 64, 32, clear))));

        // The water fades out where the stone comes within 0.2 m under its top, instead of meeting it in a hard line.
        var stone = content.Mesh(Shapes.Box(new Vector3(1.4f, 0.3f, 0.5f), new Surface { Color = new Vector3(0.55f, 0.52f, 0.48f), Roughness = 0.8f }));
        var tilt = Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f));
        world.Spawn(new Transform(new Vector3(0.1f, 0.05f, 1.75f), tilt, Vector3.One), new Model(stone));
        var water = new Surface { Color = new Vector3(0.35f, 0.55f, 0.6f), Roughness = 0.05f, Opacity = 0.3f, Ior = ior, SoftEdge = 0.2f };
        world.Spawn(Transform.At(new Vector3(0.1f, 0.12f, 1.75f)), new Model(content.Mesh(Shapes.Plane(new Vector2(2.2f, 1f), surface: water))));
    }

    /// <summary>Light and dark squares, <paramref name="square"/> pixels wide.</summary>
    private static GeneratedTexture Chequer(int size, int square)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte shade = (x / square + y / square) % 2 == 0 ? (byte)190 : (byte)90;
                int at = (y * size + x) * 4;
                pixels[at] = shade;
                pixels[at + 1] = shade;
                pixels[at + 2] = shade;
                pixels[at + 3] = 255;
            }
        }

        return new GeneratedTexture(size, pixels);
    }
}
