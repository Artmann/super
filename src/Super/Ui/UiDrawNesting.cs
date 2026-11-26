using System.Numerics;

namespace Super.Ui;

/// <summary>A rectangle drawing is cut to, with the corners rounded that have a radius.</summary>
internal readonly record struct UiClip(UiRect Rect, CornerRadii Radius);

/// <summary>
/// The clips and opacities that wrap what's drawn now. Each push lasts until its pop, so
/// a view's children are cut to it and faded with it.
/// </summary>
internal sealed class UiDrawNesting
{
    private readonly List<UiClip?> _clips = [];
    private readonly List<float> _opacities = [];

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

    /// <summary>Whether the clip cuts all of this rectangle away.</summary>
    public bool Hides(UiRect frame) => Clip is { } clip && !clip.Rect.Overlaps(frame);

    public Vector4 Fade(Color color) => new(color.R, color.G, color.B, color.A * Opacity);
}
