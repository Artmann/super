using System.Numerics;

namespace Rendition;

/// <summary>What a frame shows: the lit scene, or a look inside the renderer.</summary>
public enum DebugView
{
    /// <summary>The lit scene.</summary>
    None,

    /// <summary>
    /// What a ray from the camera hits in the acceleration structures, as the surface's base colour, unlit and
    /// ungraded; sky blue where it hits nothing. Shows what rays see, which may differ from what's drawn.
    /// </summary>
    Trace,

    /// <summary>
    /// The lit scene with a small sphere at every light probe of the finest cascade and of the fixed grid, shaded with
    /// the light the probe holds for each direction its surface faces. A probe not captured yet is a grey wireframe,
    /// and a probe inside a wall isn't drawn. Needs no ray tracing, and shows nothing while the probes are off.
    /// </summary>
    Probes,
}

/// <summary>
/// One fixed place in the world whose lighting is baked as it stands, sun included: a box, the spacing of the baked
/// probes it holds, and the lightmap texels for each metre of surface, 1 to
/// <see cref="BakeSettings.MaxTexelsPerMetre"/>. Its bake is <see cref="BakeFiles.Space"/>, and a bake made at another
/// density is stale.
/// </summary>
public readonly record struct BakedSpace(
    string Name, Vector3 Min, Vector3 Max, float ProbeSpacing, int TexelsPerMetre = BakeSettings.DefaultTexelsPerMetre);

/// <summary>
/// A piece of the world baked once in its own space and placed with <see cref="Transform"/>, which may only turn and
/// move it. <see cref="Min"/> and <see cref="Max"/> are its box in its own space, and <see cref="TexelsPerMetre"/> its
/// lightmap density, as for a <see cref="BakedSpace"/>. Its bakes are <see cref="BakeFiles.Chunk"/>.
/// <see cref="EdgeBlend"/> is how many metres in from the box's four sides, its own ±x and ±z, the bake's light blends
/// into the live light, so the chunk meets what stands beside it without a step: 0 to half the box's smaller side across
/// x and z, and 0 for a hard edge. It changes how the bake is drawn, not what it holds, so it isn't in the bake's hash.
/// </summary>
public readonly record struct BakedChunk(
    string Name, Matrix4x4 Transform, Vector3 Min, Vector3 Max, int TexelsPerMetre = BakeSettings.DefaultTexelsPerMetre,
    float EdgeBlend = BakedChunk.DefaultEdgeBlend)
{
    /// <summary>The metres of band a chunk gets unless it asks for another.</summary>
    public const float DefaultEdgeBlend = 0.5f;
}

/// <summary>Everything the renderer needs to know about one frame except the draws.</summary>
public ref struct FrameView
{
    public FrameView()
    {
    }

    public int Width;
    public int Height;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Vector3 CameraPosition;

    /// <summary>Seconds. Reaches shaders as frame.time, for animated surfaces.</summary>
    public float Time;

    /// <summary>At most eight are used.</summary>
    public ReadOnlySpan<DirectionalLight> Lights;

    /// <summary>Point and spot lights. At most <see cref="LocalLight.MaxPerFrame"/>; more throws.</summary>
    public ReadOnlySpan<LocalLight> LocalLights;

    /// <summary>
    /// Every skinned draw's matrices for this frame, one after another, which draws
    /// point into with <see cref="DrawItem.Bones"/>. At most <see cref="Renderer.MaxBones"/>.
    /// </summary>
    public ReadOnlySpan<Matrix4x4> Bones;

    public ShadowSettings Shadows = new();

    /// <summary>
    /// A box of light probes that gives the surfaces inside it bounced light in place of the sky's,
    /// or none. At most <see cref="ProbeGrid.MaxProbes"/>. See docs/rendering.md.
    /// </summary>
    public ProbeVolumeSettings? LightProbes;

    /// <summary>Effects to leave out of this frame. Left empty, every effect is drawn.</summary>
    public RenderEffects Off;

    /// <summary>The sky and how strongly the environment lights the scene.</summary>
    public EnvironmentSettings Environment;

    /// <summary>What shows where nothing was drawn, and how distant surfaces melt into it.</summary>
    public BackgroundSettings Background;

    /// <summary>The occlusion, bounced light and outlines worked out from the screen.</summary>
    public ScreenSpaceSettings ScreenSpace = new();

    /// <summary>The fog, the water the camera may be under, puddles and the wind.</summary>
    public WeatherSettings Weather;

    /// <summary>The finished picture's look, a move between pictures, and the debug views.</summary>
    public PictureSettings Picture;

    /// <summary>What the frame draws besides its draws: the overlay, particles, decals and fog volumes.</summary>
    public FrameExtras Extras;

    /// <summary>The baked space and the baked chunks the frame places.</summary>
    public BakedAreas Baked;
}

/// <summary>The sky and how strongly the environment lights the scene, for <see cref="FrameView.Environment"/>.</summary>
public struct EnvironmentSettings
{
    /// <summary>Scales the light and reflections from the environment.</summary>
    public float Intensity;

    /// <summary>A real clear sky in place of the environment's bands, or, with turbidity 0, the bands.</summary>
    public AnalyticSky Sky;

    /// <summary>
    /// Shows the environment's bands where nothing was drawn, scaled by
    /// <see cref="Intensity"/>, so the sky behind the scene is the one lighting it.
    /// Distant surfaces then melt into the sky in their own direction.
    /// </summary>
    public bool ShowAsBackground;
}

/// <summary>What shows where nothing was drawn, and how far surfaces melt into it, for <see cref="FrameView.Background"/>.</summary>
public struct BackgroundSettings
{
    /// <summary>Shown where nothing was drawn, unless <see cref="EnvironmentSettings.ShowAsBackground"/> is set.</summary>
    public Vector3 Radiance;

    /// <summary>Surfaces melt into the background between these distances. Equal values turn it off.</summary>
    public float FadeStart;
    public float FadeEnd;

    /// <summary>
    /// Where the fade's distance is measured from, the camera when unset. Set, the distance is taken
    /// across the ground only, so tall things fade no sooner than what they stand on. A point the camera
    /// looks at suits a camera far off, as an isometric one is, where everything on screen is about as far away.
    /// </summary>
    public Vector3? FadeOrigin;

    /// <summary>What distant surfaces fade into. Zero fades them into the background behind them.</summary>
    public Vector3 FadeColor;
}

/// <summary>What is worked out from the screen, for <see cref="FrameView.ScreenSpace"/>.</summary>
public struct ScreenSpaceSettings
{
    /// <summary>The defaults of each.</summary>
    public ScreenSpaceSettings()
    {
    }

    /// <summary>How nearby surfaces darken the soft light. See docs/rendering.md.</summary>
    public OcclusionSettings Occlusion = new();

    /// <summary>Light bounced from the surfaces on screen. See docs/rendering.md.</summary>
    public BounceSettings Bounce = new();

    /// <summary>The ink lines round materials with <see cref="MaterialFlags.Outline"/>. A thickness of 0 draws none.</summary>
    public OutlineSettings Outlines = new();
}

/// <summary>The air, the water and the wind, for <see cref="FrameView.Weather"/>.</summary>
public struct WeatherSettings
{
    /// <summary>
    /// Height fog between the camera and what it sees, lit by the sky and the suns. It's
    /// drawn for the view only: light and reflection probes capture clear air. See docs/rendering.md.
    /// </summary>
    public FogSettings Fog;

    public UnderwaterSettings Water;

    /// <summary>
    /// The height water stands at on surfaces whose material has <see cref="MaterialFlags.Puddles"/>, and the damp
    /// band above it. Null leaves every surface dry.
    /// </summary>
    public PuddleSettings? Puddles;

    /// <summary>
    /// The wind that sways materials with <see cref="Material.Wind"/>, and their shadows. Null keeps everything still.
    /// </summary>
    public WindSettings? Wind;

    /// <summary>
    /// The clouds in a physical sky, which the wind in <see cref="CloudSettings.Wind"/> carries over
    /// <see cref="FrameView.Time"/>. Null, or <see cref="CloudSettings.Clear"/>, leaves the sky clear. A sky without
    /// a turbidity has none. See "Clouds" in docs/rendering.md.
    /// </summary>
    public CloudSettings? Clouds;
}

/// <summary>The finished picture, for <see cref="FrameView.Picture"/>.</summary>
public struct PictureSettings
{
    public PostSettings Post;

    /// <summary>A fade, dissolve or wipe between two pictures, or none.</summary>
    public ScreenTransition Transition;

    /// <summary>
    /// Shows something other than the lit scene, to look inside the renderer. <see cref="DebugView.Trace"/> needs a
    /// GPU that can trace (<see cref="Renderer.CanTrace"/>), and <see cref="Renderer.Render"/> throws without one.
    /// <see cref="DebugView.Probes"/> works on any GPU.
    /// </summary>
    public DebugView DebugView;

    /// <summary>The glints from the sun and named lights. The default is a subtle flare from the sun.</summary>
    public LensFlareSettings LensFlare;

    /// <summary>The oil-paint look, over the whole picture or its far part. The default paints nothing.</summary>
    public PaintSettings Paint;
}

/// <summary>What a frame draws besides its draws, for <see cref="FrameView.Extras"/>.</summary>
public ref struct FrameExtras
{
    /// <summary>Flat rectangles drawn last, over everything, in order. Needs <see cref="RendererAssets.SetOverlayAtlas"/>.</summary>
    public ReadOnlySpan<OverlayQuad> Overlay;

    /// <summary>
    /// Soft puffs of dust, smoke or spray, blended over the lit scene in any order. At most
    /// <see cref="Particle.MaxPerFrame"/> are drawn; the rest are dropped.
    /// </summary>
    public ReadOnlySpan<Particle> Particles;

    /// <summary>
    /// Materials laid onto what lies inside a box, after the opaque surfaces are drawn and before they're lit.
    /// At most <see cref="Passes.DecalBatch.MaxDecals"/>; more throws. Drawn in order, so a later one covers an earlier one.
    /// </summary>
    public ReadOnlySpan<DecalDraw> Decals;

    /// <summary>
    /// Shapes filled with fog of their own, such as mist in a room or a lamp's beam, drawn into the volumetric fog.
    /// At most <see cref="FogVolumeBatch.MaxVolumes"/>. Empty while <see cref="RenderEffects.VolumetricFog"/> is off.
    /// </summary>
    public ReadOnlySpan<FogVolumeDraw> FogVolumes;

    /// <summary>
    /// Fields of grass, each grown over a ground draw. At most <see cref="GrassField.MaxKinds"/> different looks
    /// between them; any number of grounds may share one. Empty while <see cref="RenderEffects.Grass"/> is off.
    /// </summary>
    public ReadOnlySpan<GrassDraw> Grass;

    /// <summary>
    /// The one level plane that mirrors the scene, drawn from a mirrored camera at half the resolution. Null mirrors at
    /// the puddles' level when <see cref="WeatherSettings.Puddles"/> has some, and nowhere otherwise. None while
    /// <see cref="RenderEffects.PlanarReflections"/> is off.
    /// </summary>
    public ReflectionPlane? ReflectionPlane;
}

/// <summary>The areas whose lighting is baked, for <see cref="FrameView.Baked"/>.</summary>
public ref struct BakedAreas
{
    /// <summary>
    /// The one fixed place this frame whose lighting is baked, or none. Each frame it is checked for a fresh bake; see
    /// <see cref="RendererBaking.LastFrame"/> and docs/rendering.md.
    /// </summary>
    public BakedSpace? Space;

    /// <summary>
    /// The pieces of the world, each baked once in its own space, placed this frame. At most
    /// <see cref="BakeFiles.MaxChunks"/>, each moved only by rotation and translation; otherwise
    /// <see cref="Renderer.Render"/> throws. Each is checked for a fresh bake every frame. See docs/rendering.md.
    /// </summary>
    public ReadOnlySpan<BakedChunk> Chunks;
}
