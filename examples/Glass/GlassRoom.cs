using System.Numerics;
using Super;

namespace GlassExample;

/// <summary>
/// Panes of glass standing one behind another in front of coloured posts: a clear one, a
/// frosted one and a green one. Glass balls bend the posts behind them, and dust drifts
/// up between the panes, in front of some and behind others.
/// </summary>
public sealed class GlassRoom(Content content) : IGame
{
    private const float DustCycleSeconds = 3f;
    private const int DustKey = 1000;

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

    private readonly List<ModelRef> _panes = [];
    private ModelRef _floor;
    private ModelRef _ball;
    private ModelRef _bigBall;
    private long _tick;
    private int _ticksPerSecond = 60;

    private readonly record struct Pane(Vector3 Position, float Turn, Surface Surface);

    public void Load()
    {
        var chequer = content.Texture(Chequer(64, 16));
        _floor = content.Mesh(Shapes.Plane(new Vector2(20f, 20f), surface: new Surface { ColorTexture = chequer, Color = Vector3.One, Roughness = 0.7f }, uvPerMetre: 0.5f));

        foreach (var pane in Panes)
        {
            var surface = Flat ? pane.Surface with { Roughness = 0.05f } : pane.Surface;
            _panes.Add(content.Mesh(Shapes.Box(new Vector3(1.6f, 1.8f, 0.03f), surface)));
        }

        float ior = Flat ? 1f : 1.5f;
        var clear = new Surface { Color = new Vector3(0.95f), Roughness = 0.02f, Opacity = 0.08f, Ior = ior };
        _ball = content.Mesh(Shapes.Sphere(0.35f, 48, 24, clear));
        _bigBall = content.Mesh(Shapes.Sphere(0.6f, 64, 32, clear));
    }

    public void Update(GameTime time)
    {
        _tick = time.Tick;
        _ticksPerSecond = time.TicksPerSecond;
    }

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0.4f, 1.7f, 5.2f), lookAt: new Vector3(0.2f, 0.85f, -0.6f), fieldOfView: 0.8f);
        view.Sun(direction: new Vector3(0.45f, 0.9f, -1.1f), color: new Vector3(3f, 2.85f, 2.6f));
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.3f, 0.45f, 0.75f),
            Horizon = new Vector3(0.75f, 0.75f, 0.7f),
            Ground = new Vector3(0.3f, 0.28f, 0.25f),
            Brightness = 0.6f,
        });
        view.Shadows(10f, cascades: 2);
        view.Model(1, _floor, Matrix4x4.Identity);

        for (int i = 0; i < Posts.Length; i++)
        {
            var (position, color) = Posts[i];
            view.Model(10 + i, ModelRef.Cube, Matrix4x4.CreateScale(0.45f, 2.2f, 0.45f) * Matrix4x4.CreateTranslation(position + new Vector3(0f, 1.1f, 0f)), color);
        }

        for (int i = 0; i < Panes.Length; i++)
        {
            var pane = Panes[i];
            view.Model(20 + i, _panes[i], Matrix4x4.CreateRotationY(pane.Turn) * Matrix4x4.CreateTranslation(pane.Position));
        }

        view.Model(30, _ball, Matrix4x4.CreateTranslation(-1.7f, 0.35f, 1.6f));
        view.Model(31, _bigBall, Matrix4x4.CreateTranslation(1.9f, 0.6f, 0.6f));

        // Two cycles at once, so dust is always rising. Each cycle has its own key.
        long cycleTicks = (long)(DustCycleSeconds * _ticksPerSecond);
        long cycle = _tick / cycleTicks;
        for (long c = Math.Max(cycle - 1, 0); c <= cycle; c++)
        {
            view.Particles(DustKey + (int)c, Dust, new Vector3(-0.3f, 0.05f, 0.1f), view.SecondsSince(c * cycleTicks));
        }
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
