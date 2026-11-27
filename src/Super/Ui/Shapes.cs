namespace Super.Ui;

/// <summary>
/// A shape that fills the space it's offered, as SwiftUI's do: give it a size with <c>.Frame(...)</c>, or
/// let a stack stretch it. It's filled with the text colour around it unless told otherwise. A stroke is
/// drawn inside the edge, so it never spills out of the frame.
/// </summary>
public abstract record ShapeView : PrimitiveView
{
    // What a shape asks for when it's asked how big it would like to be, as in SwiftUI.
    private const float Ideal = 10f;

    private protected ShapeView()
    {
    }

    internal Paint? FillPaint { get; init; }

    internal Color? StrokeColor { get; init; }

    internal float StrokeWidth { get; init; }

    internal override IReadOnlyList<UiView?> Children => [];

    internal override bool Blocks => true;

    /// <summary>Fills the shape with a colour or a gradient.</summary>
    public ShapeView Fill(Paint paint) => this with { FillPaint = paint };

    /// <summary>
    /// Draws a line of this width inside the shape's edge. A shape with a stroke and no
    /// <see cref="Fill"/> is only its outline.
    /// </summary>
    public ShapeView Stroke(Color color, float width = 1f) => this with { StrokeColor = color, StrokeWidth = width };

    internal override UiSize Measure(UiNode node, Proposal proposal) => new(proposal.Width ?? Ideal, proposal.Height ?? Ideal);

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        // Filled with the text colour unless it's only an outline.
        Paint? fill = FillPaint ?? (StrokeColor is null ? node.Environment.Text.Color : null);
        Draw(draw, node.Frame, fill, StrokeColor is null ? 0f : StrokeWidth, StrokeColor ?? default);
    }

    private protected abstract void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke);

    /// <summary>The biggest square that fits in the middle of the frame.</summary>
    private protected static UiRect Square(UiRect frame)
    {
        float side = Math.Min(frame.Width, frame.Height);
        return frame.Align(new UiSize(side, side), Alignment.Center);
    }
}

/// <summary>A rectangle filling its frame.</summary>
public sealed record Rectangle : ShapeView
{
    private protected override void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke) =>
        draw.AddBox(frame, fill, default, new UiStroke(strokeWidth, stroke));
}

/// <summary>A rectangle with rounded corners, all the same or each its own.</summary>
public sealed record RoundedRectangle(CornerRadii Radius) : ShapeView
{
    private protected override void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke) =>
        draw.AddBox(frame, fill, Radius, new UiStroke(strokeWidth, stroke));
}

/// <summary>A rectangle whose short ends are half circles, like a pill or a switch's track.</summary>
public sealed record Capsule : ShapeView
{
    private protected override void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke) =>
        draw.AddBox(frame, fill, Math.Min(frame.Width, frame.Height) / 2f, new UiStroke(strokeWidth, stroke));
}

/// <summary>The biggest circle that fits in the middle of its frame.</summary>
public sealed record Circle : ShapeView
{
    private protected override void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke) =>
        draw.AddEllipse(Square(frame), fill, new UiStroke(strokeWidth, stroke));
}

/// <summary>An ellipse filling its frame.</summary>
public sealed record Ellipse : ShapeView
{
    private protected override void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke) =>
        draw.AddEllipse(frame, fill, new UiStroke(strokeWidth, stroke));
}

/// <summary>
/// Part of a ring with round ends, the biggest that fits in the middle of its frame: a progress ring, a
/// cooldown, a gauge. It runs clockwise from <paramref name="Start"/> through <paramref name="Sweep"/>,
/// both as fractions of a turn from the top, so <c>new Arc(0f, 0.25f)</c> is the top right quarter.
/// </summary>
public sealed record Arc(float Start, float Sweep, float Thickness = 4f) : ShapeView
{
    /// <summary>
    /// Cuts the ends straight, along lines from the middle, instead of rounding them, so arcs side by side make the
    /// slices of a wheel. Keep each under half a turn.
    /// </summary>
    public bool FlatEnds { get; init; }

    private protected override void Draw(UiDrawList draw, UiRect frame, Paint? fill, float strokeWidth, Color stroke)
    {
        // A ring has no inside to fill, so its colour is the fill's, or the stroke's for an outline.
        var color = fill?.From ?? stroke;
        draw.AddArc(Square(frame), Start * MathF.Tau, Math.Clamp(Sweep, 0f, 1f) * MathF.Tau, new UiStroke(Thickness, color), FlatEnds);
    }
}
