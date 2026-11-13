using System.Globalization;
using Rendition;
using Super.Ui;

namespace Super;

/// <summary>
/// The small line at the bottom left that says what bakes in the background, such as "Baking cottage, 42% (3 more)".
/// See "Baking without a build step" in docs/rendering.md.
/// </summary>
internal static class BakeOverlay
{
    private static readonly Color Fill = Color.Hex("#000000", 0.55f);
    private static readonly Color Ink = Color.Hex("#E8E8E8");

    /// <summary>"Baking cottage, 42%" and, with others waiting, " (3 more)". Null when progress is null.</summary>
    public static string? Text(BakeProgress? progress)
    {
        if (progress is not { } bake)
        {
            return null;
        }

        if (bake.Name.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Waiting to bake {bake.Waiting} {(bake.Waiting == 1 ? "area" : "areas")}");
        }

        int percent = (int)MathF.Floor(Math.Clamp(bake.Fraction, 0f, 1f) * 100f);
        string text = string.Create(CultureInfo.InvariantCulture, $"Baking {bake.Name}, {percent}%");
        return bake.Waiting > 0
            ? text + string.Create(CultureInfo.InvariantCulture, $" ({bake.Waiting} more)")
            : text;
    }

    /// <summary>
    /// The game's interface with the line over it at the bottom left, which the pointer passes through. The game's view
    /// stays the first child of the same stack whether a bake runs or not, so it keeps its state as bakes come and go.
    /// Null when there is neither.
    /// </summary>
    public static UiView? Over(UiView? game, BakeProgress? progress)
    {
        string? text = Text(progress);
        if (game is null && text is null)
        {
            return null;
        }

        return new ZStack
        {
            game,
            text is null ? null : Line(text),
        };
    }

    private static UiView Line(string text) =>
        new Text(text)
            .FontSize(13f)
            .Foreground(Ink)
            .Padding(10f, 4f)
            .Background(Fill)
            .CornerRadius(4f)
            .Padding(12f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.BottomLeading)
            .IgnoresPointer();
}
