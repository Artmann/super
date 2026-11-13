using System.Globalization;

namespace Rendition;

/// <summary>
/// What the renderer bakes by itself, set on <see cref="Renderer.AutoBake"/>. Each frame it looks for areas the frame
/// names whose bake is missing or stale and bakes them in the background, one at a time, nearest to the camera first,
/// a slice a frame within <see cref="BudgetMilliseconds"/> of GPU time. Null on <see cref="Renderer.AutoBake"/> bakes
/// nothing. Ignored where <see cref="Renderer.CanTrace"/> is false. See "Baking in the background" in
/// docs/rendering.md.
/// </summary>
/// <param name="BudgetMilliseconds">The GPU time a frame's slice of the bake may take, measured by timestamps.</param>
/// <param name="Spaces">Whether baked spaces are baked too; false bakes only chunks.</param>
/// <param name="Qualities">
/// The qualities each area is baked at, in order, such as Preview then Final. Every area gets its first quality before
/// any gets its next.
/// </param>
/// <param name="Done">
/// Given each finished bake's file, on the render thread, inside <see cref="Renderer.Render"/>. The renderer doesn't
/// save it: the callback writes it where the store reads it, or keeps it some other way.
/// </param>
public sealed record AutoBake(
    float BudgetMilliseconds, bool Spaces, IReadOnlyList<BakeQuality> Qualities, Action<BakeDone> Done)
{
    public float BudgetMilliseconds { get; init; } = float.IsFinite(BudgetMilliseconds) && BudgetMilliseconds > 0f
        ? BudgetMilliseconds
        : throw new ArgumentOutOfRangeException(nameof(BudgetMilliseconds), BudgetMilliseconds, string.Create(
            CultureInfo.InvariantCulture,
            $"A background bake's budget must be more than 0 milliseconds a frame, not {BudgetMilliseconds}."));

    public IReadOnlyList<BakeQuality> Qualities { get; init; } = Qualities is { Count: > 0 }
        ? Qualities
        : throw new ArgumentException("A background bake needs at least one quality to bake at.", nameof(Qualities));

    public Action<BakeDone> Done { get; init; } = Done ?? throw new ArgumentNullException(nameof(Done));
}

/// <summary>
/// A bake the renderer finished in the background: the <c>.lightbake</c> file's bytes, where they belong, and what they
/// were made from.
/// </summary>
/// <param name="File">Where the file goes, such as <c>lighting/courtyard.lightbake</c>: <see cref="BakeFiles.Space"/> or <see cref="BakeFiles.Chunk"/>.</param>
/// <param name="Name">The baked space's or chunk's name.</param>
/// <param name="Inputs">The inputs it was made from, so a frame drawn like the one it was made from finds it fresh.</param>
/// <param name="Quality">The quality it was baked at.</param>
/// <param name="Bytes">The whole file.</param>
public readonly record struct BakeDone(string File, string Name, BakeInputs Inputs, BakeQuality Quality, byte[] Bytes);

/// <summary>How the renderer's background bake is getting on, from <see cref="Renderer.BakeProgress"/>.</summary>
/// <param name="Name">The space or chunk being baked.</param>
/// <param name="Fraction">How much of it is done, from 0 to 1.</param>
/// <param name="Waiting">How many other areas the last frame named that wait to be baked.</param>
/// <param name="LastSliceMilliseconds">The GPU time of the last slice measured, or 0 before one has been.</param>
public readonly record struct BakeProgress(string Name, float Fraction, int Waiting, float LastSliceMilliseconds);
