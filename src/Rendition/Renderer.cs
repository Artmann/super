using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Rendition;

/// <summary>
/// The public face of the renderer. <see cref="Render"/> allocates nothing on the
/// managed heap unless the window has been resized.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    private const int JitterSteps = 16;

    // How much sharper a mip level material textures are read at while anti-aliasing shifts the
    // view. Each frame alone would shimmer, but the frames blended together settle, and the
    // picture keeps detail the blend would otherwise soften.
    private const float TemporalMipBias = -0.75f;

    /// <summary>The most skin matrices one frame can hold across every skinned draw.</summary>
    public const int MaxBones = 16384;

    /// <summary>The most reflection probes the renderer holds at once.</summary>
    public const int MaxReflectionProbes = ReflectionProbeOrder.MaxProbes;


    private struct Frame
    {
        public CommandPool CommandPool;
        public CommandBuffer CommandBuffer;
        public Fence InFlight;
        public Semaphore ImageAvailable;
        public GpuBuffer Uniforms;
        public GpuBuffer Bones;
        public InstanceBuffer Instances;
        public GpuBuffer Lights;
        public GpuBuffer ShadowTiles;
        public GpuBuffer Clusters;
        // The reflection probes the view sees, in the order they blend, as light_data.glsl reads them at binding 13.
        public GpuBuffer ReflectionProbes;
        // The frame's suns and placed chunks, as chunks.glsl reads them at binding 11. See GpuChunk.
        public GpuBuffer Chunks;
        public DescriptorSet Set;

        public readonly void Dispose(Vk api, Device device)
        {
            Uniforms.Dispose();
            Bones.Dispose();
            Instances.Dispose();
            Lights.Dispose();
            ShadowTiles.Dispose();
            Clusters.Dispose();
            ReflectionProbes.Dispose();
            Chunks.Dispose();
            api.DestroySemaphore(device, ImageAvailable, null);
            api.DestroyFence(device, InFlight, null);
            api.DestroyCommandPool(device, CommandPool, null);
        }
    }

    private readonly VulkanContext _context;
    private bool _disableVsync;
    private bool _vsyncChanged;
    // The cascade map size asked for, which the next frame rebuilds the maps at if it differs.
    private int _shadowResolution;
    // The scene's size against the window's, which the next frame rebuilds the targets at if it differs.
    private float _renderScale = 1f;
    private ReflectionResolution _reflectionResolution = ReflectionResolution.Half;
    private OcclusionResolution _occlusionResolution = OcclusionResolution.Half;
    // The largest image side the device can make, which the scene is shrunk to fit.
    private readonly int _maxImageSize;
    private readonly Vk _api;
    private readonly Frame[] _frames = new Frame[FrameRing.Count];
    // How far a simpler model may stray. Shadows are soft, so they can afford more.
    private const float CameraDetailError = 1f;
    private const float ShadowDetailError = 2f;

    private readonly FrameRing _ring = new();
    private readonly MeshStore _meshStore = new();
    private readonly RetiredResources _retired;
    private readonly UploadQueue _uploads;
    private readonly ResourceTable _resources;
    private readonly RayTracing? _tracing;
    private readonly TraceScene? _traceScene;
    private readonly TraceDebugPass? _traceDebugPass;

    // Whether this frame shows the trace debug view, which leaves out what would change its colours.
    private bool _traceView;
    private readonly ProbeDebugPass _probeDebugPass;

    // Whether this frame shows the probe debug view: the lit scene with a sphere at every light probe.
    private bool _probeView;
    private readonly EnvironmentMap _environment;
    private readonly ReflectionProbes _probes;

    private readonly VolumetricFogVolume _volumetricFog;

    // The reflection probe this frame captures, or -1 for none, and where it's placed.
    private readonly ReflectionCapturePass _reflectionCapture;
    private int _reflectionCaptureSlot = -1;
    private ReflectionProbe _reflectionCaptureProbe;

    /// <summary>The live light probes. Tests reach their atlases, cascades and switches through this.</summary>
    internal LightProbes LightProbes { get; }

    /// <summary>What tests do to the live light probes.</summary>
    internal ProbeTestHooks ProbeTests { get; }

    // The frame whose uniforms Render wrote last, so the probe test hooks read what that frame did.
    private int _uniformFrame;

    // Goes up with every SetEnvironment, so the light probes see the sky has changed.
    private int _environmentVersion;

    // What the last SetEnvironment gave, hashed, and the strength the last frame drew it at, for EnvironmentHash.
    private ulong _environmentContent;
    private float _environmentIntensity;

    // The clear sky and sun the environment was last baked with. The lighting hashes cover what
    // decides it through the view, so baking it again leaves the version alone.
    private SkySun _bakedSky;

    // The photographed skies made with CreateSkyImage, by handle, each with its sun and a hash of what it holds.
    private readonly List<(GpuTexture Cube, SkyImageSun Sun, ulong Content)> _skyImages = [];

    // The view's lights with the sun coloured by the physical sky's air; see SkyModel.Tinted.
    private DirectionalLight[] _tintedLights = [];

    /// <summary>The sky or the environment's description changed and its cubes aren't redrawn yet.</summary>
    private bool _environmentPending;

    private readonly DescriptorKit _kit;
    private readonly HistoryCopier _copier;
    private readonly OcclusionStage _occlusion;
    private readonly LightingStage _lighting;
    private readonly DescriptorSet _taaSet;
    private readonly DescriptorSet _tonemapSet;
    private readonly DescriptorSet _finishSet;

    private readonly ShadowPass _shadowPass;
    private readonly GBufferPass _gBufferPass;

    // The camera's view as SelectDraws made it, which decides whose depth the G-buffer pass draws first.
    private DetailView _cameraView;

    private DetailView? DepthFirstView => _off.HasFlag(RenderEffects.DepthFirst) ? null : _cameraView;
    private readonly TransparentPass _transparentPass;
    private readonly ParticlePass _particlePass;
    private readonly DecalPass _decalPass;
    private ParticlePass.CameraConstants _particleCamera;
    private TransparentView _transparentView;
    private int _particleCount;
    private readonly FullscreenPass _taaPass;
    private readonly DepthOfFieldPass _depthOfFieldPass;
    private DepthOfFieldConstants? _depthOfField;
    private readonly BloomPass _bloomPass;
    private readonly FullscreenPass _tonemapPass;
    private readonly ExposureMeter _exposureMeter;
    private readonly ExposureClock _exposureClock = new();
    private ExposureStep? _exposureStep;
    private readonly FullscreenPass _finishPass;
    private readonly OverlayPass _overlayPass;
    private readonly DescriptorSet _overlaySet;
    private readonly GpuTimer _gpuTimer;
    private readonly GpuWorkCounter? _gpuWork;
    private GpuTexture? _overlayAtlas;

    private Swapchain _swapchain;
    private GBuffer _gBuffer;
    private GpuBuffer? _capture;
    private bool _captureRecorded;
    private int _frameNumber;

    /// <summary>The number the next frame drawn gets, which picks its frame of the dither's noise. For tests.</summary>
    internal int FrameNumber => _frameNumber;

    /// <summary>
    /// Whether a baked space's probes blend into the live light over one probe spacing in from its box's faces, as they
    /// always do; off, the box is a hard edge, as before the band. For tests, to show the band is what keeps a moving
    /// thing from jumping as it crosses the box.
    /// </summary>
    internal bool BakedProbeBand { get; set; } = true;

    private Matrix4x4 _previousViewProjection;
    // Last frame's time and where its keyed draws stood, for motion vectors.
    private float _previousTime;
    private readonly MotionHistory _motion = new();

    // The effects this frame leaves out, and whether tonemapping and bloom are bound to the
    // anti-aliased image or, with anti-aliasing left out, straight to the lit one.
    private RenderEffects _off;

    /// <summary>What the trace debug view leaves out, so its colours reach the screen as they were traced.</summary>
    private const RenderEffects TraceViewOff =
        RenderEffects.AntiAliasing | RenderEffects.Bloom | RenderEffects.DepthOfField | RenderEffects.AutoExposure;

    /// <summary>OFF_DEBUG_VIEW in common.glsl: tonemapping and the finish pass pass colours through untouched.</summary>
    private const int DebugViewBit = 1 << 30;

    /// <summary>
    /// OFF_FADES in common.glsl, for <see cref="RenderEffects.Fades"/>, which is past the bits the shaders read. It
    /// takes the bit of <see cref="RenderEffects.DepthFirst"/>, which only the CPU reads.
    /// </summary>
    private const int FadesOffBit = 1 << 29;

    /// <summary>The effects the shaders read through <c>frame.off</c>, an <c>int</c>. The renderer leaves out the rest itself.</summary>
    private const RenderEffects ShadedEffects = (RenderEffects)0xFFFF_FFFFL;

    // How far the frame's wind can move each material's vertices, which culling adds to the bounds.
    private WindReach _windReach;
    private FadeRules _fadeRules;
    private bool _readsAntiAliased = true;

    // The upscaler asked for, the one it comes to once FSR's library is looked for, and whether FSR draws the
    // picture now: only with anti-aliasing on and the scene no larger than the window.
    private Upscaler _upscaler;
    private Upscaler _upscalerUsed;
    private bool _upscaling;
    private readonly FsrPass _fsrPass;
    private FsrFrame _fsrFrame;

    // Whether a sun casts its shadow this frame, so its maps are drawn.
    private bool _sunShadowsDrawn;

    // Whether the frame being recorded draws the volumetric fog.
    private bool _volumetricDrawn;

    // Moving between pictures: what the frame asks for, and which hold the held picture belongs to.
    // Whether the held picture and the last finished frame exist yet is on the targets.
    private TransitionConstants _transition;
    private int _transitionHold;
    private int _heldNumber;

    /// <summary>Vulkan validation messages so far. Always zero when validation is off.</summary>
    public int ValidationErrors => _context.Validation?.Errors ?? 0;

    /// <summary>
    /// Whether the GPU can trace rays, with ray queries turned on for the device. Nothing requires it: a GPU
    /// that can't, or <see cref="RendererOptions.DisableRayTracing"/>, just draws without tracing.
    /// </summary>
    public bool CanTrace => _context.CanTrace;

    /// <summary>Why <see cref="CanTrace"/> is false, such as "no VK_KHR_ray_query", or null when it is true.</summary>
    public string? NoTraceReason => _context.Abilities.NoTraceReason;

    /// <summary>The GPU this renderer draws with: its name, kind and memory.</summary>
    public GpuInfo Gpu => _context.Gpu;

    /// <summary>
    /// Waits for the display before showing each frame, so the picture never tears, and the
    /// frame rate stops at the display's. Turned off, frames are shown as soon as they're done.
    /// Displays that can't do that keep it on. A change takes effect on the next frame.
    /// </summary>
    public bool Vsync
    {
        get => !_disableVsync;
        set
        {
            if (value != !_disableVsync)
            {
                _disableVsync = !value;
                _vsyncChanged = true;
            }
        }
    }

    /// <summary>
    /// How many texels across each of the sun's cascade maps is: 1024, 2048 or 4096. Finer maps
    /// give sharper shadows for four times the memory and drawing each step up. A change
    /// rebuilds the maps before the next frame.
    /// </summary>
    public int ShadowResolution
    {
        get => _shadowResolution;
        set => _shadowResolution = ShadowPass.CheckResolution(value);
    }

    /// <summary>
    /// The most of the sun's cascades drawn, from 1 to <see cref="ShadowPass.MaxCascades"/>, whatever
    /// <see cref="ShadowSettings.Cascades"/> asks for. Each cascade left out saves drawing the scene once more,
    /// and the sun's shadows end where the last cascade drawn ends. Takes effect on the next frame.
    /// </summary>
    public int MaxShadowCascades
    {
        get;
        set => field = value is >= 1 and <= ShadowPass.MaxCascades
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"The sun can draw from 1 to {ShadowPass.MaxCascades} cascades.");
    } = ShadowPass.MaxCascades;

    /// <summary>
    /// Scene pixels across one cell of the volumetric fog's grid, each way, from 4 to 32; 8 unless set. Smaller
    /// cells are sharper and cost more. A change remakes the grid, with no history, before the next frame.
    /// </summary>
    public int VolumetricFogCellSize
    {
        get;
        set => field = VolumetricFog.CheckCellPixels(value);
    } = VolumetricFog.CellPixels;

    /// <summary>
    /// Slices in the volumetric fog's grid, from the camera out to <see cref="FogSettings.VolumetricDistance"/>,
    /// from 16 to 128; 64 unless set. A change remakes the grid, with no history, before the next frame.
    /// </summary>
    public int VolumetricFogSlices
    {
        get;
        set => field = VolumetricFog.CheckSlices(value);
    } = VolumetricFog.Slices;

    /// <summary>The smallest <see cref="MeshDetailScale"/>: models switch to their simpler versions at twice the distance.</summary>
    public const float MinMeshDetail = 0.5f;

    /// <summary>The largest <see cref="MeshDetailScale"/>: models keep their finer versions to twice the distance.</summary>
    public const float MaxMeshDetail = 2f;

    /// <summary>
    /// How long models keep their finer versions as they move away, from 0.5 to 2. 1 switches when a simpler version
    /// would stray by a pixel on screen. At 0.5 they may stray by two, so they switch sooner and fewer triangles are
    /// drawn; at 2 by half a pixel. The sun's shadows follow in step. Takes effect on the next frame.
    /// </summary>
    public float MeshDetailScale
    {
        get;
        set => field = value is >= MinMeshDetail and <= MaxMeshDetail
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                string.Create(CultureInfo.InvariantCulture, $"Mesh detail can be from {MinMeshDetail} to {MaxMeshDetail}."));
    } = 1f;

    /// <summary>The smallest <see cref="RenderScale"/>: a quarter of the window's pixels.</summary>
    public const float MinRenderScale = 0.5f;

    /// <summary>The largest <see cref="RenderScale"/>: four times the window's pixels.</summary>
    public const float MaxRenderScale = 2f;

    /// <summary>
    /// How large the scene is drawn against the window, from 0.5 to 2 each way. Above 1 the scene is
    /// drawn larger and shrunk to fit, which smooths edges and fine detail. Below 1 it's drawn smaller
    /// and stretched, which is faster and softer. 1 draws at the window's size, with nothing resampled.
    /// The overlay is always drawn at the window's size. A change rebuilds the targets before the next frame.
    /// </summary>
    public float RenderScale
    {
        get => _renderScale;
        set => _renderScale = CheckRenderScale(value);
    }

    /// <summary>
    /// How many pixels trace their screen-space reflection each frame: see <see cref="Rendition.ReflectionResolution"/>.
    /// A change rebuilds the targets before the next frame.
    /// </summary>
    public ReflectionResolution ReflectionResolution
    {
        get => _reflectionResolution;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Reflection resolution can be Half or Full.");
            }

            _reflectionResolution = value;
        }
    }

    /// <summary>
    /// How many pixels work out their ambient occlusion each frame: see <see cref="Rendition.OcclusionResolution"/>.
    /// A change rebuilds the targets before the next frame.
    /// </summary>
    public OcclusionResolution OcclusionResolution
    {
        get => _occlusionResolution;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Occlusion resolution can be Half or Full.");
            }

            _occlusionResolution = value;
        }
    }

    /// <summary>
    /// What anti-aliases the scene: the engine's own TAA, or AMD's FSR 3.1, which also rebuilds a scene drawn
    /// below a <see cref="RenderScale"/> of 1 at the window's size instead of stretching it. FSR needs its library,
    /// amd_fidelityfx_vk.dll, next to the game; without it, or above a render scale of 1, TAA is used, and a missing
    /// library is said once on the error stream. A change takes effect on the next frame and starts the history again.
    /// </summary>
    public Upscaler Upscaler
    {
        get => _upscaler;
        set
        {
            if (value != _upscaler)
            {
                _upscaler = value;
                _upscalerUsed = Upscale.Choose(value, value == Upscaler.Fsr && Fsr.IsAvailable, Console.Error);
            }
        }
    }

    /// <summary>The FSR version in use, such as "FSR 3.1.4", or null while FSR isn't drawing the picture.</summary>
    public string? UpscalerVersion => _upscaling ? _fsrPass.Version : null;

    /// <summary>Returns <paramref name="value"/> if it's a <see cref="RenderScale"/> from 0.5 to 2, and throws if not.</summary>
    public static float CheckRenderScale(float value) => value is >= MinRenderScale and <= MaxRenderScale
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), string.Create(
            CultureInfo.InvariantCulture, $"RenderScale must be from {MinRenderScale} to {MaxRenderScale}, not {value}."));

    /// <summary>
    /// How many pixels across and down the scene is drawn for a window this size at this
    /// <see cref="RenderScale"/>. At 1 it's the window's size. Otherwise each side is rounded to
    /// the nearest even number, so at 0.5 each scene pixel covers exactly two by two window pixels.
    /// A side longer than <paramref name="maxSize"/>, the largest image the device can make, shrinks
    /// the whole scene to fit, keeping its shape.
    /// </summary>
    public static (int Width, int Height) SceneSize(int width, int height, float scale, int maxSize = int.MaxValue)
    {
        if (scale != 1f)
        {
            width = Even(width * (double)scale);
            height = Even(height * (double)scale);
        }

        int longest = Math.Max(width, height);
        if (longest <= maxSize)
        {
            return (width, height);
        }

        // Whole numbers, so the longest side lands on the limit exactly.
        return (EvenBelow((long)width * maxSize / longest), EvenBelow((long)height * maxSize / longest));

        static int Even(double size) => Math.Max((int)Math.Round(size / 2d) * 2, 2);

        static int EvenBelow(long size) => (int)Math.Max(size / 2 * 2, 2);
    }

    /// <summary>Times the render passes. Off by default, because reading the times costs a little.</summary>
    public bool MeasureGpu { get; set; }

    /// <summary>Times from the newest finished frame, while <see cref="MeasureGpu"/> is on. Empty if the GPU can't time passes.</summary>
    public ReadOnlySpan<GpuPassTime> GpuTimes => _gpuTimer.Times;

    /// <summary>
    /// The shaders each pass ran in the newest finished frame, while <see cref="MeasureGpu"/> is on. Empty if the GPU
    /// can't count them. A pass that is switched off runs none.
    /// </summary>
    public ReadOnlySpan<GpuPassWork> GpuWork => _gpuWork is { } counter ? counter.Work : default;

    /// <summary>How long the last frame waited for the GPU to finish earlier work and for the display, while <see cref="MeasureGpu"/> is on.</summary>
    public double WaitMilliseconds { get; private set; }

    /// <summary>
    /// Triangles in the last frame's camera and shadow passes, while <see cref="MeasureGpu"/> is on. The shadows count the
    /// sun's cascades and the point and spot light tiles drawn that frame, not the ones kept from earlier frames.
    /// </summary>
    public (long Camera, long Shadows) DrawnTriangles { get; private set; }

    /// <summary>
    /// How many point and spot light shadow tiles the last frame drew, and how many it kept as earlier frames drew them,
    /// while <see cref="MeasureGpu"/> is on.
    /// </summary>
    public (int Drawn, int Kept) ShadowTiles { get; private set; }

    /// <summary>
    /// The model draw calls the last frame recorded, while <see cref="MeasureGpu"/> is on: one for each batch of
    /// copies of a model part, in the camera, shadow, reflection and probe passes together.
    /// </summary>
    public int DrawCalls { get; private set; }

    /// <summary>
    /// GPU memory the renderer holds: the bytes in use, and the bytes taken from the device, which also counts the
    /// free room in its memory blocks.
    /// </summary>
    public (long Used, long Reserved) GpuMemoryBytes => ((long)_context.Memory.UsedBytes, (long)_context.Memory.ReservedBytes);

    private DrawItem[] _cameraDraws = [];
    // Which draw each camera draw came from, and the placed chunk each camera draw is relit as, -1 for none. Only
    // filled on frames that relight a chunk.
    private int[] _cameraSources = [];
    private int[] _cameraChunks = [];

    private SkySh _skySh = null!;

    // The world-space ball round each draw this frame, for fitting sun shadows to what casts them.
    private Vector4[] _casterBounds = [];
    private readonly DrawItem[][] _shadowDraws = new DrawItem[ShadowPass.MaxCascades][];
    private readonly int[] _shadowCounts = new int[ShadowPass.MaxCascades];
    private readonly Matrix4x4[] _shadowMatrices = new Matrix4x4[ShadowPass.MaxCascades];
    private int _shadowCascades;

    // Point and spot lights: those the view needs, their shadow tiles and what each tile draws.
    private readonly LocalLights _localLights = new();
    private DrawItem[] _tileDraws = [];
    // Which tiles this frame draws, and what the others were last drawn with, so a tile whose light and casters stand
    // still keeps its depths. The world counts changes that can change any tile, such as a mesh or texture going.
    private readonly ShadowPass.TileDraws[] _tileRanges = new ShadowPass.TileDraws[LocalLights.MaxTiles];
    private int _tileCount;
    private readonly ShadowTileCache _tileCache = new();
    private ulong _shadowWorld;
    private bool _atlasKept;
    private bool _clustersNeeded;
    private readonly PipelineLayout _clusterLayout;
    private readonly LivePipeline _clusterPipeline;

    public Renderer(RendererOptions options)
    {
        _context = new VulkanContext(options);
        _api = _context.Api;
        _uploads = new UploadQueue(_context);

        _disableVsync = options.DisableVsync;
        _swapchain = new Swapchain(_context, (uint)options.Width, (uint)options.Height, _disableVsync);
        _renderScale = CheckRenderScale(options.RenderScale);
        // The targets start at the scene's size; the first frame makes FSR's, when it's used, as it learns
        // whether anti-aliasing is on.
        _upscaler = options.Upscaler;
        _upscalerUsed = Upscale.Choose(_upscaler, _upscaler == Upscaler.Fsr && Fsr.IsAvailable, Console.Error);
        _api.GetPhysicalDeviceProperties(_context.PhysicalDevice, out var deviceProperties);
        _maxImageSize = (int)Math.Min(deviceProperties.Limits.MaxImageDimension2D, int.MaxValue);
        _gBuffer = new GBuffer(_context, SceneExtent(_swapchain.Extent));

        _kit = new DescriptorKit(_context);
        for (int i = 0; i < FrameRing.Count; i++)
        {
            _frames[i] = CreateFrame();
        }

        _resources = new ResourceTable(_context);
        _retired = new RetiredResources(_ring, _meshStore, _resources);
        _chunkPool = new ChunkBakePool(_context, _resources, ChunkBakePool.ChunkUploadBudget, _readableBakes.ChunkUnreadable);
        if (_context.CanTrace)
        {
            _tracing = new RayTracing(_context, _resources, MeshFor, _uploads.IsPending,
                releaseAfterSubmitted: structure => _retired.AfterLastSubmitted.FreedOthers.Add(structure),
                releaseAfterRecorded: used => _retired.AfterRecording.FreedOthers.Add(used));
            _traceScene = new TraceScene(TraceFlagsOf);
        }

        _traceDebugPass = _tracing is null ? null : new TraceDebugPass(_context, FrameRing.Count, _kit.FrameSetLayout, _resources.Layout);

        _shadowResolution = ShadowPass.CheckResolution(options.ShadowResolution);
        _shadowPass = new ShadowPass(_context, _kit.FrameSetLayout, _resources, _shadowResolution);
        foreach (ref var frame in _frames.AsSpan())
        {
            WriteAtlasDescriptors(frame);
        }

        _clusterLayout = _context.CreatePipelineLayout([_kit.FrameSetLayout]);
        _clusterPipeline = PipelineFactory.CreateCompute(_context, "cluster.comp", _clusterLayout);

        _environment = new EnvironmentMap(_context);
        SetEnvironment(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.5f),
            Horizon = new Vector3(0.4f),
            Floor = new Vector3(0.2f),
        });
        FlushEnvironment();
        _skySh = new SkySh(_context, _environment.Sky, _environment.Sampler);
        foreach (ref var frame in _frames.AsSpan())
        {
            var skyShInfo = new DescriptorBufferInfo(_skySh.Handle, 0, SkySh.Size);
            var skyShWrite = Descriptors.BufferWrite(frame.Set, 12, &skyShInfo);
            _api.UpdateDescriptorSets(_context.Device, 1, &skyShWrite, 0, null);
        }

        _probes = new ReflectionProbes(_context, _environment.Sampler, FrameRing.Count);
        var captureScene = new CaptureScene(_meshStore.MeshSlots, _meshStore.DetailSlots, _shadowPass);
        _reflectionCapture = new ReflectionCapturePass(_context, _kit.FrameSetLayout, _resources, captureScene,
            new DescriptorImageInfo(_environment.Sampler, _environment.Sky, ImageLayout.ShaderReadOnlyOptimal));
        var probeVolume = new ProbeVolume(_context, _environment.Sampler, _environment.Sky);
        LightProbes = new LightProbes(_context, _environment, _kit,
            new LightProbes.Sources(_resources, _tracing, captureScene, _ring, probeVolume));
        ProbeTests = new ProbeTestHooks(_context, LightProbes, _kit, () => _frames[_uniformFrame].Set, WriteTargetDescriptors);

        _volumetricFog = new VolumetricFogVolume(_context, _kit.FrameSetLayout, _kit.LinearSampler);

        _copier = new HistoryCopier(_api);
        _occlusion = new OcclusionStage(_context, _kit, _copier);
        _lighting = new LightingStage(_context, _kit,
            new LightingStage.Sources(_resources, _environment, _probes, probeVolume, _shadowPass));
        _taaSet = _kit.NewSet(_kit.FourTextureLayout);
        _tonemapSet = _kit.NewSet(_kit.TwoTextureLayout);
        _finishSet = _kit.NewSet(_kit.TwoTextureLayout);
        _overlaySet = _kit.NewSet(_kit.OneTextureLayout);
        _bloomPass = new BloomPass(_context, _kit.LinearSampler, _gBuffer.Resolved.View, _gBuffer.Display);
        _particlePass = new ParticlePass(_context, _kit.FrameSetLayout, FrameRing.Count);
        _decalPass = new DecalPass(_context, _kit.FrameSetLayout, _resources, FrameRing.Count);
        _transparentPass = new TransparentPass(_context, _kit.FrameSetLayout, _resources, _environment, _probes);
        _probeDebugPass = new ProbeDebugPass(_context, _kit.FrameSetLayout, _kit.LightingSetLayout);
        _exposureMeter = new ExposureMeter(_context, _kit.NearestSampler);
        _fsrPass = new FsrPass(_context, _kit.NearestSampler, _kit.FrameSetLayout, _exposureMeter.SetLayout);
        _depthOfFieldPass = new DepthOfFieldPass(_context, _kit.NearestSampler, _kit.LinearSampler, _kit.FrameSetLayout);
        _depthOfFieldPass.SetTargets(DisplaySource, _gBuffer.Depth.View, _gBuffer.Extent);
        WriteTargetDescriptors();

        _gBufferPass = new GBufferPass(_context, _kit.FrameSetLayout, _resources);
        _taaPass = new FullscreenPass(_context, "taa.frag", GBuffer.HdrFormat, [_kit.FrameSetLayout, _kit.FourTextureLayout]);
        _tonemapPass = new FullscreenPass(_context, "tonemap.frag", GBuffer.LdrFormat,
            [_kit.FrameSetLayout, _kit.TwoTextureLayout, _exposureMeter.SetLayout]);
        _finishPass = new FullscreenPass(_context, "finish.frag", _swapchain.Format, [_kit.FrameSetLayout, _kit.TwoTextureLayout], TransitionConstants.Size);
        _overlayPass = new OverlayPass(_context, _swapchain.Format, _kit.OneTextureLayout, _resources, FrameRing.Count);
        // Counting each pass's shaders needs a device feature; without it the frame is only timed.
        _gpuWork = _context.Abilities.PipelineStatistics ? new GpuWorkCounter(_context, FrameRing.Count, GpuTimer.FramePasses) : null;
        _gpuTimer = new GpuTimer(_context, FrameRing.Count, counter: _gpuWork);
        _areaInputs = new AreaInputs(_meshStore.PartBounds, () => EnvironmentHash);
        var spaceBake = new SpaceBake(_readableBakes, data => CreateTexture(data, TextureEdges.Clamp), DestroyTexture);
        _frameBakes = new FrameBakes(_readableBakes, _chunkPool, _retired, _areaInputs, spaceBake);
        BakeJobs = new BakeJobs(_context, new PreparedBakers(_context, _environment, () => new Baker(_context, this)),
            _meshStore, _resources, _areaInputs);
        BackgroundBakes = new BackgroundBakes(_context, _ring, _retired, BakeJobs,
            new BackgroundBakes.BakeWants(_frameBakes.Resolver, _readableBakes, _areaInputs.Lights));
    }

    /// <summary>
    /// Hands a mesh's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be drawn in that same frame.
    /// </summary>
    public MeshHandle CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var traceInput = GpuBuffer.TraceInput(_context);
        var vertexBuffer = GpuBuffer.CreateDeviceLocalStaged(_context, vertices, BufferUsageFlags.VertexBufferBit | traceInput, out var vertexStaging);
        _uploads.Add(vertexStaging, vertexBuffer);
        var indexBuffer = GpuBuffer.CreateDeviceLocalStaged(_context, indices, BufferUsageFlags.IndexBufferBit | traceInput, out var indexStaging);
        _uploads.Add(indexStaging, indexBuffer);
        return _meshStore.AddMesh(new Mesh(vertexBuffer, indexBuffer, (uint)indices.Length)
        {
            CumulativeAreas = LightmapLayout.CumulativeAreas(vertices, indices),
        });
    }

    /// <summary>
    /// The area, in a live mesh's own units, of the triangles in an index range, for <see cref="LightmapLayout.Build"/>.
    /// Read from a running total kept when the mesh was made. Throws for a mesh that isn't live.
    /// </summary>
    internal float AreaOf(MeshHandle handle, uint firstIndex, uint indexCount) =>
        _meshStore.TriangleArea(handle, firstIndex, indexCount);

    /// <summary>The mesh behind a live handle. For tests, which read its buffers' addresses.</summary>
    internal Mesh MeshFor(MeshHandle handle) => _meshStore.LiveMesh(handle);

    /// <summary>The Vulkan context, for tests that make buffers of their own.</summary>
    internal VulkanContext Context => _context;

    /// <summary>The acceleration structures rays are traced through, or null when the GPU can't trace.</summary>
    internal RayTracing? Tracing => _tracing;

    /// <summary>
    /// Whether every probe near the camera, in the two finest cascades and the fixed grid, has been
    /// updated since its light last changed. Screenshots wait for it. False while the probes are switched off and
    /// before their first frame, but true on a GPU that can't run them, as there is nothing to wait for there.
    /// </summary>
    public bool ProbesSettled => LightProbes.Batch.Scheduler?.Settled ?? !LightProbes.Volume.Supported;

    /// <summary>
    /// How many light probes follow the camera, and how many are updated a frame: see
    /// <see cref="Rendition.ProbeQuality"/>. A change makes the probes' atlases again, all uncaptured,
    /// on the next frame with the probes on, which waits for the GPU once.
    /// </summary>
    public ProbeQuality ProbeQuality
    {
        get => LightProbes.Quality;
        set => LightProbes.Quality = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Probe quality can be Low, Medium or High.");
    }

    /// <summary>The bindless textures and materials, which a shader that traces rays reads hits through.</summary>
    internal ResourceTable Resources => _resources;

    /// <summary>How rays treat a material, for <see cref="TraceScene.Collect"/>.</summary>
    internal TraceFlags TraceFlagsOf(MaterialHandle material) =>
        RayTracing.TraceFlagsFor(_resources.HasCutouts(material), _resources.GetFlags(material));

    /// <summary>
    /// Lets go of a mesh and its detail entries. Returns at once; the memory comes back
    /// two frames later, when no frame in flight can read it. Drawing with the handle
    /// after this is a caller mistake that isn't checked.
    /// </summary>
    public void DestroyMesh(MeshHandle mesh)
    {
        var live = _meshStore.TakeMesh(mesh);

        // A mesh made this frame may still be waiting for its bytes. That copy must not
        // run into a buffer that is about to be freed.
        _uploads.Cancel(live.Vertices);
        _uploads.Cancel(live.Indices);
        _retired.AfterLastSubmitted.FreedMeshes.Add(live);

        // A background bake still tracing it would read freed buffers.
        BackgroundBakes.StopIfReads(job => job.Area.Uses(mesh));

        // Its structures read its buffers, and its handle may go to another mesh, so they go with it.
        _tracing?.Forget(mesh);
    }

    /// <summary>
    /// Lets go of a texture. Its slot reads as white once it's released, for whatever takes
    /// the slot next. A material that still names it must not be drawn after this.
    /// </summary>
    public void DestroyTexture(TextureHandle texture)
    {
        if (_resources.IsBuiltIn(texture) || !_resources.IsLive(texture))
        {
            throw new ArgumentException("The texture does not exist.", nameof(texture));
        }

        QueueTextureDestroy(texture.Index);
    }

    /// <summary>Lets go of a texture array, with the same rules as <see cref="DestroyTexture"/>.</summary>
    public void DestroyTextureArray(TextureArrayHandle array)
    {
        if (_resources.IsBuiltIn(array) || !_resources.IsLive(array))
        {
            throw new ArgumentException("The texture array does not exist.", nameof(array));
        }

        _uploads.Cancel(_resources.MarkTextureArrayDying(array.Index));
        _retired.AfterLastSubmitted.FreedTextureArrays.Add(array.Index);
    }

    /// <summary>Lets go of a material slot. Draws that still use it are a caller mistake.</summary>
    public void DestroyMaterial(MaterialHandle material)
    {
        if (!_resources.IsLive(material))
        {
            throw new ArgumentException("The material does not exist.", nameof(material));
        }

        QueueMaterialDestroy(material.Index);
    }

    /// <summary>
    /// Destroys everything <see cref="CreateModel"/> or <see cref="CreateDrawItem"/> made
    /// for these parts: their mesh, their materials and the textures those name. Parts
    /// that share a material, or a material already destroyed, are passed over.
    /// </summary>
    public void DestroyModel(ReadOnlySpan<DrawItem> parts)
    {
        if (parts.IsEmpty)
        {
            return;
        }

        foreach (ref readonly var part in parts)
        {
            if (!_resources.IsLive(part.Material))
            {
                continue;
            }

            foreach (int texture in _resources.TexturesOf(part.Material))
            {
                var handle = new TextureHandle(texture);
                if (texture >= 0 && !_resources.IsBuiltIn(handle) && _resources.IsLive(handle))
                {
                    QueueTextureDestroy(texture);
                }
            }

            QueueMaterialDestroy(part.Material.Index);
        }

        DestroyMesh(parts[0].Mesh);
    }

    /// <summary>Marks a live texture as gone, drops any copy still waiting for it, and queues its release.</summary>
    private void QueueTextureDestroy(int slot)
    {
        _shadowWorld++;
        // A background bake whose materials read it would trace a freed image.
        BackgroundBakes.StopIfReads(job => BakeReadsTexture(job, slot));

        _uploads.Cancel(_resources.MarkTextureDying(slot));
        _retired.AfterLastSubmitted.FreedTextures.Add(slot);
    }

    // Whether a material of one of the job's draws names the texture in this slot.
    private bool BakeReadsTexture(BakeJob job, int slot)
    {
        foreach (ref readonly var draw in job.Area.Draws.AsSpan())
        {
            if (!_resources.IsLive(draw.Material))
            {
                continue;
            }

            foreach (int texture in _resources.TexturesOf(draw.Material))
            {
                if (texture == slot)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void QueueMaterialDestroy(int slot)
    {
        _shadowWorld++;
        // The slot may go to a material with a different opaque flag, so no structure may keep using it, and a
        // background bake tracing those structures can't go on.
        var material = new MaterialHandle(slot);
        BackgroundBakes.StopIfReads(job => job.Area.Uses(material));
        _tracing?.ForgetMaterial(material);
        _resources.MarkMaterialDying(slot);
        _retired.AfterLastSubmitted.FreedMaterials.Add(slot);
    }

    /// <summary>
    /// Uploads a mesh as one draw that the renderer can skip when it's out of view, and
    /// simplify when it's small on screen. Pass <paramref name="simplify"/> false for
    /// meshes whose every vertex matters, like terrain chunks that must meet their
    /// neighbours exactly.
    /// </summary>
    public DrawItem CreateDrawItem(
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, MaterialHandle material, bool simplify = true)
    {
        ModelPart[] parts = [new ModelPart { IndexCount = (uint)indices.Length }];
        return CreateDetailedMesh(vertices, indices, parts, [material], simplify)[0];
    }

    /// <summary>
    /// Replaces what surfaces reflect. Nothing waits: the next frame redraws the environment before anything reads it,
    /// so it can change every frame.
    /// </summary>
    public void SetEnvironment(in EnvironmentDescription environment)
    {
        _environment.SetDescription(environment);
        _environmentPending = true;
        _environmentVersion++;
        _environmentContent = HashEnvironment(environment);
    }

    /// <summary>
    /// Records the frame's share of the environment's redraw: after a change of sky or description, its sharp levels
    /// and a slice of the rough reflections, and after that, while the sky holds still, the slices left. A change
    /// pending at the first frame draws every level, so a sky a game sets before it, and a screenshot of it, show no
    /// default.
    /// </summary>
    private void RecordEnvironment(CommandBuffer cmd)
    {
        if (_environmentPending && _ring.FramesSubmitted == 0)
        {
            _environment.Record(cmd, _bakedSky);
        }
        else
        {
            _environment.RecordFrame(cmd, _bakedSky, _environmentPending);
        }

        _environmentPending = false;
    }

    /// <summary>Redraws the whole of a changed environment now and waits, for the paths that draw outside a frame.</summary>
    private void FlushEnvironment()
    {
        if (_environmentPending || _environment.Unfinished)
        {
            _context.SubmitAndWait(cmd => _environment.Record(cmd, _bakedSky));
            _environmentPending = false;
        }
    }

    /// <summary>
    /// A hash of what a baked space's light comes from besides its draws and lights: the environment's bands and panels
    /// from the last <see cref="SetEnvironment"/>, and the clear sky's turbidity and the environment intensity of the
    /// last frame drawn. It follows what they hold, so setting the same environment again gives the same hash. See
    /// "Is the bake fresh?" in docs/design.md.
    /// </summary>
    public ulong EnvironmentHash => StableHasher.ForThread()
        .Add(_environmentContent)
        .Add(_bakedSky.Turbidity)
        .Add(_bakedSky.Image is { } image ? _skyImages[image.Index].Content : 0UL)
        .Add(_bakedSky.Rotation)
        .Add(_bakedSky.Scale)
        .Add(_environmentIntensity)
        .Finish();

    /// <summary>
    /// Where bakes are looked up, such as a <see cref="LightBakeStore"/> over the game's content. With none, or with
    /// <see cref="RenderEffects.BakedLighting"/> in <see cref="FrameView.Off"/>, every bake counts as missing and every
    /// area is lit live. See "Baked lighting" in docs/rendering.md.
    /// </summary>
    public IBakeStore Bakes
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = NoBakes.Instance;

    /// <summary>
    /// How long a bake that arrives while its area is on screen takes to fade in, in seconds of
    /// <see cref="FrameView.Time"/>. 0 shows it at once. A bake already there on the first frame that names its area
    /// always shows at once. From 0 to 10. See "With a fresh bake" in docs/rendering.md.
    /// </summary>
    public float BakeFadeSeconds
    {
        get => _frameBakes.Resolver.FadeSeconds;
        set => _frameBakes.Resolver.FadeSeconds = value is >= 0f and <= MaxBakeFadeSeconds
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), string.Create(
                CultureInfo.InvariantCulture, $"BakeFadeSeconds must be from 0 to {MaxBakeFadeSeconds}, not {value}."));
    }

    /// <summary>The longest <see cref="BakeFadeSeconds"/> can be.</summary>
    public const float MaxBakeFadeSeconds = 10f;

    /// <summary>
    /// What the last frame's baked areas came to, or null when it named no baked space or chunk. See "Baked lighting"
    /// in docs/rendering.md.
    /// </summary>
    public BakeFrame? LastBakeFrame => _frameBakes.Last;

    private readonly AreaInputs _areaInputs;
    private bool _drawingWithoutBakedLights;

    // Bakes is asked through this, so a bake that can't be read counts as missing until the store reloads.
    private readonly ReadableBakes _readableBakes = new();

    // The chunk bakes on the GPU, shared by every copy of a chunk.
    private readonly ChunkBakePool _chunkPool;

    /// <summary>The chunk bakes being read, uploaded or on the GPU. For tests.</summary>
    internal ChunkBakePool ChunkBakes => _chunkPool;

    // The frame's baked areas and what it draws of them.
    private readonly FrameBakes _frameBakes;

    /// <summary>
    /// Makes the jobs that bake a frame's space or chunks, on the baker it prepares. Tests prepare it and make jobs
    /// through this; <see cref="Bake"/> and <see cref="BakeChunk"/> run a job to the end.
    /// </summary>
    internal BakeJobs BakeJobs { get; }

    /// <summary>The bakes the renderer makes by itself, a slice a frame. See <see cref="AutoBake"/>.</summary>
    internal BackgroundBakes BackgroundBakes { get; }

    /// <summary>
    /// Path traces the view's baked space into a lightmap and returns it as a <c>.lightbake</c> file's bytes, made from
    /// the frame's inputs so a frame drawn like this one finds it fresh. Every static draw in the frame blocks and
    /// bounces light; those the space owns, without wind and not transparent, get a lightmap rectangle. Emissive
    /// surfaces, the sky and Baked lights are baked in full; a Mixed light only after its first bounce, since its direct
    /// light stays real-time, and it gets a shadow mask channel. A Realtime light or one that is moving is left out.
    /// The space's light probes are baked too, by the same rules, into tiles laid out as the live probes' are.
    /// Waits for the GPU, taking seconds at <see cref="BakeQuality.Final"/>. See "Baked lighting" in docs/rendering.md.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The view names no baked space, the space holds more than <see cref="BakeFiles.MaxSpaceProbes"/> probes, some of its
    /// draws don't fit in <see cref="LightmapLayout.MaxPages"/> pages, or the GPU can't trace rays.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settings"/> asks for another texel density than the space's
    /// <see cref="BakedSpace.TexelsPerMetre"/>, so no frame would find the bake fresh.
    /// </exception>
    public byte[] Bake(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings) =>
        BakeTinted(SkyModel.Tinted(view, ref _tintedLights), draws, settings);

    private byte[] BakeTinted(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings)
    {
        var space = BakeJobs.StartSpace(view, draws, settings);
        SettleForBake(view);
        BakeJobs.Bakers.Prepare(probes: true);
        using var job = BakeJobs.SpaceJob(view, draws, settings, space);
        return BakeJobs.RunToEnd(job);
    }

    // Readies the GPU for a bake that waits: nothing in flight, no background bake on the baker's pages, every upload
    // done, and the view's sky drawn.
    private void SettleForBake(in FrameView view)
    {
        _context.WaitIdle();
        BackgroundBakes.DropAll();
        _uploads.Flush();
        BakeSky(view);
        FlushEnvironment();
    }

    /// <summary>
    /// What the renderer bakes by itself, in the background, a slice a frame: each area a frame names whose bake is
    /// missing or stale, once its lights have settled. Null, the default, bakes nothing. Ignored where
    /// <see cref="CanTrace"/> is false. Setting it stops the running bake and forgets what was baked, and prepares
    /// baking, which waits for the GPU the first time, so set it outside a frame. Where the GPU can trace rays but
    /// can't bake, that is reported once and nothing is baked. See "Baking in the background" in docs/rendering.md.
    /// </summary>
    public AutoBake? AutoBake
    {
        get => BackgroundBakes.AutoBake;
        set => BackgroundBakes.AutoBake = value;
    }

    /// <summary>
    /// How the background bake is getting on, or null when none runs and no area waits for one. While areas wait and
    /// none runs yet, its name is empty and its fraction 0. See <see cref="AutoBake"/>.
    /// </summary>
    public BakeProgress? BakeProgress => BackgroundBakes.Progress;

    /// <summary>
    /// Path traces chunk <paramref name="chunk"/> of the frame in its own space and returns its <c>.lightbake</c> bytes,
    /// made from the frame's inputs so a frame placing the chunk like this, or anywhere else, finds it fresh. Only the
    /// static draws the chunk owns are in it, moved into the chunk's space as <see cref="BakeAreas.Relative"/> moves
    /// them, so they alone block and bounce light; those without wind and not transparent share one lightmap page,
    /// <see cref="LightmapLayout.BuildSingle"/>. Its plain light holds its own Baked lights in full, its Mixed lights
    /// after their first bounce, and glowing surfaces, but no sky and no sun; it is only stored when there is such a
    /// light or surface. Its transfer holds how the sky and a sun from any direction light it, so the game can light a
    /// placed copy by its own sky and sun. A chunk has no probes. Waits for the GPU. See "Baked lighting" in
    /// docs/rendering.md and "Chunk transfer" in docs/design.md.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The frame has no chunk <paramref name="chunk"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settings"/> asks for another texel density than the chunk's <see cref="BakedChunk.TexelsPerMetre"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The chunk owns no static draws, some of its draws don't fit in its one page, or the GPU can't trace rays.
    /// </exception>
    public byte[] BakeChunk(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings) =>
        BakeChunkTinted(SkyModel.Tinted(view, ref _tintedLights), draws, chunk, settings);

    private byte[] BakeChunkTinted(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings)
    {
        var resolved = BakeJobs.StartChunk(view, draws, chunk, settings);
        SettleForBake(view);
        BakeJobs.Bakers.Prepare(probes: false);
        using var job = BakeJobs.ChunkJob(view, draws, settings, resolved);
        return BakeJobs.RunToEnd(job);
    }

    private static ulong HashEnvironment(in EnvironmentDescription environment)
    {
        var hasher = new StableHasher().Add(environment.Ceiling).Add(environment.Horizon).Add(environment.Floor);
        hasher.Add(environment.Panels.Length);
        foreach (ref readonly var panel in environment.Panels)
        {
            hasher.Add(panel.Direction).Add(panel.HalfWidth).Add(panel.HalfHeight).Add(panel.Radiance);
        }

        return hasher.Finish();
    }

    /// <summary>
    /// Marks the environment for a redraw with the view's clear sky when it or its sun has changed. The next frame
    /// records it, or <see cref="FlushEnvironment"/> draws it now. A gradient sky gives the same empty
    /// <see cref="SkySun"/> whatever the sun does, so that never redraws.
    /// </summary>
    private void BakeSky(in FrameView view)
    {
        _environmentIntensity = view.EnvironmentIntensity;
        var sky = view.Sky.Image is { } image
            ? SkyModel.Photographed(image, SkyImage(image).Sun, view.Sky.Rotation, view.Sky.Exposure)
            : SkyModel.Sun(view);
        if (sky == _bakedSky)
        {
            return;
        }

        if (sky.Image != _bakedSky.Image)
        {
            // The redraws read the image through descriptor sets that recorded frames may still use.
            _context.WaitIdle();
            _environment.SetImage(sky.Image is { } next ? _skyImages[next.Index].Cube : null);
        }

        _bakedSky = sky;
        _environmentPending = true;
        SkyRebakes++;
    }

    private (GpuTexture Cube, SkyImageSun Sun, ulong Content) SkyImage(SkyImageHandle image)
    {
        if (image.Index < 0 || image.Index >= _skyImages.Count)
        {
            throw new ArgumentException($"Sky image {image.Index} was not made by this renderer.", nameof(image));
        }

        return _skyImages[image.Index];
    }

    /// <summary>
    /// Uploads a photographed sky, cooked from an equirectangular .hdr, for <see cref="AnalyticSky.Image"/>. It waits
    /// for the upload and stays until the renderer is disposed. See "A photographed sky" in docs/rendering.md.
    /// </summary>
    public SkyImageHandle CreateSkyImage(SkyImageData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Faces.Length != 6)
        {
            throw new ArgumentException($"A sky image needs 6 faces, not {data.Faces.Length}.", nameof(data));
        }

        var first = data.Faces[0];
        var content = new System.IO.Hashing.XxHash64();
        foreach (var face in data.Faces)
        {
            if (face.Width != first.Width || face.Height != first.Width || face.Format != TextureFormat.Rgba16f
                || face.Mips.Length != first.Mips.Length)
            {
                throw new ArgumentException(
                    $"Every face of a sky image must be square Rgba16f, {first.Width} wide with {first.Mips.Length} mips.",
                    nameof(data));
            }

            foreach (var mip in face.Mips)
            {
                content.Append(mip);
            }
        }

        var sun = new SkyImageSun(data.SunDirection, data.SunRadiance, data.SunCosine);
        _skyImages.Add((GpuTexture.Cube(_context, data.Faces), sun, content.GetCurrentHashAsUInt64()));
        return new SkyImageHandle(_skyImages.Count - 1);
    }

    /// <summary>How many times <see cref="BakeSky"/> has baked the environment again, for tests.</summary>
    internal int SkyRebakes { get; private set; }

    /// <summary>Projects the sky cube to L1 now and reads it back, coefficient by coefficient, for tests.</summary>
    internal Vector4[] ProjectSky() => _skySh.Project();

    /// <summary>What the last frame's uniforms gave, for tests.</summary>
    internal LastFrame Last { get; } = new();

    /// <summary>
    /// Hands a texture's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be sampled in that same frame. <paramref name="edges"/> says what is
    /// read past the texture's edge, for every material that uses it: an atlas such as a
    /// <see cref="Material.MaskTexture"/> wants <see cref="TextureEdges.Clamp"/>.
    /// </summary>
    public TextureHandle CreateTexture(TextureData data, TextureEdges edges = TextureEdges.Repeat)
    {
        var texture = new GpuTexture(_context, new TextureShape(data.Width, data.Height, data.Kind, (uint)data.Mips.Length, Format: data.Format));
        _uploads.Add(texture.CreateStaging([data]), texture);
        try
        {
            return _resources.AddTexture(texture, edges);
        }
        catch
        {
            // A full table has already let the texture go, so its copy must never ride a frame.
            _uploads.Cancel(texture);
            throw;
        }
    }

    /// <summary>
    /// Stacks same-sized images into one texture that shaders read by layer, uploaded the
    /// same way as <see cref="CreateTexture"/>. Every layer must have the first layer's
    /// size, kind, format and mip count.
    /// </summary>
    public TextureArrayHandle CreateTextureArray(ReadOnlySpan<TextureData> layers)
    {
        if (layers.IsEmpty)
        {
            throw new ArgumentException("A texture array needs at least one layer.", nameof(layers));
        }

        var first = layers[0];
        for (int i = 1; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Width != first.Width || layer.Height != first.Height || layer.Kind != first.Kind || layer.Format != first.Format
                || layer.Mips.Length != first.Mips.Length)
            {
                throw new ArgumentException(
                    $"Layer {i} is {layer.Width}x{layer.Height} {layer.Kind} {layer.Format} with {layer.Mips.Length} mips, " +
                    $"but layer 0 is {first.Width}x{first.Height} {first.Kind} {first.Format} with {first.Mips.Length} mips.",
                    nameof(layers));
            }
        }

        var texture = new GpuTexture(_context, new TextureShape(first.Width, first.Height, first.Kind, (uint)first.Mips.Length, (uint)layers.Length, first.Format) { Array = true });
        _uploads.Add(texture.CreateStaging(layers), texture);
        return _resources.AddTextureArray(texture);
    }

    /// <summary>
    /// Replaces the image that <see cref="FrameView.Overlay"/> quads read from. Only its
    /// alpha is used. Waits for the GPU, so it isn't for every frame.
    /// </summary>
    public void SetOverlayAtlas(TextureData data)
    {
        _context.WaitIdle();
        _overlayAtlas?.Dispose();
        _overlayAtlas = new GpuTexture(_context, data);
        WriteImages(_overlaySet, [new(_kit.LinearSampler, _overlayAtlas.View, ImageLayout.ShaderReadOnlyOptimal)]);
    }

    /// <summary>
    /// Adds a reflection probe, so surfaces in its box reflect what is really there, and puts it in line for its first
    /// capture. Each frame photographs the probe at the front of the line from the frame's own draws and lights, inside
    /// the frame and without waiting for the GPU, so a probe shows from a frame or a few after it's added, and surfaces
    /// reflect the sky behind it until then. Its sun shadow is framed on its box. At most
    /// <see cref="MaxReflectionProbes"/> fit. Room for <see cref="ProbeCubes.FirstCapacity"/> is made at the start, and it
    /// doubles when it runs out, which waits for the GPU once.
    /// </summary>
    public ReflectionProbeHandle AddReflectionProbe(in ReflectionProbe probe)
    {
        if (_probes.Count == MaxReflectionProbes)
        {
            throw new InvalidOperationException($"Only {MaxReflectionProbes} reflection probes are supported.");
        }

        int capacity = _probes.Cubes.Capacity;
        var handle = _probes.Add(probe);
        if (_probes.Cubes.Capacity != capacity)
        {
            // The array grew, so the lighting and glass read the new one.
            WriteTargetDescriptors();
            _transparentPass.WriteProbes(_environment.Sampler, _probes.Cubes.View);
        }

        return handle;
    }

    /// <summary>
    /// Puts a probe <see cref="AddReflectionProbe"/> made in line to be photographed again, placed as
    /// <paramref name="probe"/> says, which can move it or change its box. Surfaces keep reflecting its last picture
    /// until the new one is drawn. A probe already in line keeps its place.
    /// </summary>
    public void UpdateReflectionProbe(ReflectionProbeHandle handle, in ReflectionProbe probe) => _probes.Update(handle, probe);

    /// <summary>Whether a probe is in line for a capture that hasn't been drawn yet.</summary>
    public bool ReflectionProbeWaiting(ReflectionProbeHandle handle) => _probes.Waiting(handle);

    /// <summary>
    /// Forgets one reflection probe from the next frame. The others keep their pictures, and the next
    /// probe added takes its place.
    /// </summary>
    public void RemoveReflectionProbe(ReflectionProbeHandle handle) => _probes.Remove(handle);

    /// <summary>How many reflection probes there are, captured or waiting for their first capture.</summary>
    public int ReflectionProbeCount => _probes.Count;

    /// <summary>How many reflection probe captures the frames have drawn since the renderer was made.</summary>
    public int ReflectionCapturesDrawn { get; private set; }

    /// <summary>
    /// How many times the renderer has stopped until the GPU had nothing left to do, as it must to swap out
    /// something a frame in flight still reads. A frame that does is a hitch, so profiles count them.
    /// </summary>
    public int GpuIdleWaits => _context.Waits.WaitIdleCount;

    /// <summary>Forgets every reflection probe, so surfaces reflect only the sky from the next frame.</summary>
    public void ClearReflectionProbes() => _probes.Clear();

    /// <summary>
    /// A number that changes whenever something that lights every reflection probe's picture changes, so update the
    /// probes when it does. It covers the suns, the sky, whether sun shadows are drawn, the light probes and whether
    /// they have settled at least once since they were made, the bakes loaded and whether one is still fading in
    /// (see <see cref="BakeFadeSeconds"/>), and the switches that change what a capture draws. Moving things or the
    /// camera doesn't change it, and nor do point and spot lights, which reach only the probes near them: see
    /// <see cref="ReflectionLightsHash"/>.
    /// </summary>
    public int ReflectionLightingHash(in FrameView view)
    {
        // Light probes fill in over many frames, and reflections captured before they first settle look wrong. Only the
        // first settle counts: the live ProbesSettled turns false on every cell the camera crosses, and updating every
        // reflection probe then would keep them all busy.
        // A capture lights surfaces with the bake's probes, so a bake loaded, dropped or swapped for another changes
        // what it sees, and so does a chunk bake arriving or going. A bake that arrives on screen is still fading in
        // then, so a capture would keep its half-faded picture; the fade ending changes the hash once more, and the
        // capture after it sees the bake whole, as one made when the level loads with the bake saved does.
        return HashCode.Combine(
            ReflectionLightingHash(view, _environmentVersion), LightProbes.InUse, LightProbes.SettledOnce, _frameBakes.DrawnKey,
            _frameBakes.Fading);
    }

    /// <summary>
    /// The part of <see cref="ReflectionLightingHash(in FrameView)"/> that the view alone decides: what
    /// <see cref="ProbeLightingKey"/> holds for the light probes, which captures light surfaces as updates do, and the
    /// light probe settings and the switches for the point and spot lights. The scene's geometry and the camera aren't
    /// in it: a capture frames the sun's shadow on its own box.
    /// </summary>
    internal static int ReflectionLightingHash(in FrameView view, int environmentVersion) =>
        HashCode.Combine(
            ProbeLightingKey(view, environmentVersion, view.Off),
            view.Off & (RenderEffects.LocalLights | RenderEffects.EnvironmentLight),
            view.LightProbes);

    /// <summary>
    /// A number that changes when a point or spot light that reaches a reflection probe's box appears, goes, or changes
    /// its range, cone or shadow, so update the probe when it does. Its colour, where it is and where it points aren't in
    /// it, so a flickering or swinging light doesn't keep a probe busy: update a probe near a light that moves around
    /// all the time on every frame, or by hand when a light near it dims or moves for good.
    /// </summary>
    public static int ReflectionLightsHash(ReadOnlySpan<LocalLight> lights, Vector3 boxMin, Vector3 boxMax)
    {
        var hash = new HashCode();
        foreach (ref readonly var light in lights)
        {
            var nearest = Vector3.Clamp(light.Position, boxMin, boxMax);
            if (Vector3.Distance(nearest, light.Position) > light.Range)
            {
                continue;
            }

            hash.Add(light.Key);
            hash.Add(light.Range);
            hash.Add(light.InnerAngle);
            hash.Add(light.OuterAngle);
            hash.Add(light.CastsShadow);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Sums up the light that reaches every light probe at once: the suns, the environment and its
    /// intensity, and the switches that change what colour a surface sends on. When it changes, every
    /// probe is updated again. Point and spot lights are followed one by one instead, as each reaches
    /// only the probes near it. <see cref="RenderEffects.WearAndDirt"/> isn't in it, as neither the capture nor the trace draws wear or dirt.
    /// </summary>
    internal static int ProbeLightingKey(in FrameView view, int environmentVersion, RenderEffects off)
    {
        var hash = new HashCode();
        int count = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        for (int i = 0; i < count; i++)
        {
            ref readonly var light = ref view.Lights[i];
            hash.Add(light.Direction);
            hash.Add(light.Radiance);
            hash.Add(light.CastsShadow);
        }

        hash.Add(count);
        hash.Add(environmentVersion);
        hash.Add(view.EnvironmentIntensity);
        hash.Add(view.Sky.Turbidity);
        hash.Add(view.Sky.Image);
        hash.Add(view.Sky.Rotation);
        hash.Add(view.Sky.Exposure);
        hash.Add(view.Shadows.Radius > 0f);
        // Under water the probes see the sun through it, so switching the water off lights them again.
        hash.Add(off & (RenderEffects.SunShadows | RenderEffects.Materials | RenderEffects.LightShadows | RenderEffects.Emissive
            | RenderEffects.Underwater));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Makes the light probes' atlases on the first frame with the probes on, again after a change of
    /// <see cref="ProbeQuality"/>, and frees them when the probes are switched off. Each of those waits
    /// for the GPU, so this runs before the frame's fence, and an ordinary frame changes nothing.
    /// </summary>
    private void ManageProbeStorage()
    {
        bool atlasesChanged;
        if (_off.HasFlag(RenderEffects.LightProbes) || !LightProbes.Volume.Supported)
        {
            atlasesChanged = LightProbes.Release();
            LightProbes.InUse = false;
        }
        else
        {
            atlasesChanged = LightProbes.Ensure();
        }

        if (atlasesChanged)
        {
            WriteTargetDescriptors();
        }
    }

    /// <summary>
    /// Plans this frame's light probe work, before its uniforms are written: moves the cascades with
    /// the camera, tells the scheduler what changed, picks the batch of probes to update and where they
    /// are, and plans the probes' sun shadow map. Returns how many instances the work will draw.
    /// </summary>
    private int PlanProbes(in FrameView view, ReadOnlySpan<DrawItem> draws) =>
        LightProbes.Plan(view, draws, DrawnLocalLights(view),
            new ProbePlanInputs(ProbeLightingKey(view, _environmentVersion, _off), ShadowSun(view), _frameNumber));

    /// <summary>
    /// Takes the reflection probe at the front of the line for this frame's capture, when there is one and reflections
    /// are on, and plans it. Before the uniforms, which carry the capture's sun shadow map. Returns how many instances
    /// the capture will draw.
    /// </summary>
    private int PlanReflectionCapture(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        // A frame that threw between planning its capture and recording it gives the probe back its place.
        if (_reflectionCaptureSlot >= 0)
        {
            _probes.PutBack(new ReflectionProbeHandle(_reflectionCaptureSlot), _reflectionCaptureProbe);
        }

        _reflectionCaptureSlot = -1;
        if (_off.HasFlag(RenderEffects.Reflections) || !_reflectionCapture.Supported
            || !_probes.TakeNext(out int slot, out var probe))
        {
            return 0;
        }

        _reflectionCaptureSlot = slot;
        _reflectionCaptureProbe = probe;
        return _reflectionCapture.Plan(draws, probe, view, ShadowSun(view), _frameNumber);
    }

    /// <summary>
    /// The direction towards the sun that casts this frame's shadow, or null when none does or sun shadows are off.
    /// </summary>
    private Vector3? ShadowSun(in FrameView view)
    {
        if (view.Shadows.Radius <= 0f || _off.HasFlag(RenderEffects.SunShadows))
        {
            return null;
        }

        int lights = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        for (int i = 0; i < lights; i++)
        {
            if (view.Lights[i].CastsShadow)
            {
                return view.Lights[i].Direction;
            }
        }

        return null;
    }

    /// <summary>
    /// Records the reflection probe capture <see cref="PlanReflectionCapture"/> planned, if any, and blurs it into the
    /// probe's cube, after the lights' tiles and the light probes, which the capture reads.
    /// </summary>
    private void RecordReflectionCapture(CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        if (_reflectionCaptureSlot < 0)
        {
            return;
        }

        _reflectionCapture.Record(cmd, frameSet, draws, instances);
        _probes.RecordFilter(cmd, _ring.Index, _reflectionCaptureSlot, _reflectionCaptureProbe, _reflectionCapture.Cube.CubeView);
        _reflectionCaptureSlot = -1;
        ReflectionCapturesDrawn++;
    }

    /// <summary>
    /// Adds a surface shader from compiled SPIR-V. It decides how a material's surface
    /// looks and is lit like any other. See docs/design.md for how to write one. This is
    /// slow, so call it while loading.
    /// </summary>
    /// <param name="spirv">The compiled code.</param>
    /// <param name="name">
    /// The compiled file the code came from, without <c>.spv</c>, such as <c>terrain.frag</c>, when it is one in the
    /// <c>shaders</c> folder next to the program. <see cref="ReloadShaders"/> then reads it again when it changes.
    /// </param>
    public SurfaceShaderHandle CreateSurfaceShader(byte[] spirv, string? name = null)
    {
        const uint spirvMagic = 0x07230203;
        if (spirv.Length < 20 || spirv.Length % sizeof(uint) != 0 || BitConverter.ToUInt32(spirv, 0) != spirvMagic)
        {
            throw new ArgumentException("This is not compiled SPIR-V code.", nameof(spirv));
        }

        return _gBufferPass.AddShader(spirv, name);
    }

    /// <summary>
    /// Builds every pipeline that uses one of these shaders again, from the compiled files in the <c>shaders</c>
    /// folder as they are now, and returns how many it built. Names are the files' without <c>.spv</c>, such as
    /// <c>lighting.frag</c>. Needs <see cref="RendererOptions.LiveShaders"/>, and does nothing without it. It waits
    /// for the GPU, so call it between frames. A shader that changed what it reads or writes, not only how, needs
    /// a restart.
    /// </summary>
    public int ReloadShaders(IReadOnlySet<string> names)
    {
        _shadowWorld++;
        return PipelineFactory.Reload(_context, names);
    }

    public MaterialHandle CreateMaterial(in Material material)
    {
        ValidateMaterial(material);
        return _resources.AddMaterial(material);
    }

    /// <summary>
    /// Changes an existing material, for things that change colour during play. Unlike
    /// <see cref="CreateMaterial"/>, this doesn't use up one of the limited material slots.
    /// </summary>
    public void UpdateMaterial(MaterialHandle handle, in Material material)
    {
        ValidateMaterial(material);
        // A destroyed material's structures are already gone, so only a live one is checked.
        bool traced = _tracing is not null && _resources.IsLive(handle);
        var opaqueBefore = traced ? RayTracing.GeometryFlagsFor(_resources.HasCutouts(handle), _resources.GetFlags(handle)) : default;
        _resources.UpdateMaterial(handle, material);
        if (traced
            && RayTracing.GeometryFlagsFor(_resources.HasCutouts(handle), _resources.GetFlags(handle)) != opaqueBefore)
        {
            // Structures bake each part's opaque flag in, so ones drawn in this material are rebuilt when next traced.
            // A background bake's held top level still points at the old ones, so it stops first.
            BackgroundBakes.StopIfReads(job => job.Area.Uses(handle));
            _tracing!.ForgetMaterial(handle);
        }
    }

    private void ValidateMaterial(in Material material)
    {
        if (material.Problem() is { } problem)
        {
            throw new ArgumentException($"The material's {problem}", nameof(material));
        }

        if (material.Shader is { } shader)
        {
            ValidateShader(material, shader);
        }

        ValidateShaderTextures(material);
    }

    // Throws for a surface shader that doesn't exist, or on a transparent material, which can't use one yet.
    private void ValidateShader(in Material material, SurfaceShaderHandle shader)
    {
        if (shader.Index <= 0 || shader.Index >= _gBufferPass.ShaderCount)
        {
            throw new ArgumentException("The material's shader does not exist.", nameof(material));
        }

        if ((material.Flags & MaterialFlags.Transparent) != 0)
        {
            throw new ArgumentException("Transparent materials can't use a surface shader yet.", nameof(material));
        }
    }

    // Throws for a shader texture or texture array the material names that doesn't exist.
    private void ValidateShaderTextures(in Material material)
    {
        for (int i = 0; i < ShaderTextures.Length; i++)
        {
            if (material.ShaderTextures[i] is { } texture && !_resources.IsLive(texture))
            {
                throw new ArgumentException($"The material's shader texture {i} does not exist.", nameof(material));
            }

            if (material.ShaderArrays[i] is { } array && !_resources.IsLive(array))
            {
                throw new ArgumentException($"The material's shader texture array {i} does not exist.", nameof(material));
            }
        }
    }

    /// <summary>
    /// Uploads a cooked model and returns one draw per part. <paramref name="finish"/>, given each
    /// material's index and what the model holds, can change it before it's made, such as to give it
    /// the surface shader <see cref="ModelData.ShaderOf"/> names. <see cref="DestroyModel"/> frees
    /// every texture a material names, its shader textures too, so give each model textures of its own.
    /// </summary>
    public DrawItem[] CreateModel(ModelData model, Func<int, Material, Material>? finish = null)
    {
        var textures = new TextureHandle[model.Textures.Length];
        for (int i = 0; i < textures.Length; i++)
        {
            textures[i] = CreateTexture(model.Textures[i], model.EdgesOf(i));
        }

        var materials = new MaterialHandle[model.Materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            var material = MaterialOf(model.Materials[i], textures);
            if (model.MaskOf(i) is { } masks)
            {
                material.MaskTexture = textures[masks.Texture];
                material.Wear = masks.Wear;
                material.Dirt = masks.Dirt;
            }

            if (model.OcclusionOf(i) is >= 0 and int occlusion)
            {
                material.OcclusionTexture = textures[occlusion];
            }

            if (model.ToonOf(i) is { } toon)
            {
                material.Toon = new ToonShading { Steps = toon.Steps, Ramp = toon.Ramp >= 0 ? textures[toon.Ramp] : null, Rim = toon.Rim };
            }

            materials[i] = CreateMaterial(finish is null ? material : finish(i, material));
        }

        // A posed skeleton can reach well outside the rest pose, so a skinned part's
        // culling sphere is doubled rather than culled too early at the screen's edge.
        float radiusScale = model.Skeleton is null ? 1f : 2f;
        return CreateDetailedMesh(model.Vertices, model.Indices, model.Parts, materials, radiusScale: radiusScale);
    }

    /// <summary>
    /// The material a cooked model's material makes, with its texture indices turned into <paramref name="textures"/>'
    /// handles and -1 for none. The mask and occlusion textures come separately, from <see cref="ModelData.MaskOf"/>
    /// and <see cref="ModelData.OcclusionOf"/>.
    /// </summary>
    internal static Material MaterialOf(in ModelMaterial source, ReadOnlySpan<TextureHandle> textures)
    {
        static TextureHandle? Texture(int index, ReadOnlySpan<TextureHandle> textures) => index >= 0 ? textures[index] : null;

        return new Material
        {
            BaseColor = source.BaseColor,
            Metallic = source.Metallic,
            Roughness = source.Roughness,
            AlphaCutoff = source.AlphaCutoff,
            BaseColorTexture = Texture(source.BaseColorTexture, textures),
            NormalTexture = Texture(source.NormalTexture, textures),
            SurfaceTexture = Texture(source.SurfaceTexture, textures),
            Emissive = source.Emissive,
            EmissiveTexture = Texture(source.EmissiveTexture, textures),
            Clearcoat = source.Clearcoat,
            HeightTexture = Texture(source.HeightTexture, textures),
            HeightDepth = source.HeightDepth,
            DetailColorTexture = Texture(source.DetailColorTexture, textures),
            DetailNormalTexture = Texture(source.DetailNormalTexture, textures),
            DetailScale = source.DetailScale,
            Flags = source.Flags,
            Ior = source.Ior,
            Subsurface = source.Subsurface,
            Translucency = source.Translucency,
            Wind = source.Wind,
        };
    }

    private DrawItem[] CreateDetailedMesh(
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<ModelPart> parts,
        ReadOnlySpan<MaterialHandle> materials, bool simplify = true, float radiusScale = 1f)
    {
        var allIndices = new List<uint>(indices.ToArray());
        var draws = new DrawItem[parts.Length];
        var details = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            ref readonly var part = ref parts[i];
            var partIndices = indices.Slice((int)part.FirstIndex, (int)part.IndexCount);
            var detail = simplify
                ? MeshDetail.Build(vertices, partIndices, allIndices)
                : MeshDetail.Full(vertices, partIndices);
            details[i] = _meshStore.AddDetail(detail with { Radius = detail.Radius * radiusScale });
            draws[i] = new DrawItem
            {
                FirstIndex = part.FirstIndex,
                IndexCount = part.IndexCount,
                Material = materials[part.Material],
                Transform = Matrix4x4.Identity,
                Detail = new DetailHandle(details[i]),
            };
        }

        var mesh = CreateMesh(vertices, CollectionsMarshal.AsSpan(allIndices));
        _meshStore.LiveMesh(mesh).Details = details;
        foreach (ref var draw in draws.AsSpan())
        {
            draw.Mesh = mesh;
        }

        return draws;
    }

    /// <summary>
    /// The sphere around a part, in the model's units, before its draw's transform. A part without detail is the
    /// point at its origin.
    /// </summary>
    public BakeSphere BoundsOf(DetailHandle detail) => _meshStore.PartBounds(detail);

    public void Render(in FrameView view, ReadOnlySpan<DrawItem> draws) =>
        RenderTinted(SkyModel.Tinted(view, ref _tintedLights), draws);

    private void RenderTinted(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (view.Width <= 0 || view.Height <= 0)
        {
            return; // Minimized: there is nothing to present to.
        }

        var viewOff = DebugViewOff(view);
        KeepTargets(view, viewOff);
        TakeEffects(view, viewOff);
        bool antiAliased = !_off.HasFlag(RenderEffects.AntiAliasing);
        KeepDisplaySource(antiAliased);

        CheckDraws(view, draws);
        if (!_drawingWithoutBakedLights && DrewWithoutBakedLights(view, draws))
        {
            return;
        }

        ManageProbeStorage();

        ref Frame frame = ref _frames[_ring.Index];
        if (!AcquireImage(frame, view, out uint imageIndex))
        {
            return;
        }

        bool timed = ResetFrame(frame);
        var camera = FrameCameraOf(view, antiAliased);
        int cameraCount = WriteFrameData(frame, view, draws, camera);
        long issued = DrawCallCount.Issued;
        var cmd = RecordCommands(frame, view, imageIndex, new FrameDraws(_cameraDraws.AsSpan(0, cameraCount), draws), timed);
        DrawCalls = MeasureGpu ? (int)(DrawCallCount.Issued - issued) : 0;
        var renderFinished = _swapchain.RenderFinished[imageIndex];
        Submit(frame, cmd, renderFinished);
        Present(view, imageIndex, renderFinished);
        _ring.MoveToNextSlot();
    }

    /// <summary>
    /// Takes the view's debug view, and throws when it is the trace view on a GPU that can't trace. Returns the effects
    /// the view leaves out: the trace debug view shows colours as they are, so nothing that blends, blurs or brightens
    /// them runs.
    /// </summary>
    private RenderEffects DebugViewOff(in FrameView view)
    {
        bool traceView = view.DebugView == DebugView.Trace;
        if (traceView && _tracing is null)
        {
            throw new InvalidOperationException(
                $"The trace debug view needs a GPU that can trace rays, and this one can't ({_context.Abilities.NoTraceReason}).");
        }

        _traceView = traceView;
        _probeView = view.DebugView == DebugView.Probes;
        return _traceView ? view.Off | TraceViewOff : view.Off;
    }

    /// <summary>
    /// Makes the swapchain, the targets and the shadow maps again when the window, the render scale, the upscaler or a
    /// resolution changed since the last frame.
    /// </summary>
    private void KeepTargets(in FrameView view, RenderEffects viewOff)
    {
        // FSR draws the picture at the window's size, so starting or stopping it changes the targets.
        bool upscaling = _upscalerUsed == Upscaler.Fsr && _renderScale <= 1f && !viewOff.HasFlag(RenderEffects.AntiAliasing);
        bool upscalingChanged = upscaling != _upscaling;
        _upscaling = upscaling;
        if (_vsyncChanged || (uint)view.Width != _swapchain.Extent.Width || (uint)view.Height != _swapchain.Extent.Height)
        {
            Resize((uint)view.Width, (uint)view.Height);
        }
        else if (upscalingChanged || TargetsChanged())
        {
            // Frames in flight still read the targets being replaced. Changing the scale, the upscaler or the
            // resolution of the reflections or the occlusion is rare.
            _context.WaitIdle();
            RebuildTargets();
        }

        if (_shadowResolution != _shadowPass.Resolution)
        {
            // Frames in flight still read the maps being replaced. Changing this is rare.
            _context.WaitIdle();
            _shadowPass.Resize(_shadowResolution);
            WriteTargetDescriptors();
        }
    }

    /// <summary>
    /// Checks the decals, the static draws and the baked chunks before anything is recorded, so a frame that can't be
    /// drawn throws with the frame untouched.
    /// </summary>
    private void CheckDraws(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        PrepareDecals(view);
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
    }

    // Whether the scene's size, or the resolution of the reflections or the occlusion, is no longer the targets'.
    private bool TargetsChanged()
    {
        var scene = SceneExtent(_swapchain.Extent);
        return _reflectionResolution != _gBuffer.Reflections || _occlusionResolution != _gBuffer.Occlusion
            || scene.Width != _gBuffer.Extent.Width || scene.Height != _gBuffer.Extent.Height;
    }

    /// <summary>
    /// Takes the effects the frame leaves out, and what follows from them: the wind's reach, the fade rules, the
    /// volumetric fog's targets and depth of field.
    /// </summary>
    private void TakeEffects(in FrameView view, RenderEffects viewOff)
    {
        // Metering is left out of the shaders unless the view asked for it, so the fixed exposure stays as it was.
        _off = Exposure.ShaderOff(view.Post, viewOff & RenderEffects.All);
        _windReach = new WindReach(Wind.Strength(view.Wind, _off), _resources.SwayOf);
        _fadeRules = new FadeRules(view.CameraPosition, !_off.HasFlag(RenderEffects.Fades), _resources.FadeOf);
        KeepVolumetricTargets(view);
        _depthOfField = DepthOfFieldConstants.For(view.Post, _off, (int)_gBuffer.Extent.Width);
    }

    /// <summary>Points bloom, tonemapping, the meter and depth of field at the anti-aliased image or the lit one.</summary>
    private void KeepDisplaySource(bool antiAliased)
    {
        if (antiAliased == _readsAntiAliased)
        {
            return;
        }

        // Frames in flight still read the sets being rewritten. Changing this is rare.
        _context.WaitIdle();
        _readsAntiAliased = antiAliased;
        _bloomPass.SetSource(DisplaySource);
        WriteImages(_tonemapSet,
        [
            new(_kit.NearestSampler, DisplaySource, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, _bloomPass.Result, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        _exposureMeter.SetSource(DisplaySource, _gBuffer.Display);
        _depthOfFieldPass.SetSource(DisplaySource, _gBuffer.Depth.View);
    }

    /// <summary>
    /// Takes the finished bakes, works out the frame's baked areas and moves the background bake on. True when a fresh
    /// bake has taken some lights over, and the frame has been drawn again without them, its sun tinted already.
    /// </summary>
    private bool DrewWithoutBakedLights(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        // Before the bakes are resolved, so a file Done saves can be fresh this very frame.
        BackgroundBakes.TakeFinished();

        // Worked out once, from the original view: a frame drawn again without its baked sun keeps the sky that sun
        // lights, and its disc.
        BakeSky(view);
        bool relit = _frameBakes.TryResolve(view, draws, Bakes, out var withoutBakedLights);

        // From the frame with every light, since a bake takes the lights a fresh bake would drop.
        BackgroundBakes.Update(view, draws, LastBakeFrame);
        if (!relit)
        {
            return false;
        }

        _drawingWithoutBakedLights = true;
        try
        {
            RenderTinted(withoutBakedLights, draws);
        }
        finally
        {
            _drawingWithoutBakedLights = false;
        }

        return true;
    }

    /// <summary>
    /// Waits until the GPU is done with the frame slot, frees what waited on it, and takes the next swapchain image.
    /// False when the swapchain is out of date, after making a new one: then the frame isn't drawn.
    /// </summary>
    private bool AcquireImage(in Frame frame, in FrameView view, out uint imageIndex)
    {
        var fence = frame.InFlight;
        long waitStart = Stopwatch.GetTimestamp();
        _api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue).Check();
        _retired.Release(_ring.Index);
        BackgroundBakes.Slicer.Timer.Measure(_ring.Index, BackgroundBakes.AutoBake);

        uint acquiredIndex = 0;
        var acquired = _context.SwapchainApi.AcquireNextImage(
            _context.Device, _swapchain.Handle, ulong.MaxValue, frame.ImageAvailable, default, &acquiredIndex);
        imageIndex = acquiredIndex;
        if (acquired == Result.ErrorOutOfDateKhr)
        {
            Resize((uint)view.Width, (uint)view.Height);
            return false;
        }

        if (acquired != Result.SuboptimalKhr)
        {
            acquired.Check();
        }

        if (MeasureGpu)
        {
            WaitMilliseconds = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        }

        return true;
    }

    /// <summary>
    /// Reads the GPU timer's last times for the frame slot, when the GPU is measured, and resets its fence. Returns
    /// whether this frame is timed.
    /// </summary>
    private bool ResetFrame(in Frame frame)
    {
        bool timed = MeasureGpu && _gpuTimer.IsSupported;
        if (timed)
        {
            _gpuTimer.Collect(_ring.Index);
        }

        var fence = frame.InFlight;
        _api.ResetFences(_context.Device, 1, &fence).Check();
        return timed;
    }

    /// <summary>
    /// The frame's camera: its view and projection, last frame's, the shift of the scene's pixels and whether last
    /// frame is known at all.
    /// </summary>
    private readonly record struct FrameCamera(Matrix4x4 ViewProjection, Matrix4x4 Previous, Vector2 Shift, bool LastFrameKnown);

    /// <summary>
    /// Works out the frame's camera, with the shift of less than a pixel, different every frame, that lets
    /// anti-aliasing gather detail from between the pixels, and FSR's frame while it upscales.
    /// </summary>
    private FrameCamera FrameCameraOf(in FrameView view, bool antiAliased)
    {
        // Without anti-aliasing to gather it, the shift would only make the picture shake.
        // The pixels are the scene's, which the render scale may make smaller or larger than the window's.
        var viewProjection = view.View * view.Projection;
        // FSR takes as many shifts as it needs to land on every window pixel, more the smaller the scene.
        var scenePixels = new Vector2(_gBuffer.Extent.Width, _gBuffer.Extent.Height);
        int jitterPhases = _upscaling ? Upscale.JitterPhaseCount((float)_gBuffer.Extent.Width / _gBuffer.Display.Width) : JitterSteps;
        var jitter = Upscale.Jitter(_frameNumber, jitterPhases);
        var shift = antiAliased ? jitter * 2f / scenePixels : Vector2.Zero;
        // The occlusion keeps a history of its own, which follows the camera and the draws while the picture has none.
        bool lastFrameKnown = _gBuffer.HistoryReady || _gBuffer.OcclusionHistoryReady;
        var previous = lastFrameKnown ? _previousViewProjection : viewProjection;
        if (_upscaling)
        {
            float milliseconds = (view.Time - _previousTime) * 1000f;
            _fsrFrame = new FsrFrame(
                jitter,
                _gBuffer.HistoryReady && milliseconds > 0f ? milliseconds : 1000f / 60f,
                Reset: !_gBuffer.HistoryReady,
                CameraNear(view.Projection),
                VerticalFov(view.Projection));
        }

        return new FrameCamera(viewProjection, previous, shift, lastFrameKnown);
    }

    /// <summary>
    /// Plans the probes, writes the frame's uniforms, bones and chunks, and picks what each view draws. Returns how
    /// many draws the camera has, at the head of <see cref="_cameraDraws"/>.
    /// </summary>
    private int WriteFrameData(in Frame frame, in FrameView view, ReadOnlySpan<DrawItem> draws, in FrameCamera camera)
    {
        // Before the uniforms, which carry the probes' sun shadow maps and whether surfaces read the probes.
        int probeInstances = PlanProbes(view, draws) + PlanReflectionCapture(view, draws);

        if (_casterBounds.Length < draws.Length)
        {
            _casterBounds = new Vector4[draws.Length * 2];
        }

        int casters = DrawSelection.Bounds(draws, _meshStore.DetailSlots, _casterBounds, _windReach);
        bool castsShadow = WriteUniforms(frame, view, camera.Previous, camera.Shift, _casterBounds.AsSpan(0, casters));
        _uniformFrame = _ring.Index;
        WriteBones(frame, view.Bones);
        _motion.Begin(draws, view.Bones, camera.LastFrameKnown,
            new Span<Matrix4x4>((Matrix4x4*)frame.Bones.Mapped + view.Bones.Length, 2 * MaxBones - view.Bones.Length));
        _previousViewProjection = camera.ViewProjection;
        _previousTime = view.Time;
        _frameNumber++;
        _transition = TransitionConstants.From(view.Transition);
        _transitionHold = view.Transition.Hold;
        var (cameraCount, shadowCount) = SelectDraws(view, camera.ViewProjection, draws, castsShadow);
        int tileDrawCount = SelectTileDraws(draws);
        if (MeasureGpu)
        {
            DrawnTriangles = (DrawnTriangles.Camera, DrawnTriangles.Shadows + CountTriangles(_tileDraws.AsSpan(0, tileDrawCount)));
        }

        ResetInstances(frame, cameraCount + shadowCount + tileDrawCount + probeInstances);
        _frameBakes.Relight.WriteTo(frame.Chunks, _frameBakes.MaskSlots.SpaceSlots);

        return cameraCount;
    }

    /// <summary>
    /// What a frame draws: the camera's picked draws, the whole scene's, which probes and rays take, and the scene's
    /// top-level structure on a frame that traces rays.
    /// </summary>
    private readonly ref struct FrameDraws(ReadOnlySpan<DrawItem> camera, ReadOnlySpan<DrawItem> scene, TopLevelStructure? traced = null)
    {
        public ReadOnlySpan<DrawItem> Camera { get; } = camera;

        public ReadOnlySpan<DrawItem> Scene { get; } = scene;

        public TopLevelStructure? Traced { get; } = traced;
    }

    /// <summary>
    /// Begins the frame's command buffer with the uploads, this frame's share of the chunk bakes going up, and a slice
    /// of the background bake.
    /// </summary>
    private CommandBuffer BeginCommands(in Frame frame)
    {
        _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
        var cmd = frame.CommandBuffer;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _api.BeginCommandBuffer(cmd, &beginInfo).Check();
        var retiring = _retired.AfterRecording;
        _uploads.Record(cmd, retiring.FreedOthers);
        // The chunk bakes no copy named this frame are let go once it is done.
        _chunkPool.Record(cmd, retiring.FreedTextures, retiring.FreedOthers);
        // After the uploads, so a mesh made this frame is in the bake's scene.
        BackgroundBakes.Slicer.RecordSlice(cmd);
        return cmd;
    }

    /// <summary>Records the frame's command buffer: the uploads, a bake slice, the sky, and then the frame itself.</summary>
    private CommandBuffer RecordCommands(in Frame frame, in FrameView view, uint imageIndex, in FrameDraws draws, bool timed)
    {
        var cmd = BeginCommands(frame);
        // One top-level structure a frame, shared by the trace debug view and the traced probe updates, made only on
        // a frame that traces something.
        TopLevelStructure? traced = null;
        if (_traceView || LightProbes.TracesBatch)
        {
            // After the uploads, so a mesh made this frame is traced this frame. From here until the submit below,
            // nothing may destroy or change a material: see RayTracing.TopLevel.
            traced = _tracing!.TopLevel(cmd, _traceScene!.Collect(draws.Scene));
        }

        int overlayQuads = _overlayAtlas is null ? 0 : _overlayPass.Upload(_ring.Index, view.Overlay);
        UploadParticles(view);

        _decalPass.Upload(_ring.Index);
        if (timed)
        {
            _gpuTimer.Begin(cmd, _ring.Index);
        }

        // A changed sky, redrawn before the frame's passes read it. It waits on the GPU for earlier reads itself, so the
        // bake slice above traces last frame's sky, a frame behind, which the timer leaves out of the "Sky" pass.
        RecordEnvironment(cmd);
        _gpuTimer.Mark(cmd);

        // Worked out here, just before the frame is recorded, so a frame that is never drawn doesn't move the clock.
        _exposureStep = _exposureClock.Next(
            !_off.HasFlag(RenderEffects.AutoExposure), view.Time, Exposure.Speed(view.Post.AdaptationSpeed));
        RecordFrame(cmd, frame.Set, imageIndex, new FrameDraws(draws.Camera, draws.Scene, traced), overlayQuads);
        _gpuTimer.End(cmd);
        _api.EndCommandBuffer(cmd).Check();
        return cmd;
    }

    /// <summary>
    /// Submits the frame's commands, once the swapchain image is there, signalling <paramref name="renderFinished"/>
    /// and the frame's fence.
    /// </summary>
    private void Submit(in Frame frame, CommandBuffer cmd, Semaphore renderFinished)
    {
        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = frame.ImageAvailable,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        // Presenting waits for every command, so it comes after the last change of the image's layout
        // too, which the frame's colour output alone doesn't cover, and after the copy of a capture.
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = renderFinished,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var commandInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = cmd,
        };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = 1,
            PWaitSemaphoreInfos = &waitInfo,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signalInfo,
        };
        _api.QueueSubmit2(_context.Queue, 1, &submit, frame.InFlight).Check();
        _ring.Submitted();
    }

    /// <summary>Shows the image once the frame is drawn, and makes a new swapchain when the window has changed.</summary>
    private void Present(in FrameView view, uint imageIndex, Semaphore renderFinished)
    {
        var swapchainHandle = _swapchain.Handle;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &swapchainHandle,
            PImageIndices = &imageIndex,
        };
        var presented = _context.SwapchainApi.QueuePresent(_context.Queue, &present);
        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            Resize((uint)view.Width, (uint)view.Height);
        }
        else
        {
            presented.Check();
        }
    }

    /// <summary>
    /// How many pixels a simpler model may stray by in a view that allows <paramref name="error"/> at a mesh detail of 1,
    /// or 0, full detail, with <see cref="RenderEffects.SimplerModels"/> off.
    /// </summary>
    private float DetailError(float error) => _off.HasFlag(RenderEffects.SimplerModels) ? 0f : error / MeshDetailScale;

    /// <summary>Whether views leave out what they can't see, which <see cref="RenderEffects.Culling"/> off stops.</summary>
    private bool Culls => !_off.HasFlag(RenderEffects.Culling);

    /// <summary>Leaves out what each pass can't see, and swaps in simpler models where nobody will notice.</summary>
    private (int Camera, int Shadows) SelectDraws(
        in FrameView view, in Matrix4x4 viewProjection, ReadOnlySpan<DrawItem> draws, bool castsShadow)
    {
        // A draw near swapping to a finer version is drawn twice, cross-fading, so every view has room for two of each.
        if (_cameraDraws.Length < draws.Length * 2)
        {
            _cameraDraws = new DrawItem[draws.Length * 2];
            for (int i = 0; i < _shadowDraws.Length; i++)
            {
                _shadowDraws[i] = new DrawItem[draws.Length * 2];
            }
        }

        var (pixelsAtOneUnit, pixelsPerUnit) = Projection.PixelScale(view.Projection, (int)_gBuffer.Extent.Height);
        var camera = new DetailView(viewProjection, view.CameraPosition, pixelsAtOneUnit, pixelsPerUnit, DetailError(CameraDetailError), cull: Culls);
        _cameraView = camera;
        int cameraCount = SelectCameraDraws(camera, draws);
        int shadowCount = 0;
        Array.Clear(_shadowCounts);
        if (castsShadow)
        {
            shadowCount = SelectShadowDraws(view, draws);
        }

        if (MeasureGpu)
        {
            CountDrawnTriangles(cameraCount);
        }

        return (cameraCount, shadowCount);
    }

    /// <summary>
    /// Picks the camera's draws. While chunks are relit, each picked draw also keeps the chunk its source is relit as.
    /// </summary>
    private int SelectCameraDraws(in DetailView camera, ReadOnlySpan<DrawItem> draws)
    {
        if (!_frameBakes.Relight.Active)
        {
            return DrawSelection.Select(camera, draws, _meshStore.DetailSlots, _cameraDraws, _windReach, fades: _fadeRules);
        }

        if (_cameraSources.Length < _cameraDraws.Length)
        {
            _cameraSources = new int[_cameraDraws.Length];
            _cameraChunks = new int[_cameraDraws.Length];
        }

        int cameraCount = DrawSelection.Select(camera, draws, _meshStore.DetailSlots, _cameraDraws, _windReach, _cameraSources, _fadeRules);
        for (int i = 0; i < cameraCount; i++)
        {
            _cameraChunks[i] = _frameBakes.Relight.DrawChunks[_cameraSources[i]];
        }

        return cameraCount;
    }

    // Picks each shadow cascade's draws, and returns how many there are in all.
    private int SelectShadowDraws(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        int shadowCount = 0;
        for (int cascade = 0; cascade < _shadowCascades; cascade++)
        {
            float texelsPerUnit = _shadowPass.Resolution / (2f * ShadowPass.CascadeRadius(view.Shadows, cascade));
            var shadow = new DetailView(
                _shadowMatrices[cascade], view.CameraPosition, 0f, texelsPerUnit, DetailError(ShadowDetailError), cull: Culls);
            _shadowCounts[cascade] = DrawSelection.Select(shadow, draws, _meshStore.DetailSlots, _shadowDraws[cascade], _windReach, fades: _fadeRules);
            shadowCount += _shadowCounts[cascade];
        }

        return shadowCount;
    }

    // Counts the triangles the camera and the shadow cascades draw, for DrawnTriangles.
    private void CountDrawnTriangles(int cameraCount)
    {
        long shadowTriangles = 0;
        for (int cascade = 0; cascade < _shadowCascades; cascade++)
        {
            shadowTriangles += CountTriangles(_shadowDraws[cascade].AsSpan(0, _shadowCounts[cascade]));
        }

        DrawnTriangles = (CountTriangles(_cameraDraws.AsSpan(0, cameraCount)), shadowTriangles);
    }

    /// <summary>
    /// Checks and packs the view's decals, or none when they're switched off. Throws for a decal
    /// that can't be drawn: too many, a box that can't be inverted, a weight outside 0 to 1 or a material that doesn't exist.
    /// </summary>
    private void PrepareDecals(in FrameView view)
    {
        ReadOnlySpan<DecalDraw> decals = _off.HasFlag(RenderEffects.Decals) ? [] : view.Decals;
        for (int i = 0; i < decals.Length; i++)
        {
            if (!_resources.IsLive(decals[i].Material))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Decal {i}'s material does not exist."), nameof(view));
            }
        }

        _decalPass.Prepare(decals);
    }

    /// <summary>
    /// Sorts this frame's particles into their buffer and keeps the camera's axes, which
    /// turn each one to face the screen. The view matrix holds them in its columns.
    /// </summary>
    private void UploadParticles(in FrameView view)
    {
        ref readonly var m = ref view.View;
        var right = new Vector3(m.M11, m.M21, m.M31);
        var up = new Vector3(m.M12, m.M22, m.M32);
        var forward = -new Vector3(m.M13, m.M23, m.M33);
        _particleCamera = new ParticlePass.CameraConstants
        {
            Right = new Vector4(right, 0f),
            Up = new Vector4(up, 0f),
            Forward = new Vector4(forward, 0f),
        };
        ReadOnlySpan<Particle> particles = _off.HasFlag(RenderEffects.Particles) ? [] : view.Particles;
        _particleCount = _particlePass.Upload(_ring.Index, particles, view.CameraPosition, forward);
        _transparentView = TransparentView.From(view.View, view.Projection);
    }

    /// <summary>
    /// Picks what each shadow tile of the atlas can see, one after another in
    /// <see cref="_tileDraws"/>, and returns how many there are in all. A tile the atlas still holds as it would be
    /// drawn is left out, with no draws.
    /// </summary>
    private int SelectTileDraws(ReadOnlySpan<DrawItem> draws)
    {
        var tiles = _localLights.Tiles;
        int total = 0;
        int drawn = 0;
        _tileCache.Begin(_shadowWorld);
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            if (_tileDraws.Length < total + draws.Length * 2)
            {
                Array.Resize(ref _tileDraws, Math.Max(_tileDraws.Length * 2, total + draws.Length * 2));
            }

            ref readonly var view = ref tiles[tile];
            var shadow = new DetailView(
                view.Matrix, view.Eye, view.PixelsAtOneUnit, 0f, DetailError(ShadowDetailError), far: true, cull: Culls);
            int count = DrawSelection.Select(shadow, draws, _meshStore.DetailSlots, _tileDraws.AsSpan(total), _windReach, fades: _fadeRules);
            _tileRanges[tile] = (total, count);
            total += count;
        }

        _tileCache.End();
        ShadowTiles = MeasureGpu ? (drawn, tiles.Length - drawn) : default;
        return total;
    }

    private static long CountTriangles(ReadOnlySpan<DrawItem> draws)
    {
        long indices = 0;
        foreach (ref readonly var draw in draws)
        {
            indices += draw.IndexCount;
        }

        return indices / 3;
    }

    public byte[] Capture(in FrameView view, ReadOnlySpan<DrawItem> draws, out int width, out int height)
    {
        if ((uint)view.Width != _swapchain.Extent.Width || (uint)view.Height != _swapchain.Extent.Height)
        {
            Resize((uint)view.Width, (uint)view.Height);
        }

        width = (int)_swapchain.Extent.Width;
        height = (int)_swapchain.Extent.Height;
        int size = width * height * 4;
        using var capture = GpuBuffer.CreateHostVisible(_context, (ulong)size, BufferUsageFlags.TransferDstBit);

        _capture = capture;
        _captureRecorded = false;
        try
        {
            Render(view, draws);
        }
        finally
        {
            // A frame that throws, such as for a draw it can't make, must not leave the next frame copying into the
            // buffer the using frees.
            _capture = null;
        }

        _context.WaitIdle();

        if (!_captureRecorded)
        {
            throw new InvalidOperationException("The window changed while capturing, so no frame was rendered.");
        }

        var pixels = new ReadOnlySpan<byte>(capture.Mapped, size).ToArray();
        for (int i = 0; i < size; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); // The swapchain stores BGRA.
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    public void Dispose()
    {
        _context.WaitIdle();
        _uploads.Discard();
        BackgroundBakes.DropAll();
        for (int slot = 0; slot < FrameRing.Count; slot++)
        {
            _retired.Release(slot);
        }

        // Its pages already in the material table go with the table.
        _chunkPool.Dispose();
        DisposePasses();
        _resources.Dispose();
        _meshStore.Dispose();

        foreach (ref var frame in _frames.AsSpan())
        {
            frame.Dispose(_api, _context.Device);
        }

        _kit.Dispose();
        _gBuffer.Dispose();
        _swapchain.Dispose();
        _context.Dispose();
    }

    // Frees the passes and the timers, then the probes and the sky.
    private void DisposePasses()
    {
        _traceDebugPass?.Dispose();
        BakeJobs.Bakers.Dispose();
        _tracing?.Dispose();
        _gpuTimer.Dispose();
        BackgroundBakes.Dispose();
        _overlayPass.Dispose();
        _overlayAtlas?.Dispose();
        _finishPass.Dispose();
        _tonemapPass.Dispose();
        _fsrPass.Dispose();
        _exposureMeter.Dispose();
        _depthOfFieldPass.Dispose();
        _bloomPass.Dispose();
        _taaPass.Dispose();
        _transparentPass.Dispose();
        _probeDebugPass.Dispose();
        _particlePass.Dispose();
        _decalPass.Dispose();
        _lighting.Dispose();
        _occlusion.Dispose();
        _gBufferPass.Dispose();
        _shadowPass.Dispose();
        _clusterPipeline.Destroy();
        _api.DestroyPipelineLayout(_context.Device, _clusterLayout, null);
        DisposeProbesAndSky();
    }

    private void DisposeProbesAndSky()
    {
        _probes.Dispose();
        LightProbes.Dispose();
        _volumetricFog.Dispose();
        _reflectionCapture.Dispose();
        _skySh.Dispose();
        _environment.Dispose();
        foreach (var image in _skyImages)
        {
            image.Cube.Dispose();
        }
    }

    private void RecordFrame(CommandBuffer cmd, DescriptorSet frameSet, uint imageIndex, in FrameDraws draws, int overlayQuads)
    {
        var targets = _gBuffer;

        // Ldr still holds the last frame, so it's kept before this frame draws over it.
        if (_transitionHold != _heldNumber || !targets.HeldReady)
        {
            RecordHold(cmd, targets, copyLastFrame: _transitionHold != _heldNumber && targets.LdrReady);
            _heldNumber = _transitionHold;
        }

        RecordScene(cmd, frameSet, draws);
        RecordResolve(cmd, frameSet, targets);
        _gpuTimer.Mark(cmd);
        RecordPost(cmd, frameSet, targets);
        RecordFinish(cmd, frameSet, imageIndex, overlayQuads);
    }

    /// <summary>
    /// Anti-aliases or upscales the lit image, and keeps the frame for the next one. Anti-aliasing blends in the last
    /// frame, and reflections and the screen bounce read it, so it's kept for the next while any wants it. Without
    /// anti-aliasing the lit image is the frame.
    /// </summary>
    private void RecordResolve(CommandBuffer cmd, DescriptorSet frameSet, GBuffer targets)
    {
        // FSR keeps its own history, at the window's size, so reflections get the lit image. It's kept every frame,
        // so the last frame's camera and motion stay known to FSR whether reflections are on or not.
        if (_upscaling)
        {
            RecordHistoryCopy(cmd, targets.Hdr.Image, drawnJustNow: false);
            _fsrPass.Record(cmd, frameSet, _exposureMeter.Set, targets.Hdr.Image, targets.Depth.Image, targets.Resolved.Image, _fsrFrame);
        }
        else if (_readsAntiAliased)
        {
            ReadOnlySpan<DescriptorSet> taaSets = [frameSet, _taaSet];
            _api.BeginColorTarget(cmd, targets.Resolved.Image);
            _taaPass.Record(cmd, targets.Resolved.View, targets.Extent, taaSets);
            RecordHistoryCopy(cmd, targets.Resolved.Image, drawnJustNow: true);
        }
        else if (!_off.HasFlag(RenderEffects.Reflections)
            || (_off & (RenderEffects.AmbientOcclusion | RenderEffects.ScreenBounce)) == 0)
        {
            RecordHistoryCopy(cmd, targets.Hdr.Image, drawnJustNow: false);
        }
        else
        {
            targets.HistoryReady = false;
        }
    }

    /// <summary>Blurs what is out of focus, adds bloom, meters the exposure and tonemaps the picture into Ldr.</summary>
    private void RecordPost(CommandBuffer cmd, DescriptorSet frameSet, GBuffer targets)
    {
        // The picture's size from anti-aliasing on: the scene's, or the window's once FSR has rebuilt it.
        var extent = targets.Display;

        // After the history is kept, so anti-aliasing and reflections next frame see the scene sharp. Bloom
        // and the meter read the blurred picture. Probe captures never come here, so they stay sharp.
        if (_depthOfField is { } lens)
        {
            var source = _readsAntiAliased ? targets.Resolved : targets.Hdr;
            _depthOfFieldPass.Record(cmd, frameSet, source.Image, source.View, extent, lens);
        }

        _gpuTimer.Mark(cmd);

        if (_off.HasFlag(RenderEffects.Bloom))
        {
            _bloomPass.RecordSkipped(cmd);
        }
        else
        {
            _bloomPass.Record(cmd);
        }

        _gpuTimer.Mark(cmd);

        // Metered from the lit frame before bloom. Its time counts towards the tonemap's.
        if (_exposureStep is { } exposureStep)
        {
            _exposureMeter.Record(cmd, exposureStep);
        }

        ReadOnlySpan<DescriptorSet> tonemapSets = [frameSet, _tonemapSet, _exposureMeter.Set];
        _api.BeginColorTarget(cmd, targets.Ldr.Image);
        _tonemapPass.Record(cmd, targets.Ldr.View, extent, tonemapSets);
        _api.EndColorTarget(cmd, targets.Ldr.Image);
        _gpuTimer.Mark(cmd);
    }

    /// <summary>
    /// Draws the picture into the swapchain image at the window's size, with the transition and the overlay over it,
    /// and leaves the image to present, or copies it out for a capture.
    /// </summary>
    private void RecordFinish(CommandBuffer cmd, DescriptorSet frameSet, uint imageIndex, int overlayQuads)
    {
        var extent = _gBuffer.Display;
        var swapchainImage = _swapchain.Images[imageIndex];
        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, ColorStage, AccessFlags2.None, ColorStage, ColorWrite);

        // From here on the picture is the window's size, which the scene may not be.
        var window = _swapchain.Extent;
        ReadOnlySpan<DescriptorSet> finishSets = [frameSet, _finishSet];
        var transition = _transition;
        transition.Style.Z = extent.Width != window.Width || extent.Height != window.Height ? 1f : 0f;
        _finishPass.Record(cmd, _swapchain.Views[imageIndex], window, finishSets,
            new ReadOnlySpan<byte>(&transition, (int)TransitionConstants.Size));
        _gBuffer.LdrReady = true;
        _gpuTimer.Mark(cmd);

        if (overlayQuads > 0)
        {
            _overlayPass.Record(cmd, _swapchain.Views[imageIndex], window, _overlaySet, _ring.Index, overlayQuads);
        }

        _gpuTimer.Mark(cmd);

        if (_capture is not null)
        {
            RecordCapture(cmd, swapchainImage, window, _capture);
            return;
        }

        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr, ColorStage, ColorWrite,
            PipelineStageFlags2.None, AccessFlags2.None);
    }

    /// <summary>
    /// Keeps the last finished frame for a dissolve or wipe to show over the new ones,
    /// or black if there is none yet. Leaves both images ready to sample.
    /// </summary>
    private void RecordHold(CommandBuffer cmd, GBuffer targets, bool copyLastFrame)
    {
        // The finish pass sampled both in the frame before.
        _api.Transition(cmd, targets.Held.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, SampledStage, SampledAccess,
            PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);

        if (copyLastFrame)
        {
            _api.Transition(cmd, targets.Ldr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal, SampledStage, SampledAccess,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);

            var region = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Extent3D(targets.Display.Width, targets.Display.Height, 1),
            };
            _api.CmdCopyImage(cmd, targets.Ldr.Image, ImageLayout.TransferSrcOptimal,
                targets.Held.Image, ImageLayout.TransferDstOptimal, 1, &region);

            // Tonemapping waits on the fragment stage before it writes Ldr, which comes after this.
            _api.Transition(cmd, targets.Ldr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit, SampledStage, AccessFlags2.None);
        }
        else
        {
            var black = new ClearColorValue(0f, 0f, 0f, 1f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _api.CmdClearColorImage(cmd, targets.Held.Image, ImageLayout.TransferDstOptimal, &black, 1, &range);
        }

        _api.Transition(cmd, targets.Held.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
            SampledStage, SampledAccess);
        targets.HeldReady = true;
    }

    /// <summary>
    /// Keeps this frame for the next one, and leaves it ready to sample. The frame is still
    /// a target when it was <paramref name="drawnJustNow"/>, and already ready to sample when not.
    /// </summary>
    private void RecordHistoryCopy(CommandBuffer cmd, Image frame, bool drawnJustNow)
    {
        _copier.RecordCopy(cmd, frame, _gBuffer.History.Image, _gBuffer.Extent, drawnJustNow);
        _gBuffer.HistoryReady = true;
    }


    /// <summary>
    /// Draws and lights the frame's scene into the HDR target. Its shadow draws were picked per cascade by
    /// <see cref="SelectDraws"/>. The whole scene's draws update this frame's batch of light probes and its reflection
    /// probe capture.
    /// </summary>
    private void RecordScene(CommandBuffer cmd, DescriptorSet frameSet, in FrameDraws draws)
    {
        var targets = _gBuffer;
        var instances = _frames[_ring.Index].Instances;

        // The lighting pass binds history even before there is any.
        if (!targets.HistoryReady)
        {
            _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
        }

        RecordSunShadows(cmd, frameSet, instances);
        RecordTileShadows(cmd, frameSet, instances);
        _gpuTimer.Mark(cmd);
        RecordLightSetup(cmd, frameSet, draws, instances);
        RecordGBuffer(cmd, targets, frameSet, draws.Camera, instances);
        _gpuTimer.Mark(cmd);

        _occlusion.Record(cmd, targets, frameSet, _off, _gpuTimer);

        if (_traceView && draws.Traced is { } scene)
        {
            RecordTraceView(cmd, targets, frameSet, scene);
            return;
        }

        _lighting.RecordReflections(cmd, targets, frameSet, _off, _gpuTimer);
        _lighting.RecordLighting(cmd, targets, frameSet, _off, _gpuTimer);
        RecordTransparent(cmd, targets, frameSet, draws.Camera, instances);
    }

    /// <summary>
    /// Draws the sun's shadow cascades. Targets are shared between frames in flight, so each first use waits for the
    /// previous frame's sampling to finish. Contents are discarded via Undefined. When no sun casts a shadow the maps
    /// aren't drawn, or even cleared, and nothing reads them, but the lighting pass still binds them.
    /// </summary>
    private void RecordSunShadows(CommandBuffer cmd, DescriptorSet frameSet, InstanceBuffer instances)
    {
        var shadowMap = _shadowPass.Target.Image;
        if (_sunShadowsDrawn)
        {
            _api.Transition(cmd, shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, ShadowReadStages, SampledAccess, DepthStages,
                DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit, layerCount: ShadowPass.MaxCascades);
            for (int cascade = 0; cascade < _shadowCascades; cascade++)
            {
                var casters = _shadowDraws[cascade].AsSpan(0, _shadowCounts[cascade]);
                _shadowPass.Record(cmd, frameSet, casters, _meshStore.MeshSlots, _meshStore.DetailSlots, instances, cascade);
            }

            _api.Transition(cmd, shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, ShadowReadStages, SampledAccess,
                layerCount: ShadowPass.MaxCascades);
        }
        else
        {
            _api.Transition(cmd, shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ShadowReadStages, SampledAccess, ShadowReadStages, SampledAccess,
                layerCount: ShadowPass.MaxCascades);
        }
    }

    /// <summary>Draws the shadow tiles of the point and spot lights into the atlas.</summary>
    private void RecordTileShadows(CommandBuffer cmd, DescriptorSet frameSet, InstanceBuffer instances)
    {
        // The lighting pass binds the atlas even when no light has a tile in it.
        // Tiles that weren't drawn keep their depths from earlier frames, so the atlas keeps its contents from frame to
        // frame. On its first frame it has none, and the cache remembers no tile, so every tile is drawn.
        var atlas = _shadowPass.Atlas.Image;
        var kept = _atlasKept ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined;
        var ranges = _tileRanges.AsSpan(0, _tileCount);
        if (ShadowPass.AnyRedrawn(ranges))
        {
            _api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                kept, ImageLayout.DepthAttachmentOptimal, ShadowReadStages, SampledAccess, DepthStages,
                DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);
            _shadowPass.RecordTiles(cmd, frameSet, _localLights.Tiles, _tileDraws, _tileRanges.AsSpan(0, _tileCount), _meshStore.MeshSlots, _meshStore.DetailSlots, instances);
            _api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, ShadowReadStages, SampledAccess);
        }
        else if (!_atlasKept)
        {
            _api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ShadowReadStages, SampledAccess, ShadowReadStages, SampledAccess);
        }

        _atlasKept = true;
    }

    /// <summary>
    /// Updates the light probes and the reflection probe capture from the whole scene, sorts the lights into clusters
    /// and fills the volumetric fog, each as a timed pass, ahead of the G-buffer.
    /// </summary>
    private void RecordLightSetup(CommandBuffer cmd, DescriptorSet frameSet, in FrameDraws draws, InstanceBuffer instances)
    {
        // After the lights' tiles, which the probes' captures read for point and spot light shadows.
        LightProbes.Record(cmd, frameSet, draws.Scene, instances, draws.Traced);
        _gpuTimer.Mark(cmd);

        // After the light probes, whose light the capture's surfaces take.
        RecordReflectionCapture(cmd, frameSet, draws.Scene, instances);
        _gpuTimer.Mark(cmd);

        if (_clustersNeeded)
        {
            RecordClusters(cmd, frameSet);
        }

        _gpuTimer.Mark(cmd);

        // The fog's air needs the shadow maps and the light lists, and the lighting pass needs the fog.
        if (_volumetricDrawn)
        {
            _volumetricFog.Record(cmd, frameSet);
        }

        _gpuTimer.Mark(cmd);
    }

    /// <summary>
    /// Draws the camera's opaque draws and the decals into the G-buffer, and leaves it ready to sample. Draws that
    /// don't take decals, such as characters, are drawn after them, over what they laid.
    /// </summary>
    private void RecordGBuffer(
        CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        ReadOnlySpan<Image> colors =
            [targets.BaseColor.Image, targets.Normal.Image, targets.Emissive.Image, targets.Motion.Image, targets.Baked.Image, targets.Mask.Image];
        foreach (var color in colors)
        {
            _api.Transition(cmd, color, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        }

        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, DepthStages, AccessFlags2.DepthStencilAttachmentReadBit, DepthStages,
            DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);

        bool depthTarget = RecordOpaque(cmd, targets, frameSet, draws, instances);
        foreach (var color in colors)
        {
            _api.Transition(cmd, color, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        }

        if (depthTarget)
        {
            _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, SampledStage, SampledAccess);
        }
    }

    /// <summary>
    /// Draws the opaque draws and the decals, with what the draws read: where they stood last frame, the space's
    /// lightmaps and, for the camera's draws, which know their chunk, a relit chunk's bake. Returns whether depth is
    /// still a target: the decals leave it ready to read, unless draws came after them.
    /// </summary>
    private bool RecordOpaque(
        CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        // A chunk's bake is relit by the frame's sky and suns: its sky is projected first, for writeBaked.
        if (_frameBakes.Relight.Active)
        {
            _skySh.Record(cmd);
            instances.Chunks = _cameraChunks;
            instances.ChunkLightmaps = _frameBakes.Relight.Lightmaps;
        }

        instances.History = _motion;
        instances.Lightmaps = _frameBakes.SpaceLightmaps;
        bool late = _decalPass.Count > 0 && SceneDraws.AnyIn(draws, _resources, ScenePass.OpaqueAfterDecals);
        _gBufferPass.Record(cmd, targets, frameSet, draws, _meshStore.MeshSlots, _meshStore.DetailSlots, instances, DepthFirstView,
            late ? ScenePass.OpaqueTakingDecals : ScenePass.Opaque);
        bool decalsDrawn = RecordDecals(cmd, targets, frameSet);
        if (late)
        {
            RecordAfterDecals(cmd, targets, frameSet, draws, instances);
        }

        instances.History = null;
        instances.Lightmaps = null;
        instances.Chunks = null;
        instances.ChunkLightmaps = null;
        return !decalsDrawn || late;
    }

    /// <summary>
    /// The trace debug view writes Hdr in place of lighting, and nothing blends over it. Depth is left read-only as the
    /// transparent pass would leave it, for the passes after. Marks the timer as the passes it stands in for would.
    /// </summary>
    private void RecordTraceView(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, TopLevelStructure scene)
    {
        _gpuTimer.Mark(cmd);
        _traceDebugPass!.Record(cmd, _ring.Index, _tracing!, scene, frameSet, _resources.Set, targets.Hdr, targets.Extent);
        _gpuTimer.Mark(cmd);
        _gpuTimer.Mark(cmd);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
            AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
        _gpuTimer.Mark(cmd);
    }

    /// <summary>
    /// Blends glass and particles over the lit image, furthest first, and the probe debug view's spheres over
    /// everything. Depth is read-only throughout: glass tests against it, and both read it to see what's behind them.
    /// </summary>
    private void RecordTransparent(
        CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
            AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
        _transparentPass.Record(cmd, targets, frameSet, draws, _meshStore.MeshSlots, _meshStore.DetailSlots, instances, _transparentView,
            _particlePass, _ring.Index, _particleCount, _particleCamera, _windReach);

        // With the probes off there are no spheres, and no atlases to read.
        if (_probeView && LightProbes.InUse)
        {
            uint spheres = (uint)LightProbes.Batch.Cascades.Layout(0).Total;
            if (LightProbes.Batch.Grid is { } grid)
            {
                var (x, y, z) = ProbeGrid.Counts(grid);
                spheres += (uint)(x * y * z);
            }

            _probeDebugPass.Record(cmd, targets, frameSet, _lighting.LightingSet, spheres);
        }

        _api.EndColorTarget(cmd, targets.Hdr.Image);
        _gpuTimer.Mark(cmd);
    }

    /// <summary>
    /// Lays the prepared decals onto the G-buffer just drawn, and returns whether there were any.
    /// They read the normal target while writing it, so it is copied first and they read the copy.
    /// Depth is left ready to sample, the base colour and normal targets ready to draw into.
    /// </summary>
    private bool RecordDecals(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet)
    {
        if (_decalPass.Count == 0)
        {
            return false;
        }

        const PipelineStageFlags2 copy = PipelineStageFlags2.CopyBit;
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, ColorStage, ColorWrite, copy, AccessFlags2.TransferReadBit);
        _api.Transition(cmd, targets.NormalCopy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, SampledStage, SampledAccess, copy, AccessFlags2.TransferWriteBit);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        _api.CmdCopyImage(cmd, targets.Normal.Image, ImageLayout.TransferSrcOptimal,
            targets.NormalCopy.Image, ImageLayout.TransferDstOptimal, 1, &region);
        _api.Transition(cmd, targets.NormalCopy.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, copy, AccessFlags2.TransferWriteBit, SampledStage, SampledAccess);
        // The decals load the normals and blend over them, so the change of layout must land before
        // that read as well as before their writes.
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal, copy, AccessFlags2.TransferReadBit, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);

        // The colour blends over what the opaque pass wrote.
        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, SampledStage, SampledAccess);

        _decalPass.Record(cmd, targets, frameSet, _ring.Index);
        return true;
    }

    /// <summary>
    /// Draws what doesn't take decals over the G-buffer the decals were laid on. The decals read depth and wrote the
    /// colour and normals, so depth goes back to being drawn into, and the draws wait for the decals' writes.
    /// </summary>
    private void RecordAfterDecals(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances)
    {
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthAttachmentOptimal, SampledStage, SampledAccess, DepthStages,
            DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);
        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);
        _gBufferPass.Record(cmd, targets, frameSet, draws, _meshStore.MeshSlots, _meshStore.DetailSlots, instances, pass: ScenePass.OpaqueAfterDecals);
    }

    /// <summary>
    /// Sorts the point and spot lights into the view's clusters on the GPU, before anything
    /// that lights reads the lists. The last frame to use this slot's lists has finished,
    /// as its fence was waited on, so only this frame's readers need to wait.
    /// </summary>
    private void RecordClusters(CommandBuffer cmd, DescriptorSet frameSet)
    {
        _api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _clusterPipeline);
        _api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _clusterLayout, 0, 1, &frameSet, 0, null);
        _api.CmdDispatch(cmd, (LocalLights.ClusterCount + 63) / 64, 1, 1);

        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>Copies the finished frame into a buffer the CPU can read, then readies it for display.</summary>
    private void RecordCapture(CommandBuffer cmd, Image swapchainImage, Extent2D extent, GpuBuffer capture)
    {
        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
        };
        _api.CmdCopyImageToBuffer(cmd, swapchainImage, ImageLayout.TransferSrcOptimal, capture.Handle, 1, &region);

        var hostBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &hostBarrier,
        };
        _api.CmdPipelineBarrier2(cmd, &dependency);

        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.CopyBit, AccessFlags2.None, PipelineStageFlags2.None, AccessFlags2.None);
        _captureRecorded = true;
    }

    /// <summary>
    /// Writes the frame's uniforms, with the camera's view shifted by <paramref name="shift"/>, and last frame's.
    /// Returns whether a light casts a shadow this frame.
    /// </summary>
    private bool WriteUniforms(
        in Frame frame, in FrameView view, in Matrix4x4 previousViewProjection, Vector2 shift, ReadOnlySpan<Vector4> casters)
    {
        bool historyValid = _gBuffer.HistoryReady;
        var uniforms = (FrameUniforms*)frame.Uniforms.Mapped;
        WriteCamera(uniforms, view, previousViewProjection, shift);
        WriteEffects(uniforms, view, shift, historyValid);
        uniforms->ProbeCount = _probes.Prepare(
            view.View, view.Projection, new Span<GpuReflectionProbe>(frame.ReflectionProbes.Mapped, ReflectionProbeOrder.MaxProbes));
        WriteLightProbes(uniforms, view);
        WriteBakedProbes(uniforms);
        WriteSky(uniforms, view);
        WriteWater(uniforms, view);

        uniforms->Time = view.Time;
        uniforms->PreviousTime = historyValid ? _previousTime : view.Time;
        bool castsShadow = WriteSuns(uniforms, view, casters);
        uniforms->Off = unchecked((int)(_off & ShadedEffects & ~RenderEffects.DepthFirst))
            | (_off.HasFlag(RenderEffects.Fades) ? FadesOffBit : 0)
            | (_traceView ? DebugViewBit : 0);
        uniforms->MipBias = _off.HasFlag(RenderEffects.AntiAliasing) ? 0f
            : _upscaling ? FsrMipBias(_gBuffer.Extent.Width, _gBuffer.Display.Width)
            : TemporalMipBias;
        _sunShadowsDrawn = castsShadow;
        WriteLocalLights(frame, uniforms, view);

        // Probe updates and reflection probe captures light their faces with every point and spot light of the frame,
        // and see the sun's shadow through maps of their own, each zero when there is none this frame.
        uniforms->ProbeLightCount = _localLights.ProbeLightCount;
        uniforms->ProbeSunMatrix = LightProbes.SunMatrix;
        uniforms->ReflectionSunMatrix = _reflectionCaptureSlot >= 0 ? _reflectionCapture.SunMatrix : default;
        return castsShadow;
    }

    /// <summary>The camera's matrices, this frame's and last frame's, and which frame this is.</summary>
    private void WriteCamera(FrameUniforms* uniforms, in FrameView view, in Matrix4x4 previousViewProjection, Vector2 shift)
    {
        var viewProjection = view.View * view.Projection * Matrix4x4.CreateTranslation(shift.X, shift.Y, 0f);
        Matrix4x4.Invert(viewProjection, out var inverse);
        uniforms->ViewProjection = viewProjection;
        uniforms->View = view.View;
        uniforms->InverseViewProjection = inverse;
        uniforms->PreviousViewProjection = previousViewProjection;
        uniforms->FrameNumber = _frameNumber;
        uniforms->HistoryValid = _gBuffer.HistoryReady ? 1 : 0;
        uniforms->ShowEnvironment = view.ShowEnvironment ? 1 : 0;
    }

    /// <summary>The picture's grade and the view's haze, occlusion, outlines, fog, puddles and wind.</summary>
    private void WriteEffects(FrameUniforms* uniforms, in FrameView view, Vector2 shift, bool historyValid)
    {
        var post = PostGrade.Without(view.Post, _off);
        uniforms->Grade = new Vector4(MathF.Pow(2f, post.Exposure), 1f + post.Contrast, 1f + post.Saturation, post.Vignette);
        uniforms->Finish = new Vector4(post.Sharpen, post.Grain, shift.X, shift.Y);
        (uniforms->Lift, uniforms->Gain) = PostGrade.Tint(post);
        uniforms->WhiteBalance = PostGrade.WhiteBalance(post.Temperature, post.Tint);
        uniforms->Curve = PostGrade.Curve(post);
        (uniforms->HazeOrigin, uniforms->HazeColor) = Haze.Pack(view.CameraPosition, view.BackgroundFadeOrigin, view.BackgroundFadeColor);
        uniforms->Occlusion = Occlusion.Pack(view.Occlusion, view.Bounce, _off, historyValid);
        (uniforms->Outline, uniforms->OutlineFade) = Outlines.Pack(view.Outlines, _gBuffer.Extent.Width / (float)Math.Max(view.Width, 1));
        uniforms->Fog = Fog.Pack(view.Fog, _off, underwater: Underwater(view));
        WriteVolumetric(uniforms, view, historyValid);
        uniforms->Puddles = Puddles.Pack(view.Puddles, _off);
        uniforms->Wind = Wind.Pack(view.Wind, _off);
    }

    /// <summary>Where the live light probes are: the fixed grid's box and the cascades round the camera.</summary>
    private void WriteLightProbes(FrameUniforms* uniforms, in FrameView view)
    {
        // The fixed grid, read wherever it has a probe updated.
        bool inUse = LightProbes.InUse;
        if (inUse && view.LightProbes is { } lightProbes)
        {
            var (x, y, z) = ProbeGrid.Counts(lightProbes);
            uniforms->LightProbeMin = new Vector4(lightProbes.Min, lightProbes.Spacing);
            uniforms->LightProbeCounts = new Vector4(x, y, z, 1f);
        }
        else
        {
            uniforms->LightProbeMin = Vector4.Zero;
            uniforms->LightProbeCounts = Vector4.Zero;
        }

        // The cascades, read once any of their probes holds an update.
        var cascades = LightProbes.Batch.Cascades;
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var origin = cascades.Origin(c);
            uniforms->ProbeCascadeOrigins[c] = new Vector4(origin.X, origin.Y, origin.Z, cascades.Layout(c).Spacing);
        }

        bool readCascades = inUse && LightProbes.Volume.CascadesWritten;
        var cascadeLayout = cascades.Layout(0);
        uniforms->ProbeCascadeCounts = new Vector4(
            cascadeLayout.CountX, cascadeLayout.CountY, cascadeLayout.CountZ, readCascades ? 1f : 0f);
    }

    /// <summary>The baked space's probes and box, and how far its bake has faded in.</summary>
    private void WriteBakedProbes(FrameUniforms* uniforms)
    {
        // Inside the baked space's box, a fresh bake's probes give the soft light, blended into the live light over one
        // probe spacing in from the box's faces, or, with BakedProbeBand off for a test, cut off hard at them. The
        // shader's box is the probe grid's Min..Max, which is the space's box: the space's box is in the bake's input
        // hash, so a fresh bake's grid matches it.
        if (_frameBakes.SpaceProbes is { } baked)
        {
            var (x, y, z) = ProbeGrid.Counts(baked.Settings);
            uniforms->BakedProbeMin = new Vector4(baked.Settings.Min, baked.Settings.Spacing);
            uniforms->BakedProbeCounts = new Vector4(x, y, z, BakedProbeBand ? 1f : 2f);
            uniforms->BakedSpaceMin = new Vector4(baked.Settings.Min, baked.Irradiance);
            uniforms->BakedSpaceMax = new Vector4(baked.Settings.Max, baked.Depth);
        }
        else
        {
            uniforms->BakedProbeMin = Vector4.Zero;
            uniforms->BakedProbeCounts = Vector4.Zero;
            uniforms->BakedSpaceMin = Vector4.Zero;
            uniforms->BakedSpaceMax = Vector4.Zero;
        }

        // How far the space's bake has faded in, which weighs its lightmaps and probes against the live light.
        uniforms->BakedFade = new Vector4(_frameBakes.SpaceFade, 0f, 0f, 0f);
    }

    /// <summary>The camera's place, the environment's light, the sky's sun and night, and the shadows' settings.</summary>
    private void WriteSky(FrameUniforms* uniforms, in FrameView view)
    {
        uniforms->CameraPosition = new Vector4(view.CameraPosition, 1f);
        uniforms->BackgroundRadiance = new Vector4(view.BackgroundRadiance, view.EnvironmentIntensity);
        var flat = _environment.Flat;
        uniforms->FlatIrradiance = new Vector4(flat.Irradiance, 0f);
        uniforms->FlatReflection = new Vector4(flat.Reflection, 0f);
        Last.Flat = flat;

        // Probes leave the sun's disc out: their rough levels would scatter it into specks, and
        // the sun's own light already gives surfaces its highlight. The sky is the one BakeSky worked out from the
        // original view, so a frame drawn without its baked sun still shows that sun's disc.
        var sky = _bakedSky;
        uniforms->SkySun = new Vector4(sky.Direction, sky.Turbidity);
        var disc = new Vector4(sky.DiscRadiance, sky.DiscCosine);
        uniforms->SunDisc = disc;
        Last.FromCamera(disc, view.Lights);

        // The physical sky's air between the eye and far surfaces; see SkyModel.AirShows for where it shows.
        (uniforms->AirExtinction, uniforms->AirSun) = Atmosphere.Air(
            sky.Turbidity, sky.Direction, view.EnvironmentIntensity, SkyModel.AirShows(view, _off));
        (uniforms->Moon, uniforms->MoonRadiance, uniforms->Stars) = SkyModel.Night(view, sky.Direction, _off);

        // Moving points two texels of the first cascade off their surface stops them
        // shadowing themselves. The lighting pass works this out per cascade itself.
        float normalOffset = view.Shadows.Radius * 4f / _shadowPass.Resolution;
        // A fade from 0 to 0 is none: the shaders fade only where the end lies past the start.
        bool haze = !_off.HasFlag(RenderEffects.Haze);
        uniforms->Parameters = new Vector4(
            haze ? view.BackgroundFadeStart : 0f, haze ? view.BackgroundFadeEnd : 0f, view.Shadows.LightAngle, normalOffset);
    }

    /// <summary>The water the camera is under, or none above it.</summary>
    private void WriteWater(FrameUniforms* uniforms, in FrameView view)
    {
        var water = Underwater(view) ? view.Water : default;
        uniforms->WaterAbsorption = new Vector4(water.Absorption, water.Absorption != Vector3.Zero ? 1f : 0f);
        uniforms->WaterShallow = new Vector4(water.ShallowColor, water.SurfaceHeight);
        uniforms->WaterDeep = new Vector4(water.DeepColor, water.ShaftStrength);
        uniforms->WaterLight = new Vector4(water.CausticStrength, water.CausticScale, water.ShaftDistance, 0f);
    }

    /// <summary>
    /// The suns, and the shadow cascades of the first one that casts a shadow. Returns whether one does.
    /// </summary>
    private bool WriteSuns(FrameUniforms* uniforms, in FrameView view, ReadOnlySpan<Vector4> casters)
    {
        int count = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        uniforms->LightCount = count;
        _shadowCascades = Math.Min(ShadowPass.CascadeCount(view.Shadows), MaxShadowCascades);
        uniforms->ShadowCascades = _shadowCascades;
        bool castsShadow = false;
        uniforms->ShadowDepthScales = Vector4.One;
        for (int i = 0; i < count; i++)
        {
            ref readonly var light = ref view.Lights[i];
            var direction = Vector3.Normalize(light.Direction);
            bool casts = !castsShadow && light.CastsShadow && view.Shadows.Radius > 0f && !_off.HasFlag(RenderEffects.SunShadows);
            if (casts)
            {
                uniforms->ShadowRadii = FitCascades(view, direction, casters, out uniforms->ShadowFade, out uniforms->ShadowDepthScales);
                uniforms->ShadowFade.W = ShadowPass.ContactLength(view.Shadows, _off);
                for (int cascade = 0; cascade < _shadowCascades; cascade++)
                {
                    uniforms->ShadowMatrices[cascade] = _shadowMatrices[cascade];
                }

                castsShadow = true;
            }

            uniforms->Lights[i] = new GpuLight
            {
                Direction = new Vector4(direction, casts ? 1f : 0f),
                Radiance = new Vector4(light.Radiance, i == _frameBakes.MaskSlots.Sun ? 0f : -1f),
            };
        }

        // With no sun shadow there are no maps to look up, so nothing walks the cascades.
        uniforms->ShadowCascades = castsShadow ? _shadowCascades : 0;
        return castsShadow;
    }

    /// <summary>
    /// The point and spot lights the frame draws: none while <see cref="RenderEffects.LocalLights"/> is off. Bakes
    /// keep reading the view's own, so the switch never makes a bake stale.
    /// </summary>
    private ReadOnlySpan<LocalLight> DrawnLocalLights(in FrameView view) =>
        _off.HasFlag(RenderEffects.LocalLights) ? [] : view.LocalLights;

    /// <summary>
    /// Fills the volumetric fog's part of the frame block and writes the fog volumes it draws, and decides whether
    /// this frame draws the fog at all.
    /// </summary>
    private void WriteVolumetric(FrameUniforms* uniforms, in FrameView view, bool historyValid)
    {
        var fogVolumes = FogVolumesOf(view);
        var volumetric = VolumetricFog.Pack(view.Fog, fogVolumes.Length, _off, UnderwaterReach(view));
        if (!_volumetricFog.HasTargets)
        {
            volumetric = Vector4.Zero;
        }

        volumetric.W = historyValid && _volumetricFog.HistoryReady ? 1f : 0f;
        uniforms->Volumetric = volumetric;
        _volumetricDrawn = volumetric.X > 0f;
        uniforms->VolumetricGrid = new Vector4(VolumetricFogSlices, VolumetricFogCellSize, _volumetricDrawn ? fogVolumes.Length : 0, 0f);
        if (_volumetricDrawn)
        {
            _volumetricFog.WriteFogVolumes(fogVolumes);
        }
        else
        {
            // A frame without the fog leaves nothing for the next to carry on from.
            _volumetricFog.Skip();
        }
    }

    /// <summary>
    /// The view's fog volumes, or none when they're switched off. Throws for too many, or for one that can't be
    /// drawn, as <see cref="FogVolumeBatch.Check"/> says.
    /// </summary>
    private ReadOnlySpan<FogVolumeDraw> FogVolumesOf(in FrameView view)
    {
        var volumes = _off.HasFlag(RenderEffects.FogVolumes) ? [] : view.FogVolumes;
        if (volumes.Length > FogVolumeBatch.MaxVolumes)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A frame can draw at most {FogVolumeBatch.MaxVolumes} fog volumes, but it was given {volumes.Length}."), nameof(view));
        }

        foreach (ref readonly var volume in volumes)
        {
            FogVolumeBatch.Check(volume.Shape, volume.Transform, volume.Settings);
        }

        return volumes;
    }

    /// <summary>
    /// Makes the volumetric fog's volumes the first time a frame draws it, frees them while it's switched off, and
    /// remakes them when the cell size or slice count changes. Each waits for the GPU, which is rare.
    /// </summary>
    private void KeepVolumetricTargets(in FrameView view)
    {
        int width = (int)_gBuffer.Extent.Width;
        int height = (int)_gBuffer.Extent.Height;
        if (!_volumetricFog.HasTargets && VolumetricFog.Pack(view.Fog, FogVolumesOf(view).Length, _off, UnderwaterReach(view)).X > 0f)
        {
            // The fog's volumes are made the first time it's drawn. Frames in flight bind the stand-in.
            _context.WaitIdle();
            _volumetricFog.SetSize(width, height, VolumetricFogCellSize, VolumetricFogSlices);
            WriteTargetDescriptors();
        }
        else if (_volumetricFog.HasTargets && VolumetricFog.CanFreeTargets(_off, UnderwaterReach(view)))
        {
            // Switched off, the volumes are freed, and made again with no history if it's switched back on.
            // Frames in flight still read them. Flipping the switch is rare.
            _context.WaitIdle();
            _volumetricFog.Release();
            WriteTargetDescriptors();
        }
        else if (_volumetricFog.HasTargets
            && _volumetricFog.Size != VolumetricFog.GridSize(width, height, VolumetricFogCellSize, VolumetricFogSlices))
        {
            // A new cell size or slice count remakes the grid, with no history. Frames in flight still read the old one.
            _context.WaitIdle();
            _volumetricFog.SetSize(width, height, VolumetricFogCellSize, VolumetricFogSlices);
            WriteTargetDescriptors();
        }
    }

    /// <summary>Whether the camera is under the view's water, which it never is while <see cref="RenderEffects.Underwater"/> is off.</summary>
    private bool Underwater(in FrameView view) =>
        view.Water.Absorption != Vector3.Zero && !_off.HasFlag(RenderEffects.Underwater);

    /// <summary>How far the volumetric fog reaches under water, the shafts' distance, or null above water.</summary>
    private float? UnderwaterReach(in FrameView view) => Underwater(view) ? view.Water.ShaftDistance : null;

    /// <summary>
    /// Picks the point and spot lights the view needs and their shadow tiles, and writes
    /// them where the shaders read them.
    /// </summary>
    private void WriteLocalLights(in Frame frame, FrameUniforms* uniforms, in FrameView view)
    {
        if (view.LocalLights.Length > LocalLight.MaxPerFrame)
        {
            throw new ArgumentException(
                $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights, not {view.LocalLights.Length}.", nameof(view));
        }

        _localLights.Prepare(
            DrawnLocalLights(view),
            view.View,
            view.Projection,
            (int)_gBuffer.Extent.Height,
            new Span<GpuLocalLight>(frame.Lights.Mapped, LocalLight.MaxPerFrame),
            new Span<GpuShadowTile>(frame.ShadowTiles.Mapped, LocalLights.MaxTiles),
            shadows: !_off.HasFlag(RenderEffects.LightShadows),
            maskChannels: _frameBakes.MaskSlots.Channels,
            cover: _probes.Order.Count > 0 ? (_probes.Order.NearDepth, _probes.Order.FarDepth) : null);
        _tileCount = _localLights.TileCount;
        _clustersNeeded = _localLights.LightCount > 0 || _probes.Order.Count > 0;
        uniforms->LocalLightCount = _localLights.LightCount;
        uniforms->Clusters = new Vector4(_localLights.NearDepth, _localLights.FarDepth, _localLights.Parallel ? 1f : 0f, 0f);
        uniforms->Screen = new Vector4(_gBuffer.Extent.Width, _gBuffer.Extent.Height, 1f / _gBuffer.Extent.Width, 1f / _gBuffer.Extent.Height);
    }

    /// <summary>
    /// Places each cascade's map. In a perspective view each ball takes the next slice of
    /// what the camera sees, starting at the camera, so none of it is spent behind. Its radius
    /// never changes as the camera turns, and its centre moves in whole texels, so edges don't
    /// shimmer. A parallel view keeps every cascade centred on the settings' centre. Returns
    /// each cascade's radius, and how shadows fade out with distance.
    /// </summary>
    private Vector4 FitCascades(
        in FrameView view, Vector3 direction, ReadOnlySpan<Vector4> casters, out Vector4 fade, out Vector4 depthScales)
    {
        var radii = Vector4.Zero;
        depthScales = Vector4.One;
        float? spread = ShadowPass.Spread(view.Projection);
        Matrix4x4.Invert(view.View, out var world);
        var eye = world.Translation;
        var forward = Vector3.Normalize(-new Vector3(world.M31, world.M32, world.M33));
        float start = 0f;
        for (int cascade = 0; cascade < _shadowCascades; cascade++)
        {
            float radius = ShadowPass.CascadeRadius(view.Shadows, cascade);
            var center = view.Shadows.Center;
            float reach = radius;
            if (spread is { } opening)
            {
                (float along, start) = ShadowPass.FitSlice(start, radius, opening);
                center = eye + forward * along;

                // Reach towards the sun at least as far as a cascade round the target would,
                // so tall things out of view, like the edge of a wood in a low sun, still cast.
                reach = radius + MathF.Max(Vector3.Dot(view.Shadows.Center - center, direction), 0f);

                // And far enough to take in whatever stands up-sun of it, like a tree out of
                // frame whose shade falls on a wall in it.
                reach = ShadowPass.CasterReach(center, radius, direction, reach, casters);
            }

            _shadowMatrices[cascade] = ShadowPass.CreateMatrix(direction, center, radius, reach, _shadowPass.Resolution);
            radii[cascade] = radius;
            depthScales[cascade] = ShadowPass.DepthScale(radius, reach);
        }

        // The last slice ends where shadows do, and they fade out over its last part rather
        // than stopping at a line. A parallel view has no such end, so only the map's edge fades.
        fade = spread is null
            ? new Vector4(float.MaxValue, float.MaxValue, ShadowPass.HandOver, 0f)
            : new Vector4(start * (1f - ShadowPass.FadeShare), start, ShadowPass.HandOver, 0f);
        return radii;
    }

    /// <summary>What tonemapping and bloom read: the anti-aliased image, or the lit one when anti-aliasing is left out.</summary>
    private ImageView DisplaySource => _readsAntiAliased ? _gBuffer.Resolved.View : _gBuffer.Hdr.View;

    /// <summary>
    /// Makes a new swapchain for the window's size and vsync. The scene's targets are only made again when
    /// the size changed, so switching vsync keeps the anti-aliasing history and the next picture is the
    /// same as it would have been.
    /// </summary>
    private void Resize(uint width, uint height)
    {
        _context.WaitIdle();
        var extent = _swapchain.Extent;
        _swapchain.Dispose();
        _swapchain = new Swapchain(_context, width, height, _disableVsync);
        _vsyncChanged = false;
        if (_swapchain.Extent.Width != extent.Width || _swapchain.Extent.Height != extent.Height)
        {
            RebuildTargets();
        }
    }

    /// <summary>Replaces the scene's targets with ones at the render scale, keeping the window's swapchain.</summary>
    private void RebuildTargets()
    {
        _gBuffer.Dispose();
        CreateTargets();
    }

    /// <summary>
    /// Makes the scene's targets at the window's size times the render scale, with everything that
    /// follows their size: bloom's chain, the exposure meter's dispatch and the anti-aliasing history,
    /// which starts again.
    /// </summary>
    private void CreateTargets()
    {
        var scene = SceneExtent(_swapchain.Extent);
        _gBuffer = new GBuffer(_context, scene, _upscaling ? _swapchain.Extent : scene, _reflectionResolution, _occlusionResolution);
        if (_upscaling)
        {
            try
            {
                _fsrPass.SetTargets(scene, _swapchain.Extent, _gBuffer.Depth.View, _gBuffer.Motion.View);
            }
            catch (InvalidOperationException e)
            {
                // TAA draws instead, at the scene's size, until the upscaler is asked for again.
                Console.Error.WriteLine($"{e.Message} Anti-aliasing uses TAA instead.");
                _upscalerUsed = Upscaler.Taa;
                _upscaling = false;
                _gBuffer.Dispose();
                _gBuffer = new GBuffer(_context, scene, reflections: _reflectionResolution, occlusion: _occlusionResolution);
            }
        }
        else
        {
            _fsrPass.Release();
        }

        _bloomPass.Resize(DisplaySource, _gBuffer.Display);
        _depthOfFieldPass.SetTargets(DisplaySource, _gBuffer.Depth.View, _gBuffer.Extent);
        if (_volumetricFog.HasTargets)
        {
            _volumetricFog.SetSize((int)_gBuffer.Extent.Width, (int)_gBuffer.Extent.Height, VolumetricFogCellSize, VolumetricFogSlices);
        }

        WriteTargetDescriptors();
    }

    /// <summary>The scene's size for a window this size, at the render scale.</summary>
    private Extent2D SceneExtent(Extent2D window)
    {
        var (width, height) = SceneSize((int)window.Width, (int)window.Height, _renderScale, _maxImageSize);
        return new Extent2D((uint)width, (uint)height);
    }

    /// <summary>
    /// The mip bias FSR asks for: textures read as sharp as the window's pixels would read them, and one level sharper
    /// still, since FSR gathers the detail over its frames. -1 at a render scale of 1, -2 at 0.5.
    /// </summary>
    private static float FsrMipBias(uint sceneWidth, uint displayWidth) =>
        MathF.Log2((float)sceneWidth / displayWidth) - 1f;

    /// <summary>
    /// The near plane's distance, for FSR. A reversed-Z perspective keeps it in M43; an orthographic view has no eye,
    /// so it gets a small stand-in.
    /// </summary>
    private static float CameraNear(in Matrix4x4 projection) =>
        projection.M34 != 0f && projection.M43 > 0f ? projection.M43 : 0.1f;

    /// <summary>The view's height as an angle, in radians, for FSR: from M22, which is -1 / tan(fov / 2).</summary>
    private static float VerticalFov(in Matrix4x4 projection) =>
        projection.M34 != 0f && projection.M22 != 0f ? 2f * MathF.Atan(1f / MathF.Abs(projection.M22)) : MathF.PI / 3f;

    private Frame CreateFrame()
    {
        var frame = new Frame();

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = _context.QueueFamily,
        };
        _api.CreateCommandPool(_context.Device, &poolInfo, null, out frame.CommandPool).Check();

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = frame.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        _api.AllocateCommandBuffers(_context.Device, &allocInfo, out frame.CommandBuffer).Check();

        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit, // The first frame has nothing to wait for.
        };
        _api.CreateFence(_context.Device, &fenceInfo, null, out frame.InFlight).Check();

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _api.CreateSemaphore(_context.Device, &semaphoreInfo, null, out frame.ImageAvailable).Check();

        CreateFrameBuffers(ref frame);
        frame.Set = _kit.NewSet(_kit.FrameSetLayout);
        WriteFrameDescriptors(frame);
        frame.Instances = new InstanceBuffer(_context);
        WriteInstanceDescriptor(frame);
        return frame;
    }

    // The frame's uniforms and the buffers its shaders read: bones, lights, shadow tiles, clusters, chunks and probes.
    private void CreateFrameBuffers(ref Frame frame)
    {
        frame.Uniforms = GpuBuffer.CreateHostVisible(
            _context, (ulong)sizeof(FrameUniforms), BufferUsageFlags.UniformBufferBit);
        // This frame's bone matrices, then last frame's for motion vectors.
        frame.Bones = GpuBuffer.CreateHostVisible(
            _context, (ulong)(2 * MaxBones * sizeof(Matrix4x4)), BufferUsageFlags.StorageBufferBit);
        frame.Lights = GpuBuffer.CreateHostVisible(
            _context, (ulong)(LocalLight.MaxPerFrame * sizeof(GpuLocalLight)), BufferUsageFlags.StorageBufferBit);
        frame.ShadowTiles = GpuBuffer.CreateHostVisible(
            _context, (ulong)(LocalLights.MaxTiles * sizeof(GpuShadowTile)), BufferUsageFlags.StorageBufferBit);
        frame.Clusters = new GpuBuffer(
            _context, LocalLights.ClusterBytes, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        frame.Chunks = GpuBuffer.CreateHostVisible(
            _context, (ulong)(ChunkRelight.HeaderBytes + BakeFiles.MaxChunks * sizeof(GpuChunk)), BufferUsageFlags.StorageBufferBit);
        frame.ReflectionProbes = GpuBuffer.CreateHostVisible(
            _context, (ulong)(ReflectionProbeOrder.MaxProbes * sizeof(GpuReflectionProbe)), BufferUsageFlags.StorageBufferBit);
    }

    // Points the frame's set at its buffers, as CreateFrameSetLayout lays them out.
    private void WriteFrameDescriptors(in Frame frame)
    {
        var chunksInfo = new DescriptorBufferInfo(frame.Chunks.Handle, 0, frame.Chunks.Size);
        var uniformInfo = new DescriptorBufferInfo(frame.Uniforms.Handle, 0, (ulong)sizeof(FrameUniforms));
        var bonesInfo = new DescriptorBufferInfo(frame.Bones.Handle, 0, frame.Bones.Size);
        var lightsInfo = new DescriptorBufferInfo(frame.Lights.Handle, 0, frame.Lights.Size);
        var tilesInfo = new DescriptorBufferInfo(frame.ShadowTiles.Handle, 0, frame.ShadowTiles.Size);
        var clustersInfo = new DescriptorBufferInfo(frame.Clusters.Handle, 0, frame.Clusters.Size);
        var probesInfo = new DescriptorBufferInfo(frame.ReflectionProbes.Handle, 0, frame.ReflectionProbes.Size);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(frame.Set, 0, &uniformInfo, DescriptorType.UniformBuffer),
            Descriptors.BufferWrite(frame.Set, 1, &bonesInfo),
            Descriptors.BufferWrite(frame.Set, 3, &lightsInfo),
            Descriptors.BufferWrite(frame.Set, 4, &tilesInfo),
            Descriptors.BufferWrite(frame.Set, 5, &clustersInfo),
            Descriptors.BufferWrite(frame.Set, 11, &chunksInfo),
            Descriptors.BufferWrite(frame.Set, 13, &probesInfo),
        };
        _api.UpdateDescriptorSets(_context.Device, 7, writes, 0, null);
    }

    /// <summary>
    /// Empties this frame's model matrices for a frame of at most <paramref name="copies"/>
    /// draws, growing the buffer first if they won't fit. Must happen after the frame's
    /// fence and before its commands are recorded, since they bind the descriptor.
    /// </summary>
    private void ResetInstances(in Frame frame, int copies)
    {
        if (frame.Instances.Reset(copies))
        {
            WriteInstanceDescriptor(frame);
        }
    }

    /// <summary>The shadow atlas, read once with a depth comparison and once as plain depth.</summary>
    private void WriteAtlasDescriptors(in Frame frame)
    {
        var compare = new DescriptorImageInfo(_kit.ShadowSampler, _shadowPass.Atlas.View, ImageLayout.ShaderReadOnlyOptimal);
        var depth = new DescriptorImageInfo(_kit.NearestSampler, _shadowPass.Atlas.View, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.Set,
                DstBinding = 6,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &compare,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.Set,
                DstBinding = 7,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &depth,
            },
        };
        _api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    /// <summary>The volumetric fog's summed volume, or the texel that stands in until it's made.</summary>
    private void WriteVolumetricDescriptor(in Frame frame)
    {
        var info = new DescriptorImageInfo(_kit.LinearSampler, _volumetricFog.Result, ImageLayout.General);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = frame.Set,
            DstBinding = 8,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };
        _api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private void WriteInstanceDescriptor(in Frame frame)
    {
        var info = frame.Instances.ModelsInfo;
        var previousInfo = frame.Instances.PreviousInfo;
        var lightmapInfo = frame.Instances.LightmapInfo;
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(frame.Set, 2, &info),
            Descriptors.BufferWrite(frame.Set, 9, &previousInfo),
            Descriptors.BufferWrite(frame.Set, 10, &lightmapInfo),
        };
        _api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }

    /// <summary>
    /// Copies a frame's skin matrices to where the vertex shaders read them. Must happen
    /// after the frame's fence, since the buffer is written in place.
    /// </summary>
    private static void WriteBones(in Frame frame, ReadOnlySpan<Matrix4x4> bones)
    {
        if (bones.Length > MaxBones)
        {
            throw new ArgumentException($"A frame can have at most {MaxBones} bone matrices, not {bones.Length}.", nameof(bones));
        }

        bones.CopyTo(new Span<Matrix4x4>(frame.Bones.Mapped, bones.Length));
    }


    /// <summary>Points the fullscreen passes at the current render targets.</summary>
    private void WriteTargetDescriptors()
    {
        var depth = new DescriptorImageInfo(_kit.NearestSampler, _gBuffer.Depth.View, ImageLayout.ShaderReadOnlyOptimal);
        var normal = new DescriptorImageInfo(_kit.NearestSampler, _gBuffer.Normal.View, ImageLayout.ShaderReadOnlyOptimal);
        _decalPass.WriteTargets(depth, new DescriptorImageInfo(_kit.NearestSampler, _gBuffer.NormalCopy.View, ImageLayout.ShaderReadOnlyOptimal));

        var shadowDepth = new DescriptorImageInfo(_kit.NearestSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal);
        _occlusion.WriteSets(_gBuffer, depth, normal, shadowDepth);
        _lighting.WriteSets(_gBuffer, depth, normal, shadowDepth);
        WriteBlendSets();
        WriteDisplaySets();
    }

    // The reflection probe capture's set, and what blends over the lit image: glass and particles.
    private void WriteBlendSets()
    {
        _reflectionCapture.WriteSet(
            new(_kit.LinearSampler, LightProbes.Volume.IrradianceView, ImageLayout.General),
            new(_kit.LinearSampler, LightProbes.Volume.DepthView, ImageLayout.General),
            new(_environment.Sampler, _environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            _kit.ShadowSampler);

        // Glass and particles read depth while it's also attached for glass to test against.
        var testedDepth = new DescriptorImageInfo(_kit.NearestSampler, _gBuffer.Depth.View, ImageLayout.DepthReadOnlyOptimal);
        _transparentPass.WriteTargets(new(_kit.MipSampler, _gBuffer.SceneColor.View, ImageLayout.ShaderReadOnlyOptimal), testedDepth);

        _particlePass.WriteTargets(
            testedDepth,
            new(_kit.ShadowSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Reflections, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Background, ImageLayout.ShaderReadOnlyOptimal));
    }

    // From anti-aliasing to the window: anti-aliasing, tonemapping, the meter and the finish, and the volumetric fog.
    private void WriteDisplaySets()
    {
        // Glass has just used depth for testing, and it stays in that layout.
        WriteImages(_taaSet,
        [
            new(_kit.NearestSampler, _gBuffer.Hdr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, _gBuffer.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.NearestSampler, _gBuffer.Depth.View, ImageLayout.DepthReadOnlyOptimal),
            new(_kit.NearestSampler, _gBuffer.Motion.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        WriteImages(_tonemapSet,
        [
            new(_kit.NearestSampler, DisplaySource, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, _bloomPass.Result, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        _exposureMeter.SetSource(DisplaySource, _gBuffer.Display);
        WriteImages(_finishSet,
        [
            new(_kit.LinearSampler, _gBuffer.Ldr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, _gBuffer.Held.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);

        _volumetricFog.SetSources(
            new(_kit.ShadowSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Background, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, LightProbes.Volume.IrradianceView, ImageLayout.General),
            new(_kit.LinearSampler, LightProbes.Volume.DepthView, ImageLayout.General));
        foreach (ref var frame in _frames.AsSpan())
        {
            WriteVolumetricDescriptor(frame);
        }
    }

    private void WriteImages(DescriptorSet set, ReadOnlySpan<DescriptorImageInfo> images) =>
        ImageSets.Write(_context, set, images);
}
