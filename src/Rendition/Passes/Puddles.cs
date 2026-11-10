using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Puddles worked out the way surface.glsl's applyPuddles does, so they can be tested here. A surface whose
/// material has <see cref="MaterialFlags.Puddles"/> is water where it lies below the level, and damp for a band
/// above it. Its height map counts: the bottom of a groove lies its depth below the face.
/// </summary>
internal static class Puddles
{
    /// <summary>How far below the level, in metres, a surface goes from dry of water to all water, so the shore is soft.</summary>
    public const float Shore = 0.003f;

    /// <summary>How high, in metres, each of the water's two layers of ripples is, as surface.glsl's RIPPLE_AMPLITUDE.</summary>
    public const float RippleAmplitude = 0.001f;

    /// <summary>
    /// The height in metres of the surface at this pixel: the face's own height <paramref name="worldY"/>, less
    /// <paramref name="heightDepth"/> times how far below the top of the height map <paramref name="height"/> is,
    /// where 1 is the top and 0 the bottom.
    /// </summary>
    public static float SurfaceHeight(float worldY, float height, float heightDepth) => worldY + (height - 1f) * heightDepth;

    /// <summary>
    /// How much of a surface at <paramref name="surfaceHeight"/> is water and how damp it is, each 0 to 1. Below the
    /// level it is water, softened over <see cref="Shore"/>, and wholly damp. Above, it dries in a straight line
    /// to nothing at <paramref name="band"/> metres up.
    /// </summary>
    public static (float Water, float Damp) Wetness(float surfaceHeight, float level, float band)
    {
        float below = level - surfaceHeight;
        float water = Math.Clamp(below / Shore, 0f, 1f);
        float damp = band > 0f ? Math.Clamp(1f + below / band, 0f, 1f) : below > 0f ? 1f : 0f;
        return (water, damp);
    }

    /// <summary>How much light the water reflects straight on, as common.glsl's WATER_F0: 2%, for water's refractive index of 1.33.</summary>
    public const float WaterF0 = 0.02f;

    /// <summary>
    /// How much light the water reflects when seen at <paramref name="nDotV"/>, the cosine of the angle between the
    /// view and straight up: <see cref="WaterF0"/> looking straight down, rising to all of it at grazing (Schlick).
    /// </summary>
    public static float WaterFresnel(float nDotV) => WaterF0 + (1f - WaterF0) * MathF.Pow(1f - Math.Clamp(nDotV, 0f, 1f), 5f);

    /// <summary>
    /// The share of the light from inside the water, from the silt and the ground under it, that gets out to the
    /// eye at <paramref name="nDotV"/>: what the reflection leaves, so at grazing the water is nearly all reflection.
    /// The lighting multiplies the water's colour by it.
    /// </summary>
    public static float WaterBody(float nDotV) => 1f - WaterFresnel(nDotV);

    /// <summary>What the frame block gets: x the level, y the band, z 1 to say there is water.</summary>
    public static Vector4 Pack(float level, float band) => new(level, band, 1f, 0f);

    /// <summary>The frame block's puddles: all zero, which leaves every surface dry, with none set or the switch off.</summary>
    public static Vector4 Pack(PuddleSettings? settings, RenderEffects off) =>
        settings is { } puddles && (off & RenderEffects.Puddles) == 0 ? Pack(puddles.Level, puddles.Band) : Vector4.Zero;
}
