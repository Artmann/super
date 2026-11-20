namespace Rendition;

/// <summary>
/// Turns the picture, or only its far part, into an oil painting: flat parts go to brush strokes that follow the edges,
/// and the edges stay hard. The default paints nothing. See "Paint" in docs/rendering.md.
/// </summary>
public struct PaintSettings
{
    /// <summary>The largest <see cref="BrushSize"/>. A larger brush is drawn this size.</summary>
    public const float MaxBrushSize = 16f;

    /// <summary>The brush size used when <see cref="BrushSize"/> is 0.</summary>
    public const float DefaultBrushSize = 10f;

    /// <summary>The sharpness used when <see cref="Sharpness"/> is null.</summary>
    public const float DefaultSharpness = 0.5f;

    /// <summary>How much of the painted picture shows, from 0 for none to 1 for all of it.</summary>
    public float Amount;

    /// <summary>
    /// The brush's radius in pixels of a picture 1080 pixels tall, from 0 to <see cref="MaxBrushSize"/>, so it covers
    /// the same share of the screen at any size. 0 is <see cref="DefaultBrushSize"/>. Strokes along an edge are up to
    /// twice as long.
    /// </summary>
    public float BrushSize;

    /// <summary>
    /// How hard the edges between strokes are, from 0, soft and blended, to 1, crisp. Null is
    /// <see cref="DefaultSharpness"/>.
    /// </summary>
    public float? Sharpness;

    /// <summary>
    /// With <see cref="Far"/>, paints only what is far away: nothing nearer than this many metres, fading in to the
    /// full <see cref="Amount"/> at <see cref="Far"/>.
    /// </summary>
    public float Near;

    /// <summary>
    /// The distance in metres at which the paint reaches the full <see cref="Amount"/>, past <see cref="Near"/>. 0
    /// paints the whole picture alike. The sky always counts as far.
    /// </summary>
    public float Far;
}
