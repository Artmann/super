using Rendition;

namespace Super;

/// <summary>
/// How the game is drawn: which effects are worth their GPU time, and how fast frames come.
/// Set it in <see cref="GameOptions.Graphics"/>, or ask for it in a constructor and change
/// it as the game runs, and the next frame follows. Everything starts on, with vsync, as
/// the engine looks best. Each effect turned off skips its work on the GPU, not only its look.
/// </summary>
public sealed class Graphics
{
    /// <summary>
    /// Waits for the display before showing each frame, so the picture never tears and the frame rate
    /// stops at the display's, often 60. Off, frames come as fast as the computer can draw them.
    /// <c>--no-vsync</c> turns it off. Screenshots, recordings and profiles always keep it on.
    /// </summary>
    public bool Vsync { get; set; } = true;

    /// <summary>
    /// The most frames drawn in a second, or 0 for no limit. A limit saves power and keeps a
    /// laptop cool, and with vsync off it's how to stop at a steady rate. Only play in a window
    /// is limited; screenshots, recordings and profiles run at their own pace.
    /// </summary>
    public int MaxFramesPerSecond
    {
        get;
        set => field = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The frame limit can't be negative. Use 0 for no limit.");
    }

    /// <summary>
    /// The sun's shadows. Off, nothing is drawn into the shadow maps, and sunlight is blocked only by nearby
    /// surfaces, as ambient occlusion sees them. Often the most expensive effect.
    /// </summary>
    public bool SunShadows { get; set; } = true;

    /// <summary>Point and spot light shadows. Off, those lights are blocked only by nearby surfaces, as the sun is without its shadows.</summary>
    public bool LightShadows { get; set; } = true;

    /// <summary>Darkening in corners, creases and under things. Off, they're as bright as open ground.</summary>
    public bool AmbientOcclusion { get; set; } = true;

    /// <summary>Shiny surfaces and water mirroring what's on screen. Off, they reflect only the sky and reflection probes.</summary>
    public bool Reflections { get; set; } = true;

    /// <summary>Smoothing jagged edges over several frames. Off, edges show their pixels, and nothing blurs as the camera moves.</summary>
    public bool AntiAliasing { get; set; } = true;

    /// <summary>The glow round bright things, such as lamps, flames and the sun on water.</summary>
    public bool Bloom { get; set; } = true;

    /// <summary>
    /// Every effect off and vsync off: the fastest the engine draws, and a start for a game that
    /// must run on modest hardware. Turn back on only what it needs.
    /// </summary>
    public static Graphics Fastest() => new()
    {
        Vsync = false,
        SunShadows = false,
        LightShadows = false,
        AmbientOcclusion = false,
        Reflections = false,
        AntiAliasing = false,
        Bloom = false,
    };

    /// <summary>The effects the renderer leaves out.</summary>
    internal RenderEffects Off =>
        (SunShadows ? 0 : RenderEffects.SunShadows)
        | (LightShadows ? 0 : RenderEffects.LightShadows)
        | (AmbientOcclusion ? 0 : RenderEffects.AmbientOcclusion)
        | (Reflections ? 0 : RenderEffects.Reflections)
        | (AntiAliasing ? 0 : RenderEffects.AntiAliasing)
        | (Bloom ? 0 : RenderEffects.Bloom);
}
