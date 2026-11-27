using System.Numerics;

namespace Super.Ui;

/// <summary>
/// How far in from each edge of an image its corners and edges reach, in the image's pixels, for drawing it as a
/// 9-slice: the corners keep their size, the edges stretch along, and the middle stretches both ways. A single
/// number cuts the same on every side.
/// </summary>
public readonly record struct ImageSlices(float Top, float Trailing, float Bottom, float Leading)
{
    public ImageSlices(float all)
        : this(all, all, all, all)
    {
    }

    public static implicit operator ImageSlices(float all) => new(all);
}

/// <summary>
/// A picture a theme draws a panel from instead of a box: a card, a popup or a button. The image is drawn as a
/// 9-slice cut by <see cref="Slices"/>, its corners <see cref="Scale"/> interface units to each of its pixels.
/// When <see cref="Tinted"/>, it takes the colour the plain box would have, so a pale picture follows the theme and
/// a button still darkens when held. Otherwise it keeps its own colours.
/// </summary>
public sealed record PanelArt(GameImage Image, ImageSlices Slices)
{
    public float Scale { get; init; } = 1f;

    public bool Tinted { get; init; }

    /// <summary>The picture behind a panel whose plain box would be <paramref name="fill"/>, in <paramref name="own"/> when it keeps its own colours.</summary>
    internal UiView Behind(Color fill, Color? own = null) => new Image(Image, Tint: Tinted ? fill : own ?? Color.White) { Slices = Slices, SliceScale = Scale };
}

/// <summary>
/// The pictures a <see cref="Theme"/> draws its panels from. Each one left out is drawn as the theme's box, with its
/// colour, edge, corners and shadow, so <see cref="None"/>, the default, draws only boxes.
/// </summary>
public sealed record ThemePanels
{
    /// <summary>Boxes everywhere.</summary>
    public static ThemePanels None { get; } = new();

    /// <summary>Behind a <see cref="Super.Ui.Card"/>.</summary>
    public PanelArt? Card { get; init; }

    /// <summary>Behind what floats: dialogs, sheets, popovers, menus and toasts.</summary>
    public PanelArt? Popup { get; init; }

    /// <summary>Behind a filled button: the default, secondary and destructive styles. Outline, ghost, menu and link buttons stay as they are.</summary>
    public PanelArt? Button { get; init; }
}

/// <summary>Draws a theme's panels: on its picture when it has one, and as its box when it doesn't.</summary>
internal static class ThemePanel
{
    /// <summary>The content on <paramref name="art"/>, or in the <paramref name="box"/> made round it when there's none.</summary>
    public static UiView On(UiView content, PanelArt? art, Color fill, Func<UiView, UiView> box) =>
        art is { } picture ? content.Background(picture.Behind(fill)) : box(content);
}

/// <summary>Cuts a frame and its image into the nine pieces of a 9-slice.</summary>
internal static class NineSlice
{
    /// <summary>
    /// Draws the pieces, each with its part of the image. Where the frame is too small for the corners, they shrink
    /// together so they still meet, and a piece with no room is left out.
    /// </summary>
    public static void Draw(UiDrawList draw, UiRect frame, Image image, ImageSlices slices, Color tint)
    {
        var source = image.Source;
        var (left, right) = Fit(slices.Leading * image.SliceScale, slices.Trailing * image.SliceScale, frame.Width);
        var (top, bottom) = Fit(slices.Top * image.SliceScale, slices.Bottom * image.SliceScale, frame.Height);
        Span<float> xs = [frame.X, frame.X + left, frame.X + frame.Width - right, frame.X + frame.Width];
        Span<float> ys = [frame.Y, frame.Y + top, frame.Y + frame.Height - bottom, frame.Y + frame.Height];
        Span<float> us = [0f, slices.Leading / source.Width, 1f - slices.Trailing / source.Width, 1f];
        Span<float> vs = [0f, slices.Top / source.Height, 1f - slices.Bottom / source.Height, 1f];
        for (int piece = 0; piece < 9; piece++)
        {
            int column = piece % 3;
            int row = piece / 3;
            var area = new UiRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
            if (area.Width > 0f && area.Height > 0f)
            {
                draw.AddImage(area, new Vector2(us[column], vs[row]), new Vector2(us[column + 1], vs[row + 1]), tint, source);
            }
        }
    }

    /// <summary>Two edges across a length, shrunk by the same share when together they're longer than it.</summary>
    private static (float Start, float End) Fit(float start, float end, float length)
    {
        float both = start + end;
        if (both <= length || both <= 0f)
        {
            return (start, end);
        }

        float share = length / both;
        return (start * share, end * share);
    }
}
