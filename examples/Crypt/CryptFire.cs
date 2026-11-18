using System.Numerics;
using Super;

namespace Crypt;

/// <summary>The crypt's flames, as particles that glow: candle flames, and torch fires with embers.</summary>
public static class CryptFire
{
    /// <summary>
    /// A candle's flame: small glowing puffs packed close so they read as one teardrop, warm
    /// yellow at the wick going to orange at the tip, born all through a second so one
    /// second's worth runs on into the next.
    /// </summary>
    public static readonly ParticleEmitter Flame = new ParticleEmitter(count: 24, lifetime: 0.3f)
        .Over(1f)
        .FromSphere(0.004f)
        .Velocity(new Vector3(0f, 0.14f, 0f))
        .Scatter(0.006f)
        .Gravity(new Vector3(0f, 0.3f, 0f))
        .Size(0.022f, 0.004f)
        .Color(new Vector3(1f, 0.82f, 0.45f), new Vector3(1f, 0.38f, 0.08f))
        .Glow(5f)
        .Fade(0.05f, 0.6f);

    /// <summary>A torch's fire, the same flame bigger and wilder.</summary>
    public static readonly ParticleEmitter TorchFire = new ParticleEmitter(count: 70, lifetime: 0.5f)
        .Over(1f)
        .FromSphere(0.04f)
        .Velocity(new Vector3(0f, 0.45f, 0f))
        .Scatter(0.05f)
        .Gravity(new Vector3(0f, 0.9f, 0f))
        .Size(0.13f, 0.02f)
        .Color(new Vector3(1f, 0.78f, 0.4f), new Vector3(0.9f, 0.25f, 0.05f))
        .Glow(4f)
        .Fade(0.08f, 0.55f)
        .Spin(1f);

    /// <summary>Embers drifting up from a torch, few and slow.</summary>
    public static readonly ParticleEmitter Embers = new ParticleEmitter(count: 6, lifetime: 1.6f)
        .Over(1f)
        .FromSphere(0.05f)
        .Velocity(new Vector3(0f, 0.5f, 0f))
        .Scatter(0.25f)
        .Size(0.012f, 0.004f)
        .Color(new Vector3(1f, 0.55f, 0.15f))
        .Glow(8f)
        .Fade(0.05f, 0.4f);

    internal static readonly ParticleEffect CandleFlame = new ParticleEffect().Add(Flame);
    internal static readonly ParticleEffect Torch = new ParticleEffect().Add(TorchFire).Add(Embers);

    /// <summary>
    /// A fire that never stops, at <paramref name="seconds"/>: each second's flames under their own key, this one's and
    /// the last's still dying out.
    /// </summary>
    internal static void Draw(View view, int key, ParticleEffect effect, Vector3 at, float seconds)
    {
        int second = (int)seconds;
        float into = seconds - second;
        view.Effects.Particles(key + (second % 500), effect, at, into);
        view.Effects.Particles(key + ((second + 499) % 500), effect, at, into + 1f);
    }
}
