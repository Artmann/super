using Rendition;

namespace Super.Tests;

/// <summary>Picks a view's draws as a frame does: measures them, then selects.</summary>
internal static class Picking
{
    public static int Select(
        in DetailView view, ReadOnlySpan<DrawItem> draws, List<PartDetail> details, Span<DrawItem> selected, WindReach wind = default,
        Span<int> sources = default, FadeRules? fades = null)
    {
        var measured = new DrawBalls();
        measured.Measure(draws, details, wind, fades);
        return DrawSelection.Select(view, draws, measured, selected, sources);
    }
}
