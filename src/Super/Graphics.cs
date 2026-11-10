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

    /// <summary>
    /// How many texels across each of the sun's shadow maps is: 1024, 2048 or 4096. Each step up
    /// gives sharper shadow edges for four times the memory and more drawing. A change while the
    /// game runs rebuilds the maps before the next frame.
    /// </summary>
    public int ShadowResolution
    {
        get;
        set => field = value is 1024 or 2048 or 4096
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Shadow maps can be 1024, 2048 or 4096 texels across.");
    } = 2048;

    /// <summary>
    /// How large the scene is drawn against the window, from 0.5 to 2 each way; 1 draws it at the window's
    /// size. At 2 it's drawn with four times the pixels and shrunk to fit, which smooths thin things such as
    /// grass and railings, at about four times the cost of lighting and effects. At 0.5 it's drawn with a
    /// quarter of the pixels and stretched, which is much faster and softer. The interface and screenshots
    /// stay at the window's size. <c>--render-scale</c> sets it from the command line. A change while the
    /// game runs rebuilds the scene's targets before the next frame.
    /// </summary>
    public float RenderScale
    {
        get;
        set => field = Renderer.CheckRenderScale(value);
    } = 1f;

    /// <summary>
    /// The sun's contact shadows: the thin dark line where things meet the ground, found on screen, which
    /// the shadow maps are too coarse to draw. Off, only the maps shade there. See <see cref="View.Shadows"/>.
    /// </summary>
    public bool ContactShadows { get; set; } = true;

    /// <summary>
    /// The light bounced off walls and floors by <see cref="View.LightProbes"/>. Off, nothing is drawn
    /// into the probes and every surface takes its soft light from the sky, as with no probes at all.
    /// </summary>
    public bool LightProbes { get; set; } = true;

    /// <summary>Point and spot light shadows. Off, those lights are blocked only by nearby surfaces, as the sun is without its shadows.</summary>
    public bool LightShadows { get; set; } = true;

    /// <summary>Darkening in corners, creases and under things. Off, they're as bright as open ground.</summary>
    public bool AmbientOcclusion { get; set; } = true;

    /// <summary>
    /// Rough surfaces keeping the light that bounces between their tiny facets before it leaves. Off,
    /// rough metals such as worn iron and brushed steel come out darker than they should. It costs
    /// almost nothing.
    /// </summary>
    public bool MultiscatterSpecular { get; set; } = true;

    /// <summary>Shiny surfaces and water mirroring what's on screen, and reflection probes. Off, they and glass reflect only the sky.</summary>
    public bool Reflections { get; set; } = true;

    /// <summary>Smoothing jagged edges over several frames. Off, edges show their pixels, and nothing blurs as the camera moves.</summary>
    public bool AntiAliasing { get; set; } = true;

    /// <summary>The glow round bright things, such as lamps, flames and the sun on water.</summary>
    public bool Bloom { get; set; } = true;

    /// <summary>
    /// The height fog from <see cref="SkySettings.Fog"/>, which sets far things back in the air. Off, the air is
    /// clear whatever the fog's density. Haze is not fog and stays.
    /// </summary>
    public bool Fog { get; set; } = true;

    /// <summary>
    /// Metering the picture for <see cref="PostSettings.AutoExposure"/>, so the brightness follows what the
    /// camera sees, as an eye does. Off, the fixed <see cref="PostSettings.Exposure"/> sets it, as for a view
    /// that never asked. It costs a pass over the frame, so <see cref="Fastest"/> turns it off.
    /// </summary>
    public bool AutoExposure { get; set; } = true;

    /// <summary>
    /// Blurring what's nearer or further than <see cref="PostSettings.FocusDistance"/>, as a camera lens does,
    /// for views whose <see cref="PostSettings.Aperture"/> opens the lens. Off, everything is sharp, as for a
    /// view that never asked. It costs a few passes at half size, so <see cref="Fastest"/> turns it off.
    /// </summary>
    public bool DepthOfField { get; set; } = true;

    /// <summary>
    /// The stains, grime and damp laid onto surfaces with <see cref="View.Decal"/>. Off, they aren't drawn and the
    /// surfaces show as they are. It costs a copy of the normal target and a pass over the boxes' pixels, so
    /// <see cref="Fastest"/> turns it off.
    /// </summary>
    public bool Decals { get; set; } = true;

    /// <summary>
    /// Working out the fog near the camera in a volume that the sun's shadows and the point and spot lights
    /// light up, so sunlight falls through it in shafts and lamps glow with halos. Off, the fog is the plain
    /// height fog everywhere, exactly as without it. It costs two compute passes over a small grid, so
    /// <see cref="Fastest"/> turns it off.
    /// </summary>
    public bool VolumetricFog { get; set; } = true;

    /// <summary>
    /// Every surface's own colour, textures, normal maps and shine. Off, everything is the same light grey,
    /// matte clay, shaped only by its mesh, so the lighting and the models can be judged without the
    /// textures in the way. Glows still glow, cut-outs still cut and glass stays see-through. It's
    /// for looking, not for speed, so <see cref="Fastest"/> leaves it on.
    /// </summary>
    public bool Materials { get; set; } = true;

    /// <summary>
    /// Worn edges and dirt that models' materials lay by masks the cooker baked from their shape: paler,
    /// rubbed edges, and darker hollows and feet. Off, those surfaces show only their own colour and shine,
    /// and their masks aren't read.
    /// </summary>
    public bool WearAndDirt { get; set; } = true;

    /// <summary>
    /// Water and a damp band on surfaces whose material asks for puddles, below and just above the level set with
    /// <see cref="View.Puddles"/>. Off, every surface is dry. It costs a few noise lookups on each wet pixel, so
    /// <see cref="Fastest"/> turns it off.
    /// </summary>
    public bool Puddles { get; set; } = true;

    /// <summary>
    /// Every effect off and vsync off: the fastest the engine draws, and a start for a game that
    /// must run on modest hardware. Turn back on only what it needs.
    /// </summary>
    public static Graphics Fastest() => new()
    {
        Vsync = false,
        SunShadows = false,
        ContactShadows = false,
        LightProbes = false,
        LightShadows = false,
        AmbientOcclusion = false,
        MultiscatterSpecular = false,
        Reflections = false,
        AntiAliasing = false,
        Bloom = false,
        WearAndDirt = false,
        Fog = false,
        AutoExposure = false,
        DepthOfField = false,
        Decals = false,
        VolumetricFog = false,
        Puddles = false,
    };

    /// <summary>The effects the renderer leaves out.</summary>
    internal RenderEffects Off =>
        (SunShadows ? 0 : RenderEffects.SunShadows)
        | (ContactShadows ? 0 : RenderEffects.ContactShadows)
        | (LightProbes ? 0 : RenderEffects.LightProbes)
        | (LightShadows ? 0 : RenderEffects.LightShadows)
        | (AmbientOcclusion ? 0 : RenderEffects.AmbientOcclusion)
        | (MultiscatterSpecular ? 0 : RenderEffects.MultiscatterSpecular)
        | (Reflections ? 0 : RenderEffects.Reflections)
        | (AntiAliasing ? 0 : RenderEffects.AntiAliasing)
        | (Bloom ? 0 : RenderEffects.Bloom)
        | (Fog ? 0 : RenderEffects.Fog)
        | (AutoExposure ? 0 : RenderEffects.AutoExposure)
        | (DepthOfField ? 0 : RenderEffects.DepthOfField)
        | (Decals ? 0 : RenderEffects.Decals)
        | (VolumetricFog ? 0 : RenderEffects.VolumetricFog)
        | (Puddles ? 0 : RenderEffects.Puddles)
        | (Materials ? 0 : RenderEffects.Materials)
        | (WearAndDirt ? 0 : RenderEffects.WearAndDirt);
}
