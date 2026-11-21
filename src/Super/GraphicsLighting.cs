using Rendition;

namespace Super;

/// <summary>
/// The light beyond the sun's: point and spot lights, light bounced by the probes and on screen, ambient occlusion,
/// reflections, and how surfaces shine and glow.
/// </summary>
public sealed class GraphicsLighting
{
    /// <summary>
    /// Point and spot lights. Off, they light nothing: none is clustered, shadowed or lit onto the light probes, so the
    /// scene looks as if they weren't there and the sun and sky light it alone. Light a bake already holds stays, and no
    /// bake is redone; turn <see cref="GraphicsBaking.BakedLighting"/> off too to drop it.
    /// </summary>
    public bool LocalLights { get; set; } = true;

    /// <summary>
    /// The light bounced off walls and floors by <see cref="ViewIndirect.LightProbes"/>. Off, nothing is drawn
    /// into the probes and every surface takes its soft light from the sky, as with no probes at all.
    /// </summary>
    public bool LightProbes { get; set; } = true;

    /// <summary>
    /// How many light probes follow the camera, and how many are updated a frame, when <see cref="LightProbes"/> is on.
    /// <see cref="Rendition.ProbeQuality.High"/>, the default, keeps the most and updates 32 a frame, for about 170 MB;
    /// <see cref="Rendition.ProbeQuality.Medium"/> updates 16 for about 73 MB, and <see cref="Rendition.ProbeQuality.Low"/>
    /// updates 8 for about 43 MB, so the bounce fills in and follows changes more slowly. A change while the game runs
    /// starts every probe again on the next frame, which waits for the GPU once. <see cref="Graphics.Fastest"/> sets Low.
    /// </summary>
    public ProbeQuality ProbeQuality
    {
        get;
        set => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Probe quality can be Low, Medium or High.");
    } = ProbeQuality.High;

    /// <summary>Darkening in corners, creases and under things. Off, they're as bright as open ground.</summary>
    public bool AmbientOcclusion { get; set; } = true;

    /// <summary>
    /// How many pixels work out their ambient occlusion each frame, when <see cref="AmbientOcclusion"/> is on.
    /// <see cref="Rendition.OcclusionResolution.Half"/>, the default, works out one pixel in four, a different one each
    /// frame, and anti-aliasing blends the frames into a full picture; <see cref="Rendition.OcclusionResolution.Full"/>
    /// works out every pixel every frame, which keeps the finest creases a little crisper and costs about three times
    /// as much. A change while the game runs rebuilds the renderer's targets on the next frame, which waits for the
    /// GPU once. <see cref="GraphicsQuality.Ultra"/> sets Full.
    /// </summary>
    public OcclusionResolution OcclusionResolution
    {
        get;
        set => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Occlusion resolution can be Half or Full.");
    } = OcclusionResolution.Half;

    /// <summary>
    /// Light bounced from the surfaces on screen near each point, so a sunlit floor tints the wall beside it and a
    /// glowing sign lights the bricks round it. Off, the soft light comes from the light probes and the sky alone. It
    /// is found with the ambient occlusion, so it needs <see cref="AmbientOcclusion"/>. <c>view.Indirect.ScreenBounce</c>
    /// sets its strength and reach.
    /// </summary>
    public bool ScreenBounce { get; set; } = true;

    /// <summary>
    /// Averages the ambient occlusion and the screen bounce over the last frames, which smooths their grain without
    /// blurring them, so the shading keeps to the creases of small things such as steps and mouldings. Off, each
    /// frame's occlusion is blurred wide instead, which is softer and a little faster. It needs
    /// <see cref="AmbientOcclusion"/>.
    /// </summary>
    public bool OcclusionHistory { get; set; } = true;

    /// <summary>Shiny surfaces and water mirroring what's on screen, and reflection probes. Off, they and glass reflect only the sky.</summary>
    public bool Reflections { get; set; } = true;

    /// <summary>
    /// A sharp mirror of the scene in one flat, level plane, such as a wet floor, puddles or a calm pool, seen from
    /// a camera mirrored under it, so it shows what is off screen or hidden too. It blurs with the surface's roughness,
    /// more the higher what it shows stands over the plane. It is drawn when the view has a plane, set with
    /// <c>view.Surroundings.ReflectionPlane</c> or by puddles. Off, the plane reflects what is on screen and the
    /// probes, as any other surface does. <see cref="GraphicsQuality.Low"/> and <see cref="Graphics.Fastest"/> turn it off.
    /// </summary>
    public bool PlanarReflections { get; set; } = true;

    /// <summary>
    /// How many pixels trace their screen-space reflection each frame, when <see cref="Reflections"/> is on.
    /// <see cref="Rendition.ReflectionResolution.Half"/>, the default, traces one pixel in four, a different one each
    /// frame, and anti-aliasing blends the frames into a full picture; <see cref="Rendition.ReflectionResolution.Full"/>
    /// traces every pixel every frame, which sharpens a reflection that moves fast and costs up to four times as much.
    /// A change while the game runs rebuilds the renderer's targets on the next frame, which waits for the GPU once.
    /// <see cref="GraphicsQuality.Ultra"/> sets Full.
    /// </summary>
    public ReflectionResolution ReflectionResolution
    {
        get;
        set => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Reflection resolution can be Half or Full.");
    } = ReflectionResolution.Half;

    /// <summary>
    /// Rough surfaces keeping the light that bounces between their tiny facets before it leaves. Off,
    /// rough metals such as worn iron and brushed steel come out darker than they should. It costs
    /// almost nothing.
    /// </summary>
    public bool MultiscatterSpecular { get; set; } = true;

    /// <summary>
    /// Shine: highlights from the lights, reflections, metal and the clear coat. Off, every surface is lit as plain
    /// matte, metals in their base colour, and glass lets light through with no reflection. It's for looking, not for
    /// speed, so <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool Specular { get; set; } = true;

    /// <summary>
    /// The soft light and reflections that the sky gives from each direction. Off, the sky gives the same flat colour
    /// from every side, its average, which shows what the direct light and the probes do alone. The light probes still
    /// see the real sky. It's for looking, so <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool EnvironmentLight { get; set; } = true;

    /// <summary>
    /// Surfaces that give their own light, such as lamps, screens and embers. Off, they show only the light that falls on
    /// them, and they no longer light the probes. Bakes keep the glow they were made with. It's for looking, so
    /// <see cref="Graphics.Fastest"/> leaves it on.
    /// </summary>
    public bool Emissive { get; set; } = true;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsLighting other)
    {
        LocalLights = other.LocalLights;
        LightProbes = other.LightProbes;
        ProbeQuality = other.ProbeQuality;
        AmbientOcclusion = other.AmbientOcclusion;
        OcclusionResolution = other.OcclusionResolution;
        ScreenBounce = other.ScreenBounce;
        OcclusionHistory = other.OcclusionHistory;
        Reflections = other.Reflections;
        PlanarReflections = other.PlanarReflections;
        ReflectionResolution = other.ReflectionResolution;
        MultiscatterSpecular = other.MultiscatterSpecular;
        Specular = other.Specular;
        EnvironmentLight = other.EnvironmentLight;
        Emissive = other.Emissive;
    }
}
