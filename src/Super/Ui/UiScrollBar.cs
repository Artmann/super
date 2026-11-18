using System.Numerics;

namespace Super.Ui;

/// <summary>
/// A scroll view's bar: the track along one edge of its bounds and the thumb inside it,
/// placed from how far the view has scrolled. The bar takes its own lane, so it never
/// covers the content.
/// </summary>
internal readonly record struct UiScrollBar(UiRect Bounds, bool Vertical, UiScrollState Scrolled)
{
    public const float Thickness = 10f;
    public const float MinThumb = 28f;

    public UiRect Track => Vertical
        ? new UiRect(Bounds.Right - Thickness, Bounds.Y, Thickness, Bounds.Height)
        : new UiRect(Bounds.X, Bounds.Bottom - Thickness, Bounds.Width, Thickness);

    public UiRect Thumb
    {
        get
        {
            var track = Track;
            float start = ThumbStart;
            float length = ThumbLength;
            return Vertical
                ? new UiRect(track.X, start, track.Width, length)
                : new UiRect(start, track.Y, length, track.Height);
        }
    }

    private float ThumbLength =>
        Math.Min(Math.Max(Scrolled.ViewportLength * Scrolled.ViewportLength / Scrolled.ContentLength, MinThumb), Scrolled.ViewportLength);

    private float ThumbStart =>
        (Vertical ? Bounds.Y : Bounds.X) + Scrolled.Offset / Scrolled.MaxScroll * (Scrolled.ViewportLength - ThumbLength);

    /// <summary>Whether the bar shows and this point is on it.</summary>
    public bool Contains(float x, float y) => Scrolled.MaxScroll > 0f && Track.Contains(x, y);

    /// <summary>Where along the thumb a press holds it. A press off the thumb holds the middle, and jumps there.</summary>
    public float Grab(Vector2 position)
    {
        float along = Along(position);
        float start = ThumbStart;
        float length = ThumbLength;
        return along >= start && along < start + length ? along - start : length / 2f;
    }

    /// <summary>The scroll offset that puts the thumb under the cursor.</summary>
    public float OffsetFor(Vector2 position, float grab)
    {
        float travel = Scrolled.ViewportLength - ThumbLength;
        float origin = Vertical ? Bounds.Y : Bounds.X;
        return travel > 0f ? (Along(position) - origin - grab) / travel * Scrolled.MaxScroll : 0f;
    }

    private float Along(Vector2 position) => Vertical ? position.Y : position.X;
}
