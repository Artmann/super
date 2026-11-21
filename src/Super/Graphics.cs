using System.Text.Json;
using System.Text.Json.Serialization;
using Rendition;

namespace Super;

/// <summary>
/// How the game is drawn: which effects are worth their GPU time, and how fast frames come.
/// Set it in <see cref="GameOptions.Graphics"/>, or ask for it in a constructor and change
/// it as the game runs, and the next frame follows. Every effect starts on, as the engine
/// looks best, and vsync starts off, so frames come as fast as they are drawn. Each effect turned off skips its work on
/// the GPU, not only its look. The settings come in groups: <see cref="Screen"/>, <see cref="Shadowing"/>,
/// <see cref="Lighting"/>, <see cref="Atmosphere"/>, <see cref="Surfaces"/>, <see cref="Picture"/>,
/// <see cref="Baking"/> and <see cref="Drawing"/>.
/// </summary>
public sealed class Graphics
{
    /// <summary>How fast frames come, how large the scene is drawn and what smooths its edges.</summary>
    public GraphicsScreen Screen { get; } = new();

    /// <summary>The sun's shadows and the point and spot lights' shadows.</summary>
    public GraphicsShadowing Shadowing { get; } = new();

    /// <summary>Point and spot lights, bounced light, ambient occlusion, reflections, shine and glow.</summary>
    public GraphicsLighting Lighting { get; } = new();

    /// <summary>Fog, haze, the physical sky, water and the volumetric fog.</summary>
    public GraphicsAtmosphere Atmosphere { get; } = new();

    /// <summary>Materials, wear, height maps, puddles, wind, decals, subsurface light and particles.</summary>
    public GraphicsSurfaces Surfaces { get; } = new();

    /// <summary>Bloom, exposure, depth of field, vignette, grain, sharpening and the colour grade.</summary>
    public GraphicsPicture Picture { get; } = new();

    /// <summary>Reading bakes, and baking missing lighting in play.</summary>
    public GraphicsBaking Baking { get; } = new();

    /// <summary>Mesh detail, culling, simpler models, the depth-first pass and fades.</summary>
    public GraphicsDrawing Drawing { get; } = new();

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
    /// as <c>new Graphics()</c>. Every preset keeps vsync off and no frame limit, as those are the player's choice, not
    /// a quality. Hand one to <see cref="Apply"/> to switch a running game to it.
    /// </summary>
    public static Graphics For(GraphicsQuality quality) => quality switch
    {
        GraphicsQuality.Low => Low(),
        GraphicsQuality.Medium => Medium(),
        GraphicsQuality.High => new Graphics(),
        GraphicsQuality.Ultra => Ultra(),
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Graphics quality can be Low, Medium, High or Ultra."),
    };

    /// <summary>
    /// The quality to start a player on for their GPU, a guess from its kind and memory. Built into the processor or
    /// virtual: Low. A card of its own: Low under 3 GB, Medium under 6 GB, High under 12 GB and Ultra from there. An
    /// unknown GPU gets High, the default.
    /// </summary>
    public static GraphicsQuality Recommend(GpuInfo gpu) => QualityGuess.For(gpu);

    /// <summary>
    /// Every effect that costs GPU time off: the fastest the engine draws, and a start for a game that must
    /// run on modest hardware. Turn back on only what it needs. Point and spot lights and particles stay, as they
    /// are the game's own content rather than effects. What costs nothing or saves time stays on: the materials,
    /// the baked lighting, the shine, the sky's light, glows, the colour grade, culling and simpler models. The light probes are at
    /// <see cref="Rendition.ProbeQuality.Low"/>, so turning them back on keeps them cheap, and models switch to their
    /// simpler versions at the lowest <see cref="GraphicsDrawing.MeshDetail"/>.
    /// </summary>
    public static Graphics Fastest() => new()
    {
        Screen = { Upscaler = Upscaler.Taa, AntiAliasing = false },
        Shadowing = { SunShadows = false, ContactShadows = false, LightShadows = false },
        Lighting =
        {
            LightProbes = false,
            ProbeQuality = ProbeQuality.Low,
            AmbientOcclusion = false,
            ScreenBounce = false,
            OcclusionHistory = false,
            MultiscatterSpecular = false,
            Reflections = false,
            PlanarReflections = false,
        },
        Atmosphere = { Fog = false, VolumetricFog = false, FogVolumes = false, Clouds = false, VolumetricClouds = false, CloudShadows = false },
        Surfaces = { WearAndDirt = false, Decals = false, Puddles = false, Wind = false, HeightMaps = false, Subsurface = false, Grass = false },
        Picture =
        {
            Bloom = false,
            AutoExposure = false,
            DepthOfField = false,
            Vignette = false,
            Grain = false,
            Sharpening = false,
            ChromaticAberration = false,
            SpeedBlur = false,
            LensFlare = false,
            Paint = false,
        },
        Drawing = { MeshDetail = 0.5f },
    };

    /// <summary>
    /// Takes every setting from <paramref name="profile"/> but <see cref="DebugView"/>, so a game can switch the
    /// <see cref="Graphics"/> it was handed to a preset or a saved profile while it runs:
    /// <c>graphics.Apply(Graphics.For(GraphicsQuality.Low))</c>. The next frame follows.
    /// </summary>
    public void Apply(Graphics profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Screen.CopyFrom(profile.Screen);
        Shadowing.CopyFrom(profile.Shadowing);
        Lighting.CopyFrom(profile.Lighting);
        Atmosphere.CopyFrom(profile.Atmosphere);
        Surfaces.CopyFrom(profile.Surfaces);
        Picture.CopyFrom(profile.Picture);
        Baking.CopyFrom(profile.Baking);
        Drawing.CopyFrom(profile.Drawing);
    }

    /// <summary>
    /// Writes every setting but <see cref="DebugView"/> to a JSON profile at <paramref name="path"/>, which
    /// <see cref="Load"/> and <c>--graphics</c> read back. Edit it by hand to make a profile of your own.
    /// </summary>
    public void Save(string path)
    {
        File.WriteAllText(path, ToJson());
    }

    /// <summary>The settings as a JSON profile, as <see cref="Save"/> writes it: one object for each group.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, GraphicsProfile.Options);

    /// <summary>
    /// Reads a JSON profile that <see cref="Save"/> wrote or a person wrote by hand, with one object for each group of
    /// settings. A setting it leaves out keeps its default, so a profile can hold only what it changes.
    /// <c>"Extends"</c> names a preset to start from instead of the defaults: <c>Low</c>, <c>Medium</c>, <c>High</c>,
    /// <c>Ultra</c> or <c>Fastest</c>, so <c>{ "Extends": "Low", "Lighting": { "Reflections": true } }</c> is Low with
    /// reflections. A name it doesn't know, or a value a setting refuses, throws <see cref="InvalidDataException"/>
    /// naming the file.
    /// </summary>
    public static Graphics Load(string path) => FromJson(File.ReadAllText(path), path);

    /// <summary>Reads a JSON profile from text. See <see cref="Load"/>; <paramref name="source"/> names it in errors.</summary>
    public static Graphics FromJson(string json, string source = "the graphics profile") =>
        GraphicsProfile.Read(json, source, quality => quality is { } named ? For(named) : Fastest());

    private static Graphics Low() => new()
    {
        Screen = { RenderScale = 0.75f },
        Shadowing = { ShadowResolution = 1024, ShadowCascades = 2, ContactShadows = false, LightShadows = false },
        Lighting = { LightProbes = false, ProbeQuality = ProbeQuality.Low, AmbientOcclusion = false, ScreenBounce = false, Reflections = false, PlanarReflections = false },
        Atmosphere = { VolumetricFog = false, VolumetricClouds = false },

        // Puddles and wind stay on: in the Courtyard each costs less than a frame's noise, and the water is part
        // of the place, not a finish on it.
        Surfaces = { Decals = false, HeightMaps = false },
        Picture = { AutoExposure = false, DepthOfField = false, Paint = false },
        Baking = { BakeBudget = 1f },
        Drawing = { MeshDetail = 0.5f, GrassDetail = 0.5f },
    };

    private static Graphics Medium() => new()
    {
        Screen = { RenderScale = 0.85f },
        Shadowing = { ShadowCascades = 3 },
        Lighting = { ProbeQuality = ProbeQuality.Medium },
        // The flat clouds and their shadow cost a few percent of a frame; the march costs about a tenth of one.
        Atmosphere = { VolumetricFog = false, VolumetricClouds = false },
        Picture = { DepthOfField = false },
        Baking = { BakeBudget = 2f },
        Drawing = { MeshDetail = 0.75f, GrassDetail = 0.75f },
    };

    private static Graphics Ultra() => new()
    {
        Shadowing = { ShadowResolution = 4096 },
        Lighting = { ReflectionResolution = ReflectionResolution.Full, OcclusionResolution = OcclusionResolution.Full },
        Atmosphere = { VolumetricFogSlices = 128 },
        Drawing = { MeshDetail = 1.5f, GrassDetail = 1.25f },
    };
}
