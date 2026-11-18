using System.Numerics;

namespace Super;

/// <summary>
/// The nodes for how an emitter's particles look over their lives. These multiply, starting
/// from one metre wide, white and solid.
/// </summary>
public static class ParticleLooks
{
    /// <summary>
    /// Width in metres from birth to death, eased over the life. Out grows fast at first and
    /// then slows, as a billow does. Spread makes each a little bigger or smaller, as a fraction.
    /// </summary>
    public static ParticleEmitter Size(this ParticleEmitter emitter, float start, float end, Easing easing = Easing.Out, float spread = 0.3f) =>
        emitter.Then(new SizeNode(
            ParticleChecks.NotNegative(start, nameof(start)),
            ParticleChecks.NotNegative(end, nameof(end)),
            easing,
            ParticleChecks.Fraction(spread, nameof(spread), 0.9f)));

    /// <summary>One color all its life, as linear red, green and blue from 0 to 1 like a model's color. The sun, the sky and point and spot lights light it.</summary>
    public static ParticleEmitter Color(this ParticleEmitter emitter, Vector3 color) => emitter.Color(color, color);

    /// <summary>A color blending evenly from birth to death.</summary>
    public static ParticleEmitter Color(this ParticleEmitter emitter, Vector3 start, Vector3 end) => emitter.Gradient((0f, start), (1f, end));

    /// <summary>Colors at points through the life, from 0 at birth to 1 at death, blending between them. The points must go up.</summary>
    public static ParticleEmitter Gradient(this ParticleEmitter emitter, params ReadOnlySpan<(float At, Vector3 Color)> stops)
    {
        if (stops.IsEmpty)
        {
            throw new ArgumentException("A gradient needs at least one color.", nameof(stops));
        }

        var at = new float[stops.Length];
        var colors = new Vector3[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            if (!(stops[i].At >= 0f && stops[i].At <= 1f) || (i > 0 && stops[i].At < stops[i - 1].At))
            {
                throw new ArgumentOutOfRangeException(nameof(stops), stops[i].At, "A gradient's points must go up from 0 to 1.");
            }

            at[i] = stops[i].At;
            colors[i] = ParticleChecks.NotNegative(stops[i].Color, nameof(stops));
        }

        return emitter.Then(new GradientNode(at, colors));
    }

    /// <summary>How solid the middle of a particle is at most, from 0 to 1. Overlapping particles add up.</summary>
    public static ParticleEmitter Opacity(this ParticleEmitter emitter, float opacity) =>
        emitter.Then(new OpacityNode(ParticleChecks.Fraction(opacity, nameof(opacity), 1f)));

    /// <summary>
    /// Makes each particle shine with its own colour times this, on top of the light that falls
    /// on it, as a flame or a spark does. Past 1 it blooms. Without it particles only show the
    /// light that reaches them.
    /// </summary>
    public static ParticleEmitter Glow(this ParticleEmitter emitter, float strength) =>
        emitter.Then(new GlowNode(ParticleChecks.NotNegative(strength, nameof(strength))));

    /// <summary>Fades in over this part of the life and out over this last part, both from 0 to 1.</summary>
    public static ParticleEmitter Fade(this ParticleEmitter emitter, float fadeIn, float fadeOut) =>
        emitter.Then(new FadeNode(ParticleChecks.Fraction(fadeIn, nameof(fadeIn), 1f), ParticleChecks.Fraction(fadeOut, nameof(fadeOut), 1f)));

    /// <summary>Dips the opacity by up to this fraction, each particle at its own beat, about this many times a second.</summary>
    public static ParticleEmitter Flicker(this ParticleEmitter emitter, float amount, float perSecond) =>
        emitter.Then(new FlickerNode(ParticleChecks.Fraction(amount, nameof(amount), 1f), ParticleChecks.NotNegative(perSecond, nameof(perSecond))));

    /// <summary>Turns each particle at its own speed, up to this many radians a second either way.</summary>
    public static ParticleEmitter Spin(this ParticleEmitter emitter, float radiansPerSecond) =>
        emitter.Then(new SpinNode(ParticleChecks.NotNegative(radiansPerSecond, nameof(radiansPerSecond))));
}
