using Rendition;

namespace Super;

/// <summary>
/// The look of the picture a frame asks for, kept for <see cref="ViewEffects"/>: the finish, the screen effects driven
/// from play, the paint and the outlines. Checked by <see cref="ViewEffects"/> before they are set here.
/// </summary>
internal sealed class ViewLook
{
    public PostSettings Finish { get; set; }

    public ScreenEffects Screen { get; set; }

    public PaintSettings Paint { get; set; }

    /// <summary>The ink lines round surfaces that ask for them.</summary>
    public OutlineSettings Outlines { get; set; } = new();

    /// <summary>The <see cref="ArtLook"/> <see cref="ArtStyle"/> shows this frame, laid over the rest as the frame is made.</summary>
    public ArtLook Style { get; set; }

    /// <summary>The finish, with the screen effects laid over its own.</summary>
    public PostSettings Post => Finish with { Screen = ScreenEffects.Over(Screen, Finish.Screen) };

    /// <summary>
    /// Puts the look back as a frame starts: a light vignette and sharpening, the default outlines, and no screen effects
    /// or paint.
    /// </summary>
    public void Clear()
    {
        Finish = new PostSettings { Vignette = 0.3f, Sharpen = 0.4f };
        Screen = default;
        Paint = default;
        Outlines = new OutlineSettings();
        Style = default;
    }
}
