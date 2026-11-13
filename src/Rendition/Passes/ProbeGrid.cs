using System.Numerics;

namespace Rendition;

/// <summary>
/// A box filled with a grid of light probes, <see cref="Spacing"/> metres apart, starting at
/// <see cref="Min"/>. Each probe records the light that reaches it from every direction, which
/// gives surfaces inside the box the light bounced off the walls and floor around them.
/// </summary>
public readonly record struct ProbeVolumeSettings(Vector3 Min, Vector3 Max, float Spacing);

/// <summary>The layout of a <see cref="ProbeVolumeSettings"/>'s probes and how much each one counts at a point.</summary>
public static class ProbeGrid
{
    public const int MaxProbes = 4096;

    /// <summary>
    /// How many probes run along each axis: enough that they're at most a spacing apart and reach
    /// at least to the box's far side, and never fewer than two, so every point has a cell around it.
    /// </summary>
    public static (int x, int y, int z) Counts(in ProbeVolumeSettings settings)
    {
        var extent = settings.Max - settings.Min;
        return (Along(extent.X, settings.Spacing), Along(extent.Y, settings.Spacing), Along(extent.Z, settings.Spacing));
    }

    /// <summary>How many probes the box holds, as a long so a huge box can't overflow.</summary>
    public static long Total(in ProbeVolumeSettings settings)
    {
        var (x, y, z) = Counts(settings);
        return (long)x * y * z;
    }

    /// <summary>Where a probe sits. Indices run along x first, then y, then z.</summary>
    public static Vector3 Position(in ProbeVolumeSettings settings, int index)
    {
        var (x, y, _) = Counts(settings);
        int i = index % x;
        int j = index / x % y;
        int k = index / (x * y);
        return settings.Min + new Vector3(i, j, k) * settings.Spacing;
    }

    /// <summary>
    /// How much one of the eight probes round a point counts towards its light. The same sums as
    /// probes.glsl, so these tests pin the shader. Four parts multiply. Trilinear: nearer probes
    /// count more, falling to 0 a spacing away along any axis. Facing: a probe behind the surface,
    /// straight down its normal, counts for nothing. Chebyshev: the probe knows the mean and the mean
    /// of the squares of the distances it sees towards the point, so a point further away than
    /// that is probably behind a wall from it, and counts for little. Captured: a probe that has not
    /// been captured yet (its irradiance alpha is still 0) counts for nothing.
    /// </summary>
    public static float Weight(Vector3 point, Vector3 normal, Vector3 probe, float meanDepth, float meanSquares, float distance, float spacing = 1f, bool captured = true)
    {
        var offset = Vector3.Abs(point - probe) / spacing;
        float trilinear = MathF.Max(1f - offset.X, 0f) * MathF.Max(1f - offset.Y, 0f) * MathF.Max(1f - offset.Z, 0f);

        var towards = probe - point;
        float length = towards.Length();
        float facing = 1f;
        if (length > 1e-5f)
        {
            float wrap = (Vector3.Dot(towards / length, normal) + 1f) * 0.5f;
            facing = wrap * wrap;
        }

        float visible = 1f;
        if (distance > meanDepth)
        {
            float variance = MathF.Abs(meanSquares - meanDepth * meanDepth);
            float beyond = distance - meanDepth;
            float chebyshev = variance / (variance + beyond * beyond);
            visible = chebyshev * chebyshev * chebyshev;
        }

        return captured ? trilinear * facing * visible : 0f;
    }

    // Clamped so a huge box at a tiny spacing counts as too many rather than overflowing.
    private static int Along(float extent, float spacing) => (int)Math.Clamp(MathF.Ceiling(extent / spacing) + 1f, 2f, 1 << 20);
}
