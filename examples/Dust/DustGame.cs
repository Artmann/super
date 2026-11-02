using System.Numerics;
using Super;

namespace Dust;

/// <summary>
/// Trees falling in a clearing, over and over. Dust puffs off the stump as each one goes
/// and billows up along the trunk where it lands. Every puff is worked out from how long
/// ago its tree fell, so pausing or recording shows the same dust.
/// </summary>
public sealed class DustGame : IGame
{
    private const float FallSeconds = 1.1f;
    private const float CycleSeconds = 4f;
    private const float TrunkLength = 6f;
    private const float TrunkWidth = 0.35f;
    private const int GroundKey = 1;
    private const int TreeKey = 100;
    private const int StumpKey = 200;
    private const int DustKey = 1000;

    /// <summary>Earthy dust kicked up off dry ground: tan, fading greyer as it thins.</summary>
    public static readonly ParticleEffect GroundDust = new()
    {
        Count = 40,
        Lifetime = 1.6f,
        EmitSeconds = 0.15f,
        Spawn = ParticleSpawn.Line(),
        Outward = 2.2f,
        Scatter = 0.4f,
        Velocity = new Vector3(0f, 0.6f, 0f),
        Drag = 2.5f,
        Gravity = new Vector3(0f, 0.25f, 0f),
        StartSize = 0.5f,
        EndSize = 1.8f,
        StartColor = new Vector3(0.55f, 0.42f, 0.28f),
        EndColor = new Vector3(0.5f, 0.45f, 0.38f),
        Opacity = 0.6f,
    };

    /// <summary>A smaller puff round the foot of the tree as it gives way.</summary>
    public static readonly ParticleEffect StumpDust = GroundDust with
    {
        Count = 14,
        Lifetime = 1.2f,
        Spawn = ParticleSpawn.Disc(0.3f),
        Outward = 1.2f,
        StartSize = 0.3f,
        EndSize = 1f,
        Opacity = 0.35f,
        Seed = 1,
    };

    private static readonly Tree[] Trees =
    [
        new(new Vector3(-2f, 0f, 1f), 0.3f, 0f),
        new(new Vector3(3f, 0f, -3f), 2.4f, 1.6f),
        new(new Vector3(-5f, 0f, -6f), 4.2f, 2.7f),
    ];

    private long _tick;
    private int _ticksPerSecond = 60;

    public void Load()
    {
    }

    public void Update(GameTime time)
    {
        _tick = time.Tick;
        _ticksPerSecond = time.TicksPerSecond;
    }

    public void Draw(View view)
    {
        view.Camera(new Vector3(4f, 3.2f, 9f), new Vector3(-0.5f, 0.8f, -1.5f), MathF.PI / 4f);
        view.Sun(new Vector3(0.6f, 0.8f, 0.35f), new Vector3(3.4f, 2.9f, 2.3f));
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.25f, 0.45f, 0.85f),
            Horizon = new Vector3(0.85f, 0.75f, 0.6f),
            Ground = new Vector3(0.3f, 0.25f, 0.18f),
            Brightness = 0.6f,
            HazeStart = 20f,
            HazeEnd = 90f,
        });
        view.Shadows(10f, cascades: 2);

        view.Model(GroundKey, ModelRef.Cube, Matrix4x4.CreateScale(60f, 0.1f, 60f) * Matrix4x4.CreateTranslation(0f, -0.05f, 0f), new Vector3(0.2f, 0.26f, 0.1f));
        for (int i = 0; i < Trees.Length; i++)
        {
            DrawTree(view, i, Trees[i]);
        }
    }

    private void DrawTree(View view, int index, in Tree tree)
    {
        // Each cycle has its own number, which names its dust so no two falls look alike.
        long offsetTicks = (long)(tree.Offset * _ticksPerSecond);
        long cycleTicks = (long)(CycleSeconds * _ticksPerSecond);
        long sinceStart = Math.Max(_tick - offsetTicks, 0);
        long cycle = sinceStart / cycleTicks;
        long fellAt = offsetTicks + cycle * cycleTicks;
        float since = view.SecondsSince(fellAt);

        var direction = new Vector3(MathF.Cos(tree.Heading), 0f, MathF.Sin(tree.Heading));
        float fallen = Math.Clamp(since / FallSeconds, 0f, 1f);
        float tilt = fallen * fallen * MathF.PI * 0.5f;
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));

        // A trunk standing on the ground, tipped over about its foot.
        var trunk = Matrix4x4.CreateScale(TrunkWidth, TrunkLength, TrunkWidth)
            * Matrix4x4.CreateTranslation(0f, TrunkLength * 0.5f + TrunkWidth * 0.5f * fallen, 0f)
            * Matrix4x4.CreateFromAxisAngle(axis, tilt)
            * Matrix4x4.CreateTranslation(tree.Foot);
        view.Model(TreeKey + index, ModelRef.Cube, trunk, new Vector3(0.36f, 0.24f, 0.15f));
        view.Model(StumpKey + index, ModelRef.Cube, Matrix4x4.CreateScale(0.45f, 0.3f, 0.45f) * Matrix4x4.CreateTranslation(tree.Foot + new Vector3(0f, 0.15f, 0f)), new Vector3(0.4f, 0.3f, 0.2f));

        int key = DustKey + index * 10_000 + (int)cycle * 2;
        view.Particles(key, StumpDust, tree.Foot, since);

        // Where it lands, along the whole trunk from just past the stump to the crown.
        var from = tree.Foot + direction * 0.6f;
        var to = tree.Foot + direction * TrunkLength;
        view.Particles(key + 1, GroundDust, from, to, since - FallSeconds);
    }

    private readonly record struct Tree(Vector3 Foot, float Heading, float Offset);
}
