using Rendition;

namespace Super;

/// <summary>The finish on the whole picture: glow, exposure, focus, corners, grain, sharpening, colour grade and screen effects.</summary>
public sealed class GraphicsPicture
{
    /// <summary>The glow round bright things, such as lamps, flames and the sun on water.</summary>
    public bool Bloom { get; set; } = true;

    /// <summary>
    /// Metering the picture for <see cref="PostSettings.AutoExposure"/>, so the brightness follows what the
    /// camera sees, as an eye does. Off, the fixed <see cref="PostSettings.Exposure"/> sets it, as for a view
    /// that never asked. It costs a pass over the frame, so <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool AutoExposure { get; set; } = true;

    /// <summary>
    /// Blurring what's nearer or further than <see cref="LensSettings.FocusDistance"/>, as a camera lens does,
    /// for views whose <see cref="LensSettings.Aperture"/> opens the lens. Off, everything is sharp, as for a
    /// view that never asked. It costs a few passes at half size, so <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool DepthOfField { get; set; } = true;

    /// <summary>
    /// The darkening towards the picture's corners set with <see cref="PostSettings.Vignette"/>. Off, the corners are as
    /// bright as the middle. <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool Vignette { get; set; } = true;

    /// <summary>
    /// The film grain set with <see cref="PostSettings.Grain"/>. Off, the picture is clean. <see cref="Graphics.Fastest"/>
    /// turns it off.
    /// </summary>
    public bool Grain { get; set; } = true;

    /// <summary>
    /// The sharpening set with <see cref="PostSettings.Sharpen"/>, which wins back the detail anti-aliasing softens. Off,
    /// the finish pass skips it. <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool Sharpening { get; set; } = true;

    /// <summary>
    /// The colour grade set in <see cref="PostSettings"/>: contrast, saturation, lift, gain, white balance, the channel
    /// mixer, the hue shift, the shadow, midtone and highlight tints and split toning. Off, they're all neutral. The
    /// exposure, the bloom and the tonemapper with its look stay, as the picture needs them. It costs almost nothing, so
    /// <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool ColorGrading { get; set; } = true;

    /// <summary>
    /// The artist's colour table set with <see cref="ColorGrade.Lut"/>. Off, the picture is as if the view named none.
    /// It is baked into the grade's own table, so it costs nothing a frame and <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool ColorLut { get; set; } = true;

    /// <summary>
    /// The colour fringes set with <see cref="Rendition.ScreenEffects.ChromaticAberration"/>. Off, the picture is read once a pixel
    /// instead of three times, so <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool ChromaticAberration { get; set; } = true;

    /// <summary>
    /// The blur at speed set with <see cref="Rendition.ScreenEffects.SpeedBlur"/>. Off, the picture is read once a pixel instead of
    /// sixteen times while it shows, so <see cref="Graphics.Fastest"/> turns it off.
    /// </summary>
    public bool SpeedBlur { get; set; } = true;

    /// <summary>
    /// The flashes set with <see cref="Rendition.ScreenEffects.Flash"/>. Turn it off for a player who is sensitive to flashing
    /// light; the fade stays. It costs nothing, so <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool Flashes { get; set; } = true;

    /// <summary>
    /// All of <see cref="Rendition.ScreenEffects"/> but the fade: the fringes, the speed blur, the flash, the edge tints, the loss
    /// of colour and the paper. Off, the picture is as if the game set none of them; the fade stays, as a game uses it to
    /// say something. <see cref="Graphics.Fastest"/> leaves it on and turns off the two that cost time.
    /// </summary>
    public bool ScreenEffects { get; set; } = true;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsPicture other)
    {
        Bloom = other.Bloom;
        AutoExposure = other.AutoExposure;
        DepthOfField = other.DepthOfField;
        Vignette = other.Vignette;
        Grain = other.Grain;
        Sharpening = other.Sharpening;
        ColorGrading = other.ColorGrading;
        ColorLut = other.ColorLut;
        ChromaticAberration = other.ChromaticAberration;
        SpeedBlur = other.SpeedBlur;
        Flashes = other.Flashes;
        ScreenEffects = other.ScreenEffects;
    }
}
