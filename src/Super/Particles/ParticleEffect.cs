using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Emitters on a timeline, drawn together under one key. A tree falling might puff dust
/// at its stump straight away and throw up a line of it where it lands a second later:
/// <code>
/// static readonly ParticleEffect TreeFall = new ParticleEffect()
///     .Add(StumpPuff, offset: new Vector3(-0.5f, 0f, 0f))
///     .Add(LandingDust, delay: 1.1f);
/// </code>
/// Each emitter gets its own pattern from the key, so two emitters that are alike still
/// look different. Adding gives back a new effect, so shared ones never change. An
/// emitter can be drawn on its own too; it turns into an effect of just itself.
/// </summary>
public sealed class ParticleEffect
{
    private readonly Layer[] _layers;

    /// <summary>An effect with nothing in it yet.</summary>
    public ParticleEffect()
        : this([])
    {
    }

    private ParticleEffect(Layer[] layers)
    {
        _layers = layers;
        foreach (var layer in layers)
        {
            Duration = MathF.Max(Duration, layer.Delay + layer.Emitter.Duration);
        }
    }

    /// <summary>Seconds from the start until the last particle has gone. Past this, drawing adds nothing.</summary>
    public float Duration { get; }

    /// <summary>
    /// Adds an emitter that starts this many seconds after the effect does, with its middle
    /// moved by the offset in the effect's own space. A line drawn from and to stretches
    /// the offset's x with the line, so -0.5 is the start and 0.5 the end.
    /// </summary>
    public ParticleEffect Add(ParticleEmitter emitter, float delay = 0f, Vector3 offset = default)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        if (!(delay >= 0f) || !float.IsFinite(delay))
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "An emitter's delay can't be negative.");
        }

        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "An emitter's offset must be a real number on every axis.");
        }

        return new ParticleEffect([.. _layers, new Layer(emitter, delay, offset)]);
    }

    /// <summary>Lists the emitters with their delays and offsets.</summary>
    public override string ToString() =>
        $"ParticleEffect [{string.Join(", ", _layers.Select(l => $"{l.Emitter} at {l.Delay} s, {l.Offset}"))}]";

    /// <summary>Adds the particles alive at this age, the shapes' x stretched by the given factor.</summary>
    internal void Emit(int key, float age, in Matrix4x4 transform, List<Particle> into, float stretch = 1f)
    {
        if (!(age >= 0f) || age >= Duration)
        {
            return;
        }

        uint keySeed = ParticleHash.Mix(unchecked((uint)key));
        for (int i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            uint seed = ParticleHash.Mix(unchecked(keySeed + (uint)i * 0x27D4EB2Fu));
            layer.Emitter.Emit(seed, age - layer.Delay, transform, layer.Offset, stretch, into);
        }
    }

    private readonly record struct Layer(ParticleEmitter Emitter, float Delay, Vector3 Offset);
}
