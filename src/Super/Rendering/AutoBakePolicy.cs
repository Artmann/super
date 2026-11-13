using Rendition;

namespace Super;

/// <summary>What a running game bakes by itself, worked out by <see cref="AutoBakePolicy.For"/>.</summary>
/// <param name="Budget">GPU milliseconds a frame for the bake.</param>
/// <param name="Spaces">Whether baked spaces are baked too, not only chunks.</param>
/// <param name="Qualities">The qualities each area is baked at, in order.</param>
/// <param name="SpaceFolder">The content root a space's bake is written into, under <c>lighting/</c>; null when published.</param>
internal sealed record AutoBakePlan(float Budget, bool Spaces, BakeQuality[] Qualities, string? SpaceFolder);

/// <summary>
/// Decides what a game bakes while it runs. From source it bakes spaces and chunks, Preview then Final, at 4 ms a
/// frame, and writes a space's bake into the first content root. Published it bakes chunks only, at Preview and 2 ms
/// a frame, since a player's machine has no sources to write into. See "Baking without a build step" in
/// docs/rendering.md.
/// </summary>
internal static class AutoBakePolicy
{
    public const float SourceBudget = 4f;
    public const float PublishedBudget = 2f;

    /// <summary>Null when nothing should bake: the switch is off or the GPU can't trace.</summary>
    /// <param name="dev">The dev settings when running from source, or null when published.</param>
    /// <param name="canTrace">Whether the GPU can trace rays.</param>
    /// <param name="graphics">The game's settings, for <see cref="Graphics.BakeOnArrival"/> and <see cref="Graphics.BakeBudget"/>.</param>
    public static AutoBakePlan? For(DevSettings? dev, bool canTrace, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (!graphics.BakeOnArrival || !canTrace)
        {
            return null;
        }

        if (dev is { Roots.Length: > 0 })
        {
            return new AutoBakePlan(
                graphics.BakeBudget ?? SourceBudget, true, [BakeQuality.Preview, BakeQuality.Final], dev.Roots[0]);
        }

        return new AutoBakePlan(graphics.BakeBudget ?? PublishedBudget, false, [BakeQuality.Preview], null);
    }
}
