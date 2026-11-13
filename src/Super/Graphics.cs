using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// The most of the sun's shadow cascades drawn, from 1 to 4, whatever <see cref="View.Shadows"/> asks for. Each
    /// cascade covers ground further out than the last and costs drawing the scene once more, so fewer are faster,
    /// and the sun's shadows end sooner.
    /// </summary>
    public int ShadowCascades
    {
        get;
        set => field = value is >= 1 and <= MaxShadowCascades
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The sun can draw from 1 to 4 shadow cascades.");
    } = MaxShadowCascades;

    private const int MaxShadowCascades = 4;

    /// <summary>
    /// How long models keep their finer versions as they move away, from 0.5 to 2. At 1 a model switches to a simpler
    /// version once that would stray by a pixel on screen. At 0.5 it may stray by two pixels, so it switches
    /// sooner and fewer triangles are drawn; at 2 it keeps the finer one longer. Shadows follow in step.
    /// </summary>
    public float MeshDetail
    {
        get;
        set => field = value is >= Renderer.MinMeshDetail and <= Renderer.MaxMeshDetail
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, string.Create(CultureInfo.InvariantCulture,
                $"Mesh detail can be from {Renderer.MinMeshDetail} to {Renderer.MaxMeshDetail}."));
    } = 1f;

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
    /// What smooths the scene's edges when <see cref="AntiAliasing"/> is on. <see cref="Upscaler.Taa"/> blends
    /// each frame with the last. <see cref="Upscaler.Fsr"/> hands the frame to AMD FSR, which rebuilds it at the
    /// window's size from the scene and its own history, so a <see cref="RenderScale"/> below 1 comes back
    /// sharper than a stretch. FSR needs amd_fidelityfx_vk.dll next to the game and is used only at a render
    /// scale of 1 or less; otherwise TAA is used. <c>--upscaler</c> sets it from the command line, and
    /// <see cref="Fastest"/> uses TAA.
    /// </summary>
    public Upscaler Upscaler { get; set; } = Upscaler.Taa;

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

    /// <summary>
    /// How many light probes follow the camera, and how many are updated a frame, when <see cref="LightProbes"/> is on.
    /// <see cref="Rendition.ProbeQuality.High"/>, the default, keeps the most and updates 32 a frame, for about 170 MB;
    /// <see cref="Rendition.ProbeQuality.Medium"/> updates 16 for about 73 MB, and <see cref="Rendition.ProbeQuality.Low"/>
    /// updates 8 for about 43 MB, so the bounce fills in and follows changes more slowly. A change while the game runs
    /// starts every probe again on the next frame, which waits for the GPU once. <see cref="Fastest"/> sets Low.
    /// </summary>
    public ProbeQuality ProbeQuality
    {
        get;
        set => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Probe quality can be Low, Medium or High.");
    } = ProbeQuality.High;

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
    /// Grass, leaves and anything else whose material sways in the wind set with <see cref="View.Wind"/>, along with
    /// their shadows. Off, everything stands still. It costs a little on each swaying vertex, in the picture and in
    /// every shadow map, so <see cref="Fastest"/> turns it off.
    /// </summary>
    public bool Wind { get; set; } = true;

    /// <summary>
    /// Light read from the bakes made with <c>--bake</c> for the areas set with <see cref="View.BakedLighting"/> and
    /// <see cref="View.BakedChunk"/>. Off, every bake is ignored and each area is lit live, as if it had never been
    /// baked, which shows what the bake changes. A bake costs less than the live light it stands in for, so
    /// <see cref="Fastest"/> leaves it on.
    /// </summary>
    public bool BakedLighting { get; set; } = true;

    /// <summary>
    /// Bakes missing lighting as it appears, where the GPU can trace. From source: spaces and chunks, Preview then
    /// Final. Published: chunks only, Preview. Only play in a window bakes this way; screenshots, recordings, profiles
    /// and <c>--bake</c> never do. See "Baking without a build step" in docs/rendering.md.
    /// </summary>
    public bool BakeOnArrival { get; set; } = true;

    /// <summary>
    /// GPU milliseconds a frame for background bakes, from 0.5 to 33. Null: 4 from source, 2 in a published game. More
    /// finishes a bake sooner and costs frame rate while it runs.
    /// </summary>
    public float? BakeBudget
    {
        get;
        set => field = value is null or (>= MinBakeBudget and <= MaxBakeBudget)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), string.Create(CultureInfo.InvariantCulture,
                $"BakeBudget must be from {MinBakeBudget} to {MaxBakeBudget} milliseconds, not {value}. Use null for the default."));
    }

    private const float MinBakeBudget = 0.5f;
    private const float MaxBakeBudget = 33f;

    /// <summary>
    /// What the frame shows: the lit scene, the default, or a look inside the renderer such as
    /// <see cref="Rendition.DebugView.Probes"/>, which a game can switch on with a key. It is not an effect and costs
    /// nothing while <see cref="Rendition.DebugView.None"/>. <c>--debug-view</c> on the command line wins: while it
    /// names a view, this is ignored. <see cref="Rendition.DebugView.Trace"/> needs a GPU that can trace rays; where
    /// the GPU can't, it is ignored and the lit scene is shown, as a game can't know what a player's GPU has.
    /// </summary>
    [JsonIgnore]
    public DebugView DebugView { get; set; } = DebugView.None;

    /// <summary>
    /// A ready-made set of settings for a <see cref="GraphicsQuality"/>. <see cref="GraphicsQuality.High"/> is the same
    /// as <c>new Graphics()</c>. Every preset keeps vsync on and no frame limit, as those are the player's choice, not
    /// a quality. Hand one to <see cref="Apply"/> to switch a running game to it.
    /// </summary>
    public static Graphics For(GraphicsQuality quality) => quality switch
    {
        GraphicsQuality.Low => new Graphics
        {
            RenderScale = 0.75f,
            ShadowResolution = 1024,
            ShadowCascades = 2,
            MeshDetail = 0.5f,
            ContactShadows = false,
            LightShadows = false,
            LightProbes = false,
            ProbeQuality = ProbeQuality.Low,
            AmbientOcclusion = false,
            Reflections = false,
            AutoExposure = false,
            DepthOfField = false,
            Decals = false,
            VolumetricFog = false,
            // Puddles and wind stay on: in the Courtyard each costs less than a frame's noise, and the water is part
            // of the place, not a finish on it.
            BakeBudget = 1f,
        },
        GraphicsQuality.Medium => new Graphics
        {
            RenderScale = 0.85f,
            ShadowCascades = 3,
            MeshDetail = 0.75f,
            ProbeQuality = ProbeQuality.Medium,
            DepthOfField = false,
            VolumetricFog = false,
            BakeBudget = 2f,
        },
        GraphicsQuality.High => new Graphics(),
        GraphicsQuality.Ultra => new Graphics
        {
            ShadowResolution = 4096,
            MeshDetail = 1.5f,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Graphics quality can be Low, Medium, High or Ultra."),
    };

    /// <summary>
    /// The quality to start a player on for their GPU, a guess from its kind and memory. Built into the processor or
    /// virtual: Low. A card of its own: Low under 3 GB, Medium under 6 GB, High under 12 GB and Ultra from there. An
    /// unknown GPU gets High, the default.
    /// </summary>
    public static GraphicsQuality Recommend(GpuInfo gpu) => gpu.Kind switch
    {
        GpuKind.Integrated or GpuKind.Virtual => GraphicsQuality.Low,
        GpuKind.Discrete => gpu.MemoryBytes switch
        {
            < 3 * Gigabyte => GraphicsQuality.Low,
            < 6 * Gigabyte => GraphicsQuality.Medium,
            < 12 * Gigabyte => GraphicsQuality.High,
            _ => GraphicsQuality.Ultra,
        },
        _ => GraphicsQuality.High,
    };

    private const long Gigabyte = 1L << 30;

    /// <summary>
    /// Takes every setting from <paramref name="profile"/> but <see cref="DebugView"/>, so a game can switch the
    /// <see cref="Graphics"/> it was handed to a preset or a saved profile while it runs:
    /// <c>graphics.Apply(Graphics.For(GraphicsQuality.Low))</c>. The next frame follows.
    /// </summary>
    public void Apply(Graphics profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Vsync = profile.Vsync;
        MaxFramesPerSecond = profile.MaxFramesPerSecond;
        SunShadows = profile.SunShadows;
        ShadowResolution = profile.ShadowResolution;
        ShadowCascades = profile.ShadowCascades;
        MeshDetail = profile.MeshDetail;
        RenderScale = profile.RenderScale;
        Upscaler = profile.Upscaler;
        ContactShadows = profile.ContactShadows;
        LightProbes = profile.LightProbes;
        ProbeQuality = profile.ProbeQuality;
        LightShadows = profile.LightShadows;
        AmbientOcclusion = profile.AmbientOcclusion;
        MultiscatterSpecular = profile.MultiscatterSpecular;
        Reflections = profile.Reflections;
        AntiAliasing = profile.AntiAliasing;
        Bloom = profile.Bloom;
        Fog = profile.Fog;
        AutoExposure = profile.AutoExposure;
        DepthOfField = profile.DepthOfField;
        Decals = profile.Decals;
        VolumetricFog = profile.VolumetricFog;
        Materials = profile.Materials;
        WearAndDirt = profile.WearAndDirt;
        Puddles = profile.Puddles;
        Wind = profile.Wind;
        BakedLighting = profile.BakedLighting;
        BakeOnArrival = profile.BakeOnArrival;
        BakeBudget = profile.BakeBudget;
    }

    /// <summary>
    /// Writes every setting but <see cref="DebugView"/> to a JSON profile at <paramref name="path"/>, which
    /// <see cref="Load"/> and <c>--graphics</c> read back. Edit it by hand to make a profile of your own.
    /// </summary>
    public void Save(string path)
    {
        File.WriteAllText(path, ToJson());
    }

    /// <summary>The settings as a JSON profile, as <see cref="Save"/> writes it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, GraphicsProfile.Options);

    /// <summary>
    /// Reads a JSON profile that <see cref="Save"/> wrote or a person wrote by hand. A setting it leaves out keeps
    /// its default, so a profile can hold only what it changes. <c>"Extends"</c> names a preset to start from instead of the
    /// defaults: <c>Low</c>, <c>Medium</c>, <c>High</c>, <c>Ultra</c> or <c>Fastest</c>, so
    /// <c>{ "Extends": "Low", "Reflections": true }</c> is Low with reflections. A name it doesn't know, or a value a
    /// setting refuses, throws <see cref="InvalidDataException"/> naming the file.
    /// </summary>
    public static Graphics Load(string path) => FromJson(File.ReadAllText(path), path);

    /// <summary>Reads a JSON profile from text. See <see cref="Load"/>; <paramref name="source"/> names it in errors.</summary>
    public static Graphics FromJson(string json, string source = "the graphics profile") =>
        GraphicsProfile.Read(json, source, quality => quality is { } named ? For(named) : Fastest());

    /// <summary>
    /// Every effect off but the materials and the baked lighting, and vsync off: the fastest the engine draws, and a
    /// start for a game that must run on modest hardware. Turn back on only what it needs. The light probes are at
    /// <see cref="Rendition.ProbeQuality.Low"/>, so turning them back on keeps them cheap, and models switch to their
    /// simpler versions at the lowest <see cref="MeshDetail"/>.
    /// </summary>
    public static Graphics Fastest() => new()
    {
        Vsync = false,
        Upscaler = Upscaler.Taa,
        MeshDetail = 0.5f,
        SunShadows = false,
        ContactShadows = false,
        LightProbes = false,
        ProbeQuality = ProbeQuality.Low,
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
        Wind = false,
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
        | (Wind ? 0 : RenderEffects.Wind)
        | (BakedLighting ? 0 : RenderEffects.BakedLighting)
        | (Materials ? 0 : RenderEffects.Materials)
        | (WearAndDirt ? 0 : RenderEffects.WearAndDirt);
}
