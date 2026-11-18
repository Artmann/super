using System.Globalization;

namespace Super;

/// <summary>Baked lighting: whether bakes are read, and whether and how fast missing ones are baked in play.</summary>
public sealed class GraphicsBaking
{
    private const float MinBakeBudget = 0.5f;
    private const float MaxBakeBudget = 33f;

    /// <summary>
    /// Light read from the bakes made with <c>--bake</c> for the areas set with <see cref="ViewIndirect.BakedLighting"/> and
    /// <see cref="ViewIndirect.BakedChunk"/>. Off, every bake is ignored and each area is lit live, as if it had never been
    /// baked, which shows what the bake changes. A bake costs less than the live light it stands in for, so
    /// <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool BakedLighting { get; set; } = true;

    /// <summary>
    /// Bakes missing lighting as it appears, where the GPU can trace. From source: spaces and chunks, Preview then
    /// Final. Published: chunks only, Preview. Only play in a window bakes this way; screenshots, recordings, profiles
    /// and <c>--bake</c> never do. See "Baking without a build step" in docs/rendering.md.
    /// </summary>
    public bool BakeOnArrival { get; set; } = true;

    /// <summary>
    /// GPU milliseconds a frame for background bakes, from 0.5 to 33. Null: 4 from source, 2 in a published game. More
    /// finishes a bake sooner and costs frame rate while it runs.
    /// </summary>
    public float? BakeBudget
    {
        get;
        set => field = value is null or (>= MinBakeBudget and <= MaxBakeBudget)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), string.Create(CultureInfo.InvariantCulture,
                $"BakeBudget must be from {MinBakeBudget} to {MaxBakeBudget} milliseconds, not {value}. Use null for the default."));
    }

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsBaking other)
    {
        BakedLighting = other.BakedLighting;
        BakeOnArrival = other.BakeOnArrival;
        BakeBudget = other.BakeBudget;
    }
}
