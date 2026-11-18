using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// One kind of particle, built from nodes the way a particle system is in Unity or
/// Unreal: where they're born, how they start moving, the forces on them and how they
/// look over their lives. Each call adds a node and gives back a new emitter, so a shared
/// one is never changed and a variation is one more call:
/// <code>
/// static readonly ParticleEmitter Dust = new ParticleEmitter(count: 40, lifetime: 1.6f)
///     .FromLine()
///     .Outward(2.2f)
///     .Drag(2.5f)
///     .Gravity(new Vector3(0f, 0.25f, 0f))
///     .Size(0.5f, 1.8f)
///     .Color(tan, grey)
///     .Fade(0.1f, 0.6f);
/// </code>
/// Nothing is simulated from frame to frame: each particle is worked out from the key,
/// its number and the age, so a paused, loaded or recorded game shows the same puff.
/// Draw one with <see cref="View.Particles(int, ParticleEffect, Vector3, float)"/>, or put
/// several on a timeline in a <see cref="ParticleEffect"/>.
/// </summary>
/// <remarks>
/// The timing, the shape, the count, the lifetime and the seed have one setting each, and
/// setting one again replaces it. Every other node stacks: speeds and forces add up,
/// motions apply in turn, and sizes, colors and opacities multiply, starting from one
/// metre wide, white and solid. Spawn shapes and speeds are in the emitter's own space
/// and follow the transform it's drawn with; gravity, wind, sizes and colors are in the
/// world. The nodes are extension methods by stage: <see cref="ParticleSpawning"/> for when
/// and where particles are born, <see cref="ParticleMotion"/> for speeds, forces and motions,
/// and <see cref="ParticleLooks"/> for how they look.
/// </remarks>
public sealed class ParticleEmitter
{
    private const uint ShapeSalt = 64;

    private readonly ParticleNode[] _nodes;
    private readonly ParticleStages _stages;
    private ParticleEffect? _alone;

    /// <summary>An emitter that bursts this many particles from a point, each lasting about this many seconds.</summary>
    /// <param name="lifetimeSpread">How much lifetimes differ, as a fraction: 0.3 gives 70% to 130%. 0 to 0.9.</param>
    public ParticleEmitter(int count, float lifetime, float lifetimeSpread = 0.3f)
        : this(
            new ParticleSettings
            {
                Count = ParticleChecks.Count(count),
                Lifetime = ParticleChecks.Lifetime(lifetime),
                LifetimeSpread = ParticleChecks.Fraction(lifetimeSpread, nameof(lifetimeSpread), 0.9f),
                Birth = BurstNode.Instance,
                Shape = PointNode.Instance,
            },
            [])
    {
    }

    private ParticleEmitter(ParticleSettings settings, ParticleNode[] nodes)
    {
        Settings = settings;
        _nodes = nodes;
        _stages = new ParticleStages(nodes);
    }

    /// <summary>How many particles there are, from 1 to <see cref="Particle.MaxPerFrame"/>.</summary>
    public int Count => Settings.Count;

    /// <summary>How long each particle lasts on average, in seconds.</summary>
    public float Lifetime => Settings.Lifetime;

    public float LifetimeSpread => Settings.LifetimeSpread;

    /// <summary>Seconds from the start until the last particle has gone. Past this, drawing adds nothing.</summary>
    public float Duration => Settings.Birth.LastBirth() + Lifetime * (1f + LifetimeSpread);

    /// <summary>The settings with one value each: the count, the lifetime, the seed, the timing and the shape.</summary>
    internal ParticleSettings Settings { get; }

    /// <summary>An effect of this emitter alone, made once, so drawing an emitter allocates nothing.</summary>
    public static implicit operator ParticleEffect(ParticleEmitter emitter) =>
        emitter._alone ??= new ParticleEffect().Add(emitter);

    /// <summary>Changes how many particles there are.</summary>
    public ParticleEmitter WithCount(int count) => With(Settings with { Count = ParticleChecks.Count(count) });

    /// <summary>Changes how long particles last, and how much that differs, as a fraction from 0 to 0.9.</summary>
    public ParticleEmitter WithLifetime(float lifetime, float spread = 0.3f) =>
        With(Settings with { Lifetime = ParticleChecks.Lifetime(lifetime), LifetimeSpread = ParticleChecks.Fraction(spread, nameof(spread), 0.9f) });

    /// <summary>Changes the pattern for every key, so two emitters that are otherwise alike don't match.</summary>
    public ParticleEmitter Seed(int seed) => With(Settings with { Seed = seed });

    /// <summary>Lists the emitter's nodes in order, which helps when a puff doesn't look as expected.</summary>
    public override string ToString() =>
        $"ParticleEmitter {Count} x {Lifetime} s: {string.Join(" -> ", new ParticleNode[] { Settings.Birth, Settings.Shape }.Concat(_nodes))}";

    /// <summary>
    /// Adds the particles alive at this age. The seed comes from the key and the place in
    /// the effect; the placement says where the emitter is drawn.
    /// </summary>
    internal void Emit(uint seed, float age, in ParticlePlacement placement, List<Particle> into)
    {
        if (!(age >= 0f) || age >= Duration)
        {
            return;
        }

        var settings = Settings;
        uint emitterSeed = ParticleHash.Mix(seed ^ ParticleHash.Mix(unchecked((uint)settings.Seed + 0x632BE5ABu)));
        for (int i = 0; i < settings.Count; i++)
        {
            uint particle = ParticleHash.Mix(emitterSeed + unchecked((uint)i * 0x85EBCA6Bu));
            var core = new ParticleDice(particle, 0);
            float birth = settings.Birth.Birth(i, settings.Count, core.Roll(0));
            float life = settings.Lifetime * (1f + settings.LifetimeSpread * core.Signed(1));
            float t = age - birth;
            if (t < 0f || t >= life)
            {
                continue;
            }

            var (start, outward) = settings.Shape.Start(new ParticleDice(particle, ShapeSalt));
            var position = _stages.PositionAt(particle, t, placement.Stretched(start), outward, placement);
            var look = _stages.AppearanceAt(particle, t, t / life, core.Roll(2) * MathF.Tau);
            into.Add(new Particle
            {
                Position = position,
                Size = look.Size,
                Color = new Vector4(look.Color, look.Opacity),
                Rotation = look.Rotation,
                Variant = core.Roll(3) * 64f,
                Glow = look.Glow,
            });
        }
    }

    /// <summary>The same nodes with other settings, such as another shape.</summary>
    internal ParticleEmitter With(ParticleSettings settings) => new(settings, _nodes);

    /// <summary>The same settings with one more node at the end.</summary>
    internal ParticleEmitter Then(ParticleNode node) => new(Settings, [.. _nodes, node]);
}

/// <summary>What an emitter has one of each: how many particles, how long they last, its seed, when they're born and where.</summary>
internal readonly record struct ParticleSettings
{
    public required int Count { get; init; }

    public required float Lifetime { get; init; }

    public required float LifetimeSpread { get; init; }

    public int Seed { get; init; }

    public required BirthNode Birth { get; init; }

    public required ShapeNode Shape { get; init; }
}
