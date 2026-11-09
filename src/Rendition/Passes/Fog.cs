using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Packs the height fog for the frame block, and works out how much light gets through it the
/// way fog.glsl does, so the shader's sums can be tested here.
/// </summary>
internal static class Fog
{
    /// <summary>Below this, a ray is level enough to take as level, which is off by under 0.005 per cent.</summary>
    private const float LevelRay = 1e-4f;

    /// <summary>x: density at the fog's height; y: that height; z: falloff per metre; w: sun scatter.</summary>
    public static Vector4 Pack(in FogSettings settings) =>
        new(settings.Density, settings.Height, settings.Falloff, settings.SunScatter);

    /// <summary>
    /// What the frame block gets: nothing with the <see cref="RenderEffects.Fog"/> switch off,
    /// nothing while a probe is captured, since fog belongs to the view that looks through it, and
    /// nothing under water, which takes over the sky and the fade with distance.
    /// </summary>
    public static Vector4 Pack(in FogSettings settings, RenderEffects off, bool capturingProbe, bool underwater) =>
        capturingProbe || underwater || off.HasFlag(RenderEffects.Fog) ? Vector4.Zero : Pack(settings);

    /// <summary>
    /// The share of light leaving <paramref name="to"/> that reaches <paramref name="from"/>, for fog
    /// whose density is <paramref name="density"/> at <paramref name="height"/> and falls by e every
    /// 1 / <paramref name="falloff"/> metres up. The density along the ray is an exponential in
    /// its height, so its integral has a closed form.
    /// </summary>
    public static float Transmittance(float density, float height, float falloff, Vector3 from, Vector3 to)
    {
        if (density <= 0f)
        {
            return 1f;
        }

        float length = Vector3.Distance(from, to);
        float climb = falloff * (to.Y - from.Y);
        float along = MathF.Abs(climb) > LevelRay ? (1f - MathF.Exp(-climb)) / climb : 1f;
        float depth = density * MathF.Exp(-falloff * (from.Y - height)) * length * along;
        return MathF.Exp(-depth);
    }
}
