namespace Rendition;

/// <summary>
/// The parts of the picture that cost GPU time and can be left out. Each one left out skips
/// its work, not only its look, so a frame without any of them is as cheap as the renderer gets.
/// The low 32 bits reach the shaders, which read them as an <c>int</c>; the effects above them the renderer leaves out
/// on its own.
/// </summary>
[Flags]
public enum RenderEffects : long
{
    None = 0,

    /// <summary>The sun's shadow maps. Without them sunlight is blocked only by nearby surfaces, as ambient occlusion sees them.</summary>
    SunShadows = 1 << 0,

    /// <summary>Point and spot light shadows. Without them those lights, too, are blocked only by nearby surfaces.</summary>
    LightShadows = 1 << 1,

    /// <summary>Darkening in corners and creases, worked out from the screen.</summary>
    AmbientOcclusion = 1 << 2,

    /// <summary>Shiny surfaces mirroring what's on screen. Without them they reflect only the sky and reflection probes.</summary>
    Reflections = 1 << 3,

    /// <summary>Smoothing jagged edges over several frames. Without it edges show their pixels.</summary>
    AntiAliasing = 1 << 4,

    /// <summary>The glow round bright things.</summary>
    Bloom = 1 << 5,

    /// <summary>
    /// Every material's colour, textures, normal maps and shine. Without them every surface is the same
    /// light grey, matte clay, shaped only by its mesh, which shows the lighting and the models on their own.
    /// Cut-outs still cut, glows still glow and see-through things stay see-through.
    /// </summary>
    Materials = 1 << 6,

    /// <summary>
    /// The sun's contact shadows, the last centimetres where things meet the ground, found on screen.
    /// Without them the sun's shadow maps alone shade those, as <see cref="ShadowSettings.ContactLength"/> 0 does.
    /// </summary>
    ContactShadows = 1 << 7,

    /// <summary>
    /// The light bounced off walls and floors, from <see cref="FrameView.LightProbes"/>. Without it
    /// nothing is drawn into the probes and every surface takes its soft light from the sky.
    /// </summary>
    LightProbes = 1 << 8,

    /// <summary>
    /// The light that bounces between a rough surface's microfacets before it leaves, given back to its
    /// shine. Without it rough metals come out darker than they should.
    /// It costs a few instructions a pixel.
    /// </summary>
    MultiscatterSpecular = 1 << 9,

    /// <summary>
    /// Worn edges and dirt laid by materials' baked masks (<see cref="MaterialWeathering.Wear"/> and <see cref="MaterialWeathering.Dirt"/>).
    /// Without them those surfaces show only their own colour and roughness, and their masks aren't read.
    /// </summary>
    WearAndDirt = 1 << 10,

    /// <summary>The height fog from <see cref="WeatherSettings.Fog"/>. Without it the air is clear, whatever its density.</summary>
    Fog = 1 << 11,

    /// <summary>
    /// Metering the frame for <see cref="PostSettings.AutoExposure"/>. Without it the fixed
    /// <see cref="PostSettings.Exposure"/> sets the brightness, whatever the frame asks for.
    /// </summary>
    AutoExposure = 1 << 12,

    /// <summary>
    /// Blurring what's nearer or further than <see cref="PostSettings.FocusDistance"/>, as a camera lens
    /// does once <see cref="PostSettings.Aperture"/> opens it. Without it everything is sharp.
    /// </summary>
    DepthOfField = 1 << 13,

    /// <summary>The frame's <see cref="FrameExtras.Decals"/>. Without them stains and grime aren't drawn.</summary>
    Decals = 1 << 14,

    /// <summary>
    /// Working out the fog near the camera in a volume lit by the sun's shadows and the point and
    /// spot lights, up to <see cref="FogSettings.VolumetricDistance"/>. Without it the fog is the
    /// plain height fog everywhere, with no shafts of light or halos.
    /// </summary>
    VolumetricFog = 1 << 15,

    /// <summary>
    /// Water and a damp band on surfaces whose material has <see cref="MaterialFlags.Puddles"/>, below and just
    /// above <see cref="WeatherSettings.Puddles"/>. Without it every surface is dry.
    /// </summary>
    Puddles = 1 << 16,

    /// <summary>
    /// Materials with <see cref="Material.Wind"/> sway in <see cref="WeatherSettings.Wind"/>, and their shadows with them.
    /// Without it everything stands still.
    /// </summary>
    Wind = 1 << 17,

    /// <summary>
    /// Light read from the <c>.lightbake</c> files in <see cref="RendererBaking.Bakes"/> for the
    /// <see cref="BakedAreas.Space"/> and <see cref="BakedAreas.Chunks"/>. Without it every bake counts as
    /// missing, so each area is lit live, as if it had never been baked.
    /// </summary>
    BakedLighting = 1 << 18,

    /// <summary>
    /// Surfaces sunk into their height maps (<see cref="MaterialHeight.Texture"/>). Without it they are drawn flat, shaped
    /// only by their normal maps, and the height map is read only where puddles need it.
    /// </summary>
    HeightMaps = 1 << 19,

    /// <summary>
    /// Shine: highlights from lights, reflections, coats and the darker grazing look of metals. Without it every surface,
    /// metals and water too, is lit only diffusely, the way chalk is, in its own colour.
    /// </summary>
    Specular = 1 << 20,

    /// <summary>
    /// The light the sky gives from each direction, worked out from <see cref="EnvironmentSettings.Sky"/> or the studio. Without it
    /// the sky lights and reflects as one flat colour, its average, the same from every side. The background still shows
    /// the sky.
    /// </summary>
    EnvironmentLight = 1 << 21,

    /// <summary>
    /// The light materials give off (<see cref="Material.Emissive"/>), on screen and in the light probes. Without it
    /// nothing glows. Bakes keep the glow they were made with.
    /// </summary>
    Emissive = 1 << 22,

    /// <summary>The corners darkened by <see cref="PostSettings.Vignette"/>. Without it they stay as bright as the rest.</summary>
    Vignette = 1 << 23,

    /// <summary>The film grain from <see cref="PostSettings.Grain"/>. Without it the picture is clean.</summary>
    Grain = 1 << 24,

    /// <summary>The crispness <see cref="PostSettings.Sharpen"/> gives back after anti-aliasing. Without it the frame is left as it is.</summary>
    Sharpening = 1 << 25,

    /// <summary>
    /// The view's grade: <see cref="PostSettings.Contrast"/>, <see cref="PostSettings.Saturation"/>,
    /// <see cref="PostSettings.Lift"/>, <see cref="PostSettings.Gain"/> and the white balance. Without it they are all
    /// neutral. The tonemapper and its look stay, as they're how the light fits on a screen.
    /// </summary>
    ColorGrading = 1 << 26,

    /// <summary>
    /// Leaving out what a camera or a shadow map can't see. Without it every draw is drawn by every pass, which only costs
    /// time, so it's for checking what culling hides.
    /// </summary>
    Culling = 1 << 27,

    /// <summary>
    /// Swapping in a model's simpler versions as it moves away, by <see cref="RenderSettings.MeshDetailScale"/>. Without it every
    /// model is drawn at full detail at any distance, which only costs time, so it's for checking what the simpler
    /// versions change.
    /// </summary>
    SimplerModels = 1 << 28,

    /// <summary>
    /// Drawing the depth of large solid surfaces ahead of shading them, so a pixel is shaded once however many
    /// surfaces lie behind it. Without it every surface drawn before the one that hides it is shaded too, which only
    /// costs time, so it's for measuring what the extra pass saves. The picture is the same either way, but where two
    /// surfaces lie in the very same plane, where the other of the two may show.
    /// </summary>
    DepthFirst = 1 << 29,

    // Bit 30 is the renderer's debug view bit, so no effect uses it.

    /// <summary>
    /// Light spreading under the surface of skin and wax, from <see cref="Material.Subsurface"/>. Without it those
    /// surfaces are lit as plain matte, light no longer shines through their thin parts, and the two blurs across the
    /// picture are skipped. The last bit the shaders can read.
    /// </summary>
    Subsurface = 1L << 31,

    /// <summary>The frame's <see cref="FrameExtras.Particles"/>. Without them no particle is uploaded or drawn.</summary>
    Particles = 1L << 32,

    /// <summary>
    /// Point and spot lights, from <see cref="FrameView.LocalLights"/>. Without them those lights give no light: none
    /// is clustered, shadowed or lit onto the light probes. Light a bake already holds stays, and no bake is redone.
    /// </summary>
    LocalLights = 1L << 33,

    /// <summary>
    /// Distant things fading into the sky behind them, from <see cref="BackgroundSettings.FadeStart"/> to
    /// <see cref="BackgroundSettings.FadeEnd"/>. Without it they keep their colours. The sky's own colour stays.
    /// </summary>
    Haze = 1L << 34,

    /// <summary>
    /// The water from <see cref="WeatherSettings.Water"/>. Without it the view is drawn as if the camera were not under water:
    /// no absorption, caustics or light shafts.
    /// </summary>
    Underwater = 1L << 35,

    /// <summary>
    /// Fading things out with distance, from <see cref="Material.Fade"/> and <see cref="DrawItem.Fade"/>, cross-fading
    /// between a model and its simpler versions, and glass fading where it meets the scene, from
    /// <see cref="MaterialGlass.SoftEdge"/>. Without it a distance fade is a hard cut halfway through, a simpler version
    /// swaps in at once and glass ends in a hard line.
    /// </summary>
    Fades = 1L << 36,

    /// <summary>
    /// Fog volumes, the shapes of fog a view places with <c>View.FogVolume</c>. Without it only the scene's own fog
    /// fills the volumetric fog. It needs <see cref="VolumetricFog"/>.
    /// </summary>
    FogVolumes = 1L << 37,

    /// <summary>
    /// Light bounced from what's on screen, from <see cref="ScreenSpaceSettings.Bounce"/>: a sunlit floor tints the wall beside
    /// it and a glowing sign lights the bricks round it. Without it the soft light comes from the light probes and the
    /// sky alone. It's found with the ambient occlusion, so it needs <see cref="AmbientOcclusion"/>.
    /// </summary>
    ScreenBounce = 1L << 38,

    /// <summary>
    /// The physical sky's air between the eye and what it sees, from <see cref="AnalyticSky.Turbidity"/>: far hills turn
    /// blue and pale, and towards a low sun they glow warm. Without it they keep their colours however far they are.
    /// A view that sets its own haze, with <see cref="BackgroundSettings.FadeEnd"/>, gets that haze instead.
    /// </summary>
    AerialPerspective = 1L << 39,

    /// <summary>
    /// The stars in the physical sky at night, from <see cref="AnalyticSky.HideStars"/>. They show in the background
    /// only, fading in as the sky darkens after sunset. Without them the night sky is its plain glow and the moon.
    /// </summary>
    Stars = 1L << 40,

    /// <summary>
    /// The average of the ambient occlusion and the screen bounce over the last frames, which smooths their grain
    /// without blurring them. Without it each frame's occlusion is blurred wide instead. It needs
    /// <see cref="AmbientOcclusion"/>.
    /// </summary>
    OcclusionHistory = 1L << 41,

    All =SunShadows | LightShadows | AmbientOcclusion | Reflections | AntiAliasing | Bloom | Materials | ContactShadows | LightProbes
        | MultiscatterSpecular | WearAndDirt | Fog | AutoExposure | DepthOfField | Decals | VolumetricFog | Puddles | Wind
        | BakedLighting | HeightMaps | Specular | EnvironmentLight | Emissive | Vignette | Grain | Sharpening | ColorGrading
        | Culling | SimplerModels | DepthFirst | Subsurface | Particles | LocalLights | Haze | Underwater | Fades | FogVolumes
        | ScreenBounce | AerialPerspective | Stars | OcclusionHistory,
}
