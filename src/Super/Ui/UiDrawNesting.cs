using System.Numerics;

namespace Super.Ui;

/// <summary>A rectangle drawing is cut to, with the corners rounded that have a radius.</summary>
internal readonly record struct UiClip(UiRect Rect, CornerRadii Radius);

/// <summary>
/// The clips, opacities and scales that wrap what's drawn now. Each push lasts until its pop, so
/// a view's children are cut to it, faded with it and scaled with it.
/// </summary>
internal sealed class UiDrawNesting
{
    private readonly List<UiClip?> _clips = [];
    private readonly List<float> _opacities = [];
    private readonly List<(float Zoom, Vector2 Shift)> _scales = [];

    /// <summary>The clip in force. Nested clips intersect and keep the innermost radius that's set.</summary>
    public UiClip? Clip { get; private set; }

    /// <summary>Every opacity pushed, multiplied together.</summary>
    public float Opacity { get; private set; } = 1f;

    /// <summary>
    /// Everything until the matching <see cref="PopClip"/> stays inside this rectangle.
    /// A quad has room for one clip, so nested ones intersect and keep the innermost
    /// radius that's set.
    /// </summary>
    public void PushClip(UiRect rectangle, CornerRadii radius)
    {
        // Kept as it's drawn, so a clip inside a scaled view shrinks with it and one outside doesn't.
        rectangle = Place(rectangle);
        radius = new CornerRadii(radius.TopLeading * Zoom, radius.TopTrailing * Zoom, radius.BottomTrailing * Zoom, radius.BottomLeading * Zoom);
        _clips.Add(Clip);
        Clip = Clip is { } outer
            ? new UiClip(outer.Rect.Intersect(rectangle), radius.Any ? radius : outer.Radius)
            : new UiClip(rectangle, radius);
    }

    public void PopClip()
    {
        Clip = _clips[^1];
        _clips.RemoveAt(_clips.Count - 1);
    }

    /// <summary>Multiplies everything until the matching <see cref="PopOpacity"/>.</summary>
    public void PushOpacity(float opacity)
    {
        _opacities.Add(Opacity);
        Opacity *= opacity;
    }

    public void PopOpacity()
    {
        Opacity = _opacities[^1];
        _opacities.RemoveAt(_opacities.Count - 1);
    }

    /// <summary>Every scale pushed, multiplied together.</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>How far the scales pushed move what's drawn, after <see cref="Zoom"/>.</summary>
    public Vector2 Shift { get; private set; }

    /// <summary>
    /// Scales everything until the matching <see cref="PopScale"/> by <paramref name="factor"/> about
    /// <paramref name="centre"/>, as it's drawn. The layout and the mouse don't see it.
    /// </summary>
    public void PushScale(Vector2 centre, float factor)
    {
        _scales.Add((Zoom, Shift));
        Shift += Zoom * centre * (1f - factor);
        Zoom *= factor;
    }

    public void PopScale()
    {
        (Zoom, Shift) = _scales[^1];
        _scales.RemoveAt(_scales.Count - 1);
    }

    /// <summary>Where a rectangle laid out here is drawn, after the scales in force.</summary>
    public UiRect Place(UiRect frame) =>
        new(frame.X * Zoom + Shift.X, frame.Y * Zoom + Shift.Y, frame.Width * Zoom, frame.Height * Zoom);

    /// <summary>Whether the clip cuts all of this rectangle, as it's drawn, away.</summary>
    public bool Hides(UiRect frame) => Clip is { } clip && !clip.Rect.Overlaps(frame);

    public Vector4 Fade(Color color) => new(color.R, color.G, color.B, color.A * Opacity);
}
