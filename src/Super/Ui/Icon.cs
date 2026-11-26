using System.Numerics;

namespace Super.Ui;

/// <summary>The icons Super draws itself, in lines on a 24 by 24 grid.</summary>
public enum IconName
{
    Check,
    ChevronLeft,
    ChevronRight,
    ChevronUp,
    ChevronDown,
    Close,
    Plus,
    Minus,
    Menu,
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,
    Search,
    Info,
    Dot,
}

/// <summary>
/// A small line drawing, sized and coloured like the text around it, so <c>.FontSize(...)</c> and
/// <c>.Foreground(...)</c> work on it as they do on text, and it lines up beside a label.
/// They're drawn as shapes, not from an image, so they're sharp at any size.
/// </summary>
public sealed record Icon(IconName Name) : PrimitiveView
{
    // On the 24 grid, as Lucide's icons are: lines 2 wide, kept off the edge.
    private const float Grid = 24f;
    private const float Line = 2f;

    // A little bigger than the text, as an icon's ink sits inside its box and letters fill theirs.
    private const float SizeOfText = 1.15f;

    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float side = SideFor(node);
        return new UiSize(side, side);
    }

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        var text = node.Environment.Text;
        float side = SideFor(node);
        var box = node.Frame.Align(new UiSize(side, side), Alignment.Center);
        var pen = new IconPen(draw, box, side / Grid, Math.Max(Line * side / Grid, 1.25f), text.Color);
        IconDrawings.Draw(Name, pen);
    }

    private static float SideFor(UiNode node) => MathF.Round(node.Environment.Text.Size * SizeOfText);
}

/// <summary>How each icon is drawn on the 24 grid, in the order of <see cref="IconName"/>.</summary>
internal static class IconDrawings
{
    private static readonly Action<IconPen>[] Drawings =
    [
        pen => pen.Lines((20f, 6f), (9f, 17f), (4f, 12f)),
        pen => pen.Lines((15f, 18f), (9f, 12f), (15f, 6f)),
        pen => pen.Lines((9f, 18f), (15f, 12f), (9f, 6f)),
        pen => pen.Lines((18f, 15f), (12f, 9f), (6f, 15f)),
        pen => pen.Lines((6f, 9f), (12f, 15f), (18f, 9f)),
        pen =>
        {
            pen.Lines((18f, 6f), (6f, 18f));
            pen.Lines((6f, 6f), (18f, 18f));
        },
        pen =>
        {
            pen.Lines((5f, 12f), (19f, 12f));
            pen.Lines((12f, 5f), (12f, 19f));
        },
        pen => pen.Lines((5f, 12f), (19f, 12f)),
        pen =>
        {
            pen.Lines((4f, 6f), (20f, 6f));
            pen.Lines((4f, 12f), (20f, 12f));
            pen.Lines((4f, 18f), (20f, 18f));
        },
        pen =>
        {
            pen.Lines((19f, 12f), (5f, 12f));
            pen.Lines((12f, 19f), (5f, 12f), (12f, 5f));
        },
        pen =>
        {
            pen.Lines((5f, 12f), (19f, 12f));
            pen.Lines((12f, 5f), (19f, 12f), (12f, 19f));
        },
        pen =>
        {
            pen.Lines((12f, 19f), (12f, 5f));
            pen.Lines((5f, 12f), (12f, 5f), (19f, 12f));
        },
        pen =>
        {
            pen.Lines((12f, 5f), (12f, 19f));
            pen.Lines((19f, 12f), (12f, 19f), (5f, 12f));
        },
        pen =>
        {
            pen.Ring(11f, 11f, 7f);
            pen.Lines((21f, 21f), (16.2f, 16.2f));
        },
        pen =>
        {
            pen.Ring(12f, 12f, 9f);
            pen.Lines((12f, 16f), (12f, 11.5f));
            pen.Spot(12f, 8f, 1.25f);
        },
        pen => pen.Spot(12f, 12f, 4f),
    ];

    /// <summary>Draws the icon, or a dot for a name that isn't one.</summary>
    public static void Draw(IconName name, IconPen pen)
    {
        int index = (int)name;
        Drawings[index >= 0 && index < Drawings.Length ? index : (int)IconName.Dot](pen);
    }
}

/// <summary>Draws on the 24 grid, scaled into the icon's box.</summary>
internal readonly struct IconPen(UiDrawList draw, UiRect box, float scale, float width, Color color)
{
    public void Lines(params ReadOnlySpan<(float X, float Y)> points)
    {
        for (int i = 1; i < points.Length; i++)
        {
            draw.AddSegment(At(points[i - 1]), At(points[i]), width, color);
        }
    }

    public void Ring(float x, float y, float radius)
    {
        float outer = radius * scale + width / 2f;
        var center = At((x, y));
        draw.AddArc(new UiRect(center.X - outer, center.Y - outer, outer * 2f, outer * 2f), 0f, MathF.Tau, new UiStroke(width, color));
    }

    public void Spot(float x, float y, float radius)
    {
        var center = At((x, y));
        float reach = radius * scale;
        draw.AddEllipse(new UiRect(center.X - reach, center.Y - reach, reach * 2f, reach * 2f), color);
    }

    private Vector2 At((float X, float Y) point) => new(box.X + point.X * scale, box.Y + point.Y * scale);
}
