using System.Numerics;
using Rendition;

namespace Super;

/// <summary>Where an effect's particles start, in the effect's own space before its transform.</summary>
public readonly record struct ParticleSpawn
{
    private ParticleSpawn(ParticleSpawnKind kind, Vector3 size)
    {
        Kind = kind;
        Size = size;
    }

    /// <summary>Every particle starts at the middle.</summary>
    public static ParticleSpawn Point { get; } = new(ParticleSpawnKind.Point, Vector3.Zero);

    internal ParticleSpawnKind Kind { get; }

    internal Vector3 Size { get; }

    /// <summary>
    /// Along a line through the middle on the x axis, this many metres long. With the
    /// from-and-to form of <see cref="View.Particles(int, ParticleEffect, Vector3, Vector3, float)"/>,
    /// a line one metre long runs exactly from one end to the other. Outward speed throws
    /// these mostly sideways, off the line.
    /// </summary>
    public static ParticleSpawn Line(float length = 1f) =>
        new(ParticleSpawnKind.Line, new Vector3(ParticleEffect.NotNegative(length, nameof(length)), 0f, 0f));

    /// <summary>Anywhere in a box this many metres across on each axis, centred on the middle.</summary>
    public static ParticleSpawn Box(Vector3 size)
    {
        if (!(size.X >= 0f && size.Y >= 0f && size.Z >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A spawn box can't be smaller than nothing.");
        }

        return new(ParticleSpawnKind.Box, size);
    }

    /// <summary>Anywhere on a flat round patch of ground of this radius, such as round the foot of a tree.</summary>
    public static ParticleSpawn Disc(float radius) =>
        new(ParticleSpawnKind.Disc, new Vector3(ParticleEffect.NotNegative(radius, nameof(radius))));
}

internal enum ParticleSpawnKind
{
    Point,
    Line,
    Box,
    Disc,
}

/// <summary>
/// Describes a burst of soft particles, such as dust or smoke, to draw with
/// <see cref="View.Particles(int, ParticleEffect, Vector3, float)"/>. Nothing is simulated
/// from frame to frame: every particle's place, size and color are worked out from the
/// effect's age and its key, so a paused, reloaded or recorded game shows exactly the same
/// puff. Make one once and keep it; use <c>with</c> to make a variation.
/// </summary>
/// <remarks>
/// Positions, spawn shapes and speeds are in the effect's own space and go through the
/// transform it's drawn with, so a rotated transform turns the burst and a scaled one
/// stretches it. <see cref="Gravity"/>, sizes and colors are in the world, so dust still
/// rises upward and keeps its size however the effect is turned.
/// </remarks>
public sealed record ParticleEffect
{
    /// <summary>How many particles the burst has, from 1 to <see cref="Particle.MaxPerFrame"/>.</summary>
    public int Count
    {
        get;
        init => field = value is >= 1 and <= Particle.MaxPerFrame
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Count), value, $"An effect needs 1 to {Particle.MaxPerFrame} particles.");
    } = 24;

    /// <summary>How long each particle lasts, in seconds.</summary>
    public float Lifetime
    {
        get;
        init => field = value > 0f && float.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Lifetime), value, "A particle's lifetime must be more than zero seconds.");
    } = 1.5f;

    /// <summary>How much lifetimes differ, as a fraction: 0.3 gives 70% to 130% of <see cref="Lifetime"/>. 0 to 0.9.</summary>
    public float LifetimeSpread { get; init => field = Fraction(value, nameof(LifetimeSpread), 0.9f); } = 0.3f;

    /// <summary>
    /// Spreads the particles' births over this many seconds, evenly with a little jitter.
    /// Zero starts them all at once, as a burst.
    /// </summary>
    public float EmitSeconds { get; init => field = NotNegative(value, nameof(EmitSeconds)); }

    /// <summary>Where particles start. A point by default.</summary>
    public ParticleSpawn Spawn { get; init; } = ParticleSpawn.Point;

    /// <summary>A speed every particle starts with, in metres per second.</summary>
    public Vector3 Velocity { get; init => field = Finite(value, nameof(Velocity)); }

    /// <summary>
    /// How fast particles fly out sideways from the middle of the spawn shape, in metres per
    /// second. Each gets 50% to 150% of it. Dust billowing off the ground wants this.
    /// </summary>
    public float Outward { get; init => field = NotNegative(value, nameof(Outward)); }

    /// <summary>The most speed each particle gets in a random direction, in metres per second.</summary>
    public float Scatter { get; init => field = NotNegative(value, nameof(Scatter)); }

    /// <summary>
    /// How quickly the air slows particles down, per second. At 2, a particle has lost
    /// about 86% of its speed after one second. It also caps how fast gravity can move them.
    /// </summary>
    public float Drag { get; init => field = NotNegative(value, nameof(Drag)); }

    /// <summary>A pull in metres per second, per second, in the world. A positive y rises, as warm air does.</summary>
    public Vector3 Gravity { get; init => field = Finite(value, nameof(Gravity)); }

    /// <summary>How wide each particle is when it's born, in metres.</summary>
    public float StartSize { get; init => field = NotNegative(value, nameof(StartSize)); } = 0.3f;

    /// <summary>How wide each particle is when it dies, in metres. It grows fast at first and then slows, as a billow does.</summary>
    public float EndSize { get; init => field = NotNegative(value, nameof(EndSize)); } = 0.8f;

    /// <summary>How much sizes differ, as a fraction: 0.3 gives 70% to 130%. 0 to 0.9.</summary>
    public float SizeSpread { get; init => field = Fraction(value, nameof(SizeSpread), 0.9f); } = 0.3f;

    /// <summary>The color when born, as linear red, green and blue from 0 to 1 like a model's color. The sun and sky light it.</summary>
    public Vector3 StartColor { get; init => field = NotNegative(value, nameof(StartColor)); } = new(0.8f);

    /// <summary>The color when it dies. Colors blend evenly over the particle's life.</summary>
    public Vector3 EndColor { get; init => field = NotNegative(value, nameof(EndColor)); } = new(0.8f);

    /// <summary>How solid the middle of a particle is at its most, from 0 to 1. Overlapping particles add up.</summary>
    public float Opacity { get; init => field = Fraction(value, nameof(Opacity), 1f); } = 0.5f;

    /// <summary>The part of its life a particle takes to fade in, from 0 to 1.</summary>
    public float FadeIn { get; init => field = Fraction(value, nameof(FadeIn), 1f); } = 0.1f;

    /// <summary>The part of its life at the end over which a particle fades out, from 0 to 1.</summary>
    public float FadeOut { get; init => field = Fraction(value, nameof(FadeOut), 1f); } = 0.6f;

    /// <summary>The fastest a particle turns, in radians per second, either way.</summary>
    public float Spin { get; init => field = NotNegative(value, nameof(Spin)); } = 0.5f;

    /// <summary>Changes the pattern for every key. Two effects with different seeds don't look alike at the same key.</summary>
    public int Seed { get; init; }

    /// <summary>Seconds from the start until the last particle has gone. Past this, drawing the effect adds nothing.</summary>
    public float Duration => EmitSeconds + Lifetime * (1f + LifetimeSpread);

    /// <summary>
    /// Adds the particles alive at this age. Each one's randomness comes from the key, its
    /// number and the seed alone, so the same call always gives the same particles.
    /// </summary>
    /// <param name="stretch">Multiplies where particles start along x, so a line can cover a span without speeding up.</param>
    internal void Emit(int key, float age, in Matrix4x4 transform, List<Particle> into, float stretch = 1f)
    {
        if (!(age >= 0f) || age >= Duration)
        {
            return;
        }

        uint callSeed = Hash(unchecked((uint)key * 0x9E3779B9u) ^ Hash((uint)Seed));
        for (int i = 0; i < Count; i++)
        {
            uint particle = Hash(callSeed + unchecked((uint)i * 0x85EBCA6Bu));
            float birth = EmitSeconds * (i + Random(particle, 0)) / Count;
            float life = Lifetime * (1f + LifetimeSpread * (2f * Random(particle, 1) - 1f));
            float t = age - birth;
            if (t < 0f || t >= life)
            {
                continue;
            }

            float f = t / life;
            var (start, outward) = Start(particle);
            start.X *= stretch;
            var velocity = Velocity + outward * (Outward * (0.5f + Random(particle, 5))) + RandomDirection(particle) * (Scatter * Random(particle, 8));

            // Closed form of drag slowing the start speed while gravity pulls: travel is
            // v * (1 - e^-kt) / k, and gravity's share is g * (t - that) / k.
            float travelled;
            float pulled;
            if (Drag > 0f)
            {
                travelled = (1f - MathF.Exp(-Drag * t)) / Drag;
                pulled = (t - travelled) / Drag;
            }
            else
            {
                travelled = t;
                pulled = 0.5f * t * t;
            }

            var position = Vector3.Transform(start, transform)
                + Vector3.TransformNormal(velocity, transform) * travelled
                + Gravity * pulled;

            float grown = 1f - (1f - f) * (1f - f);
            float size = float.Lerp(StartSize, EndSize, grown) * (1f + SizeSpread * (2f * Random(particle, 9) - 1f));
            float fadeIn = FadeIn > 0f ? SmoothStep(f / FadeIn) : 1f;
            float fadeOut = FadeOut > 0f ? 1f - SmoothStep((f - (1f - FadeOut)) / FadeOut) : 1f;
            float spin = Spin * (2f * Random(particle, 11) - 1f);

            into.Add(new Particle
            {
                Position = position,
                Size = size,
                Color = new Vector4(Vector3.Lerp(StartColor, EndColor, f), Opacity * fadeIn * fadeOut),
                Rotation = Random(particle, 10) * MathF.Tau + spin * t,
                Variant = Random(particle, 12) * 64f,
            });
        }
    }

    /// <summary>Where a particle starts in the effect's space, and which way is out from the middle.</summary>
    private (Vector3 Start, Vector3 Outward) Start(uint particle)
    {
        float angle = Random(particle, 2) * MathF.Tau;
        var around = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
        var size = Spawn.Size;
        switch (Spawn.Kind)
        {
            case ParticleSpawnKind.Line:
                // Off the line to either side, fanning a little along it.
                return (new Vector3((Random(particle, 3) - 0.5f) * size.X, 0f, 0f), Vector3.Normalize(around * new Vector3(0.35f, 0f, 1f)));
            case ParticleSpawnKind.Box:
                var inBox = (new Vector3(Random(particle, 3), Random(particle, 4), Random(particle, 6)) - new Vector3(0.5f)) * size;
                var flat = new Vector3(inBox.X, 0f, inBox.Z);
                return (inBox, flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : around);
            case ParticleSpawnKind.Disc:
                return (around * (size.X * MathF.Sqrt(Random(particle, 3))), around);
            default:
                return (Vector3.Zero, around);
        }
    }

    private static Vector3 RandomDirection(uint particle)
    {
        float y = 2f * Random(particle, 6) - 1f;
        float angle = Random(particle, 7) * MathF.Tau;
        float across = MathF.Sqrt(1f - y * y);
        return new Vector3(across * MathF.Cos(angle), y, across * MathF.Sin(angle));
    }

    private static float SmoothStep(float x)
    {
        float t = Math.Clamp(x, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>A number from 0 up to 1 for one particle, with each channel independent of the others.</summary>
    private static float Random(uint particle, uint channel) =>
        (Hash(particle + channel * 0x9E3779B9u) >> 8) * (1f / (1 << 24));

    /// <summary>Mixes the bits of a number thoroughly, so neighbouring inputs give unrelated outputs.</summary>
    private static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }

    internal static float NotNegative(float value, string name) =>
        value >= 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} can't be negative.");

    private static Vector3 NotNegative(Vector3 value, string name) =>
        value.X >= 0f && value.Y >= 0f && value.Z >= 0f && float.IsFinite(value.X + value.Y + value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} can't have a negative channel.");

    private static float Fraction(float value, string name, float most) =>
        value >= 0f && value <= most ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} must be from 0 to {most}.");

    private static Vector3 Finite(Vector3 value, string name) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be a real number on every axis.");
}
