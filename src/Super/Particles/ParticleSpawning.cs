using System.Numerics;

namespace Super;

/// <summary>
/// The nodes for when an emitter's particles are born and where, in the emitter's own space.
/// An emitter has one of each, so calling one again replaces it.
/// </summary>
public static class ParticleSpawning
{
    /// <summary>All the particles are born at once. This is how an emitter starts.</summary>
    public static ParticleEmitter Burst(this ParticleEmitter emitter) => emitter.WithBirth(BurstNode.Instance);

    /// <summary>Spreads births evenly over this many seconds.</summary>
    public static ParticleEmitter Over(this ParticleEmitter emitter, float seconds) =>
        emitter.WithBirth(new OverNode(ParticleChecks.Positive(seconds, nameof(seconds))));

    /// <summary>Shares the particles between this many bursts, this many seconds apart, like puffs from a chimney.</summary>
    public static ParticleEmitter Pulses(this ParticleEmitter emitter, int times, float every)
    {
        if (times < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(times), times, "There must be at least one pulse.");
        }

        return emitter.WithBirth(new PulsesNode(times, ParticleChecks.Positive(every, nameof(every))));
    }

    /// <summary>Every particle starts at the middle. This is how an emitter starts.</summary>
    public static ParticleEmitter FromPoint(this ParticleEmitter emitter) => emitter.WithShape(PointNode.Instance);

    /// <summary>
    /// Along a line on the x axis through the middle, this many metres long. Drawn with
    /// <see cref="ViewEffects.Particles(int, ParticleEffect, Vector3, Vector3, float)"/>, a line one
    /// metre long runs from one end to the other. Outward speed throws particles mostly
    /// sideways, off the line.
    /// </summary>
    public static ParticleEmitter FromLine(this ParticleEmitter emitter, float length = 1f) =>
        emitter.WithShape(new LineNode(ParticleChecks.NotNegative(length, nameof(length))));

    /// <summary>Anywhere in a box this many metres across on each axis, centred on the middle.</summary>
    public static ParticleEmitter FromBox(this ParticleEmitter emitter, Vector3 size)
    {
        if (!(size.X >= 0f && size.Y >= 0f && size.Z >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A spawn box can't be smaller than nothing.");
        }

        return emitter.WithShape(new BoxNode(size));
    }

    /// <summary>Anywhere on a flat round patch of this radius, such as round the foot of a tree.</summary>
    public static ParticleEmitter FromDisc(this ParticleEmitter emitter, float radius) =>
        emitter.WithShape(new DiscNode(ParticleChecks.NotNegative(radius, nameof(radius))));

    /// <summary>Anywhere inside a ball of this radius. Outward speed throws particles straight away from the middle.</summary>
    public static ParticleEmitter FromSphere(this ParticleEmitter emitter, float radius) =>
        emitter.WithShape(new SphereNode(ParticleChecks.NotNegative(radius, nameof(radius))));

    private static ParticleEmitter WithBirth(this ParticleEmitter emitter, BirthNode birth) =>
        emitter.With(emitter.Settings with { Birth = birth });

    private static ParticleEmitter WithShape(this ParticleEmitter emitter, ShapeNode shape) =>
        emitter.With(emitter.Settings with { Shape = shape });
}
