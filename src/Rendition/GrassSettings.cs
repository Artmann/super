using System.Globalization;
using System.Numerics;

namespace Rendition;

/// <summary>
/// How a field of grass looks: its colours, the size and shape of its blades, how thick they stand and how they move in
/// the wind. Blades vary round these values, and grow in clumps that lean and stand alike. See "Grass" in
/// docs/rendering.md.
/// </summary>
public readonly record struct GrassSettings
{
    /// <summary>Makes the default meadow: knee-high green grass, yellowing at the tips.</summary>
    public GrassSettings()
    {
    }

    /// <summary>The blades' linear colour at the root, from 0 to 1.</summary>
    public Vector3 BaseColor { get; init; } = new(0.06f, 0.13f, 0.025f);

    /// <summary>Their linear colour at the tip, from 0 to 1. The colour runs from <see cref="BaseColor"/> up to it.</summary>
    public Vector3 TipColor { get; init; } = new(0.32f, 0.42f, 0.11f);

    /// <summary>How tall a blade stands, in metres, before it bends. At most 2.</summary>
    public float Height { get; init; } = 0.45f;

    /// <summary>How much blades differ in height, from 0, all alike, to 1, from nothing to twice as tall.</summary>
    public float HeightVariation { get; init; } = 0.4f;

    /// <summary>How wide a blade is at its root, in metres. It tapers to a point. At most 0.5.</summary>
    public float Width { get; init; } = 0.04f;

    /// <summary>
    /// How thick the grass stands, from 0 for none to 1 for the most, about 64 blades a square metre nearby. A
    /// density map thins it further.
    /// </summary>
    public float Density { get; init; } = 1f;

    /// <summary>How far a blade's tip droops over, from 0, straight up, to 1, bent over level with its root.</summary>
    public float Bend { get; init; } = 0.35f;

    /// <summary>How much sunlight shines through a blade from behind, from 0 to 1.</summary>
    public float Translucency { get; init; } = 0.5f;

    /// <summary>How far the wind sways the blades, from 0 to stand still. 1 sways them as far as a material's full wind.</summary>
    public float Sway { get; init; } = 0.7f;

    /// <summary>How wide a clump of blades is, in metres, from 0.25 to 8. Blades in a clump lean and stand alike.</summary>
    public float ClumpSize { get; init; } = 1.2f;

    /// <summary>What is wrong with <paramref name="settings"/>, or null when they can be drawn.</summary>
    public static string? Problem(in GrassSettings settings)
    {
        if (!InUnit(settings.BaseColor) || !InUnit(settings.TipColor))
        {
            return "Grass colours must be from 0 to 1.";
        }

        if (!(settings.Height is > 0f and <= 2f))
        {
            return Invariant($"Grass height must be above 0 and at most 2 metres, not {settings.Height}.");
        }

        if (!(settings.Width is > 0f and <= 0.5f))
        {
            return Invariant($"Grass width must be above 0 and at most 0.5 metres, not {settings.Width}.");
        }

        if (!(settings.ClumpSize is >= 0.25f and <= 8f))
        {
            return Invariant($"Grass clump size must be from 0.25 to 8 metres, not {settings.ClumpSize}.");
        }

        if (!(settings.Sway is >= 0f and <= 4f))
        {
            return Invariant($"Grass sway must be from 0 to 4, not {settings.Sway}.");
        }

        return Unit(settings.HeightVariation, "height variation") ?? Unit(settings.Density, "density")
            ?? Unit(settings.Bend, "bend") ?? Unit(settings.Translucency, "translucency");
    }

    private static string? Unit(float value, string name) =>
        value is >= 0f and <= 1f ? null : Invariant($"Grass {name} must be from 0 to 1, not {value}.");

    private static bool InUnit(Vector3 color) =>
        color.X is >= 0f and <= 1f && color.Y is >= 0f and <= 1f && color.Z is >= 0f and <= 1f;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Grass grown over one ground draw, such as a terrain chunk, for <see cref="FrameExtras.Grass"/>. The blades stand
/// on the ground's top surface, seen from above, so they follow its height.
/// </summary>
/// <param name="Ground">The draw the grass grows on. It is drawn as usual too, with its own material.</param>
/// <param name="Look">How the grass looks.</param>
/// <param name="Density">Where on the ground the grass grows, and how thick, or null to grow it all over.</param>
public readonly record struct GrassDraw(DrawItem Ground, GrassSettings Look, GrassDensity? Density = null);

/// <summary>
/// A map of how thick grass stands, laid flat over its ground from above, for <see cref="GrassDraw.Density"/>.
/// </summary>
/// <param name="Map">The texture. Its <paramref name="Channel"/> says how thick the grass stands, from 0 for bare to 1 for the full <see cref="GrassSettings.Density"/>.</param>
/// <param name="Min">
/// The x and z, in the ground's own space, where the map's first texel's corner lies. Its u runs along x and its v
/// along z.
/// </param>
/// <param name="Max">The x and z, in the ground's own space, where its last texel's far corner lies. Above <paramref name="Min"/> both ways.</param>
/// <param name="Channel">Which channel to read: 0 red, 1 green, 2 blue or 3 alpha.</param>
public readonly record struct GrassDensity(TextureHandle Map, Vector2 Min, Vector2 Max, int Channel = 0)
{
    /// <summary>What is wrong with <paramref name="density"/>, or null when it can be drawn. Doesn't check the texture.</summary>
    public static string? Problem(in GrassDensity density)
    {
        if (density.Channel is < 0 or > 3)
        {
            return string.Create(CultureInfo.InvariantCulture, $"A grass density map's channel must be from 0 to 3, not {density.Channel}.");
        }

        bool finite = float.IsFinite(density.Min.X) && float.IsFinite(density.Min.Y)
            && float.IsFinite(density.Max.X) && float.IsFinite(density.Max.Y);
        if (!finite || density.Max.X <= density.Min.X || density.Max.Y <= density.Min.Y)
        {
            return "A grass density map's Max must be above its Min along both x and z.";
        }

        return null;
    }
}
