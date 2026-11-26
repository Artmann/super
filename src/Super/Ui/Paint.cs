using System.Numerics;
using Rendition;

namespace Super.Ui;

/// <summary>
/// What fills a background or a shape: one colour, or a gradient between two. A colour is a
/// paint, so <c>.Background(Color.Hex("#18181B"))</c> works as it always did. Gradients mix in
/// sRGB, as CSS does, so they look the same as in a browser.
/// </summary>
public readonly record struct Paint
{
    /// <summary>The colour, or the colour a gradient starts at.</summary>
    public Color From { get; private init; }

    /// <summary>The colour a gradient ends at. A solid paint ends where it starts.</summary>
    public Color To { get; private init; }

    /// <summary>Whether this is a gradient at all.</summary>
    public bool IsGradient => Kind != OverlayGradient.None;

    internal OverlayGradient Kind { get; private init; }

    // Where a linear gradient runs from and to, or a radial one's centre, as fractions of the box.
    private Vector2 Start { get; init; }

    private Vector2 End { get; init; }

    // A CSS angle in degrees, which wins over Start and End when it's set.
    private float? Angle { get; init; }

    // How far a radial gradient reaches, in interface units. Null reaches the farthest corner.
    private float? Radius { get; init; }

    public static implicit operator Paint(Color color) => new() { From = color, To = color };

    /// <summary>
    /// A gradient across the box at an angle in degrees, as CSS's <c>linear-gradient</c> has it: 180 runs
    /// from the top down, 90 from left to right, and 135 from the top left corner to the bottom right one.
    /// </summary>
    public static Paint Linear(Color from, Color to, float angle = 180f) =>
        new() { From = from, To = to, Kind = OverlayGradient.Linear, Angle = angle };

    /// <summary>A gradient from one point of the box to another, such as <see cref="Alignment.Top"/> to <see cref="Alignment.BottomTrailing"/>.</summary>
    public static Paint Linear(Color from, Color to, Alignment start, Alignment end) =>
        new() { From = from, To = to, Kind = OverlayGradient.Linear, Start = Point(start), End = Point(end) };

    /// <summary>
    /// A gradient out from a point of the box, the middle unless said otherwise, reaching <paramref name="radius"/>
    /// in interface units, or the farthest corner when that's left out.
    /// </summary>
    public static Paint Radial(Color center, Color edge, Alignment at = Alignment.Center, float? radius = null) =>
        new() { From = center, To = edge, Kind = OverlayGradient.Radial, Start = Point(at), Radius = radius };

    /// <summary>Where the gradient's colours land in this box, in interface units from its top left.</summary>
    internal Vector4 PointsIn(UiRect box)
    {
        var size = new Vector2(box.Width, box.Height);
        if (Kind == OverlayGradient.Radial)
        {
            var center = Start * size;
            var farthest = Vector2.Max(center, size - center);
            return new Vector4(center, Radius ?? farthest.Length(), 0f);
        }

        if (Angle is { } degrees)
        {
            // CSS's gradient line: through the middle, just long enough that the corners get the end colours.
            float radians = degrees * MathF.PI / 180f;
            var direction = new Vector2(MathF.Sin(radians), -MathF.Cos(radians));
            float length = MathF.Abs(size.X * direction.X) + MathF.Abs(size.Y * direction.Y);
            var middle = size / 2f;
            return Line(middle - direction * length / 2f, middle + direction * length / 2f);
        }

        return Line(Start * size, End * size);
    }

    private static Vector4 Line(Vector2 from, Vector2 to) => new(from.X, from.Y, to.X, to.Y);

    private static Vector2 Point(Alignment alignment)
    {
        var (x, y) = alignment.Fractions();
        return new Vector2(x, y);
    }
}

/// <summary>
/// How round each corner of a box is, in interface units. A single number rounds all four, so
/// <c>.CornerRadius(8f)</c> still works; name the corners to round only some, as a tab's top does.
/// </summary>
public readonly record struct CornerRadii(float TopLeading, float TopTrailing, float BottomTrailing, float BottomLeading)
{
    public CornerRadii(float all)
        : this(all, all, all, all)
    {
    }

    /// <summary>Whether any corner is rounded.</summary>
    public bool Any => TopLeading > 0f || TopTrailing > 0f || BottomTrailing > 0f || BottomLeading > 0f;

    public static implicit operator CornerRadii(float all) => new(all);

    internal Vector4 ToVector() => new(TopLeading, TopTrailing, BottomTrailing, BottomLeading);

    /// <summary>The same corners grown or shrunk by this much, never below square.</summary>
    internal CornerRadii Grow(float amount) => new(
        Math.Max(TopLeading + amount, 0f),
        Math.Max(TopTrailing + amount, 0f),
        Math.Max(BottomTrailing + amount, 0f),
        Math.Max(BottomLeading + amount, 0f));
}
