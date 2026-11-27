using System.Numerics;
using Rendition;

namespace Super.Ui;

/// <summary>The shapes a <see cref="UiDrawList"/> can draw, each one quad the overlay shader shapes.</summary>
internal static class UiShapeDrawing
{
    /// <summary>Draws a filled rectangle, rounded and bordered if asked to be.</summary>
    public static void AddBox(this UiDrawList list, UiRect frame, Paint? fill, CornerRadii radius, UiStroke border = default)
    {
        var quad = Filled(list, frame, fill, border);
        quad.Radii = radius.ToVector();
        quad.Kind = OverlayKind.Box;
        list.Add(frame, quad);
    }

    /// <summary>Draws the ellipse that fills this rectangle, bordered if asked to be.</summary>
    public static void AddEllipse(this UiDrawList list, UiRect frame, Paint? fill, UiStroke border = default)
    {
        var quad = Filled(list, frame, fill, border);
        quad.Kind = OverlayKind.Ellipse;
        list.Add(frame, quad);
    }

    /// <summary>
    /// Draws part of a ring, touching the shorter sides of the rectangle, from <paramref name="start"/> through
    /// <paramref name="sweep"/> in radians, clockwise from the top. Its ends are round, or cut straight towards the
    /// middle when <paramref name="flatEnds"/> is set.
    /// </summary>
    public static void AddArc(this UiDrawList list, UiRect frame, float start, float sweep, UiStroke line, bool flatEnds = false)
    {
        list.Add(frame, new OverlayQuad
        {
            Color = list.Nesting.Fade(line.Color),
            Kind = OverlayKind.Arc,
            Params = new Vector4(start, sweep, flatEnds ? 1f : 0f, 0f),
            Thickness = line.Width,
        });
    }

    /// <summary>Draws a line with round ends from one point to another.</summary>
    public static void AddSegment(this UiDrawList list, Vector2 from, Vector2 to, float thickness, Color color)
    {
        // The quad only has to cover the line and its round ends.
        float reach = thickness / 2f + 1f;
        var min = Vector2.Min(from, to) - new Vector2(reach);
        var max = Vector2.Max(from, to) + new Vector2(reach);
        list.Add(new UiRect(min.X, min.Y, max.X - min.X, max.Y - min.Y), new OverlayQuad
        {
            Color = list.Nesting.Fade(color),
            Kind = OverlayKind.Segment,
            Params = new Vector4(from - min, (to - min).X, (to - min).Y),
            Thickness = thickness,
        });
    }

    /// <summary>Draws the soft shadow a box with these corners casts, behind where the box will be.</summary>
    public static void AddShadow(this UiDrawList list, UiRect box, CornerRadii radius, Shadow shadow)
    {
        var cast = new UiRect(box.X + shadow.X - shadow.Spread, box.Y + shadow.Y - shadow.Spread, box.Width + shadow.Spread * 2f, box.Height + shadow.Spread * 2f);

        // A Gaussian is all but gone three deviations out, and the deviation is half the blur.
        float reach = shadow.Blur * 1.5f + 1f;
        var frame = new UiRect(cast.X - reach, cast.Y - reach, cast.Width + reach * 2f, cast.Height + reach * 2f);
        list.Add(frame, new OverlayQuad
        {
            Color = list.Nesting.Fade(shadow.Color),
            Kind = OverlayKind.Shadow,
            Radii = radius.Grow(shadow.Spread).ToVector(),
            Params = new Vector4(cast.X, cast.Y, cast.Width, cast.Height),
            GradientPoints = new Vector4(box.X, box.Y, box.Width, box.Height),
            Thickness = shadow.Blur,
        });
    }

    // A quad painted and bordered, which the caller gives its shape.
    private static OverlayQuad Filled(UiDrawList list, UiRect frame, Paint? fill, UiStroke border)
    {
        var uv = UiGlyphSheet.SolidUv;
        var paint = fill ?? default(Color);
        return new OverlayQuad
        {
            UvMin = uv,
            UvMax = uv,
            Color = list.Nesting.Fade(paint.From),
            Color2 = list.Nesting.Fade(paint.To),
            Gradient = paint.Kind,
            GradientPoints = paint.IsGradient ? paint.PointsIn(frame) : default,
            BorderColor = list.Nesting.Fade(border.Color),
            BorderWidth = border.Width,
        };
    }
}

/// <summary>A line's width and colour, such as a border's. The default draws none.</summary>
internal readonly record struct UiStroke(float Width, Color Color);
