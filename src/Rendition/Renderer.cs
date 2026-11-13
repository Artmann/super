using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Rendition;

/// <summary>
/// The public face of the renderer. <see cref="Render"/> allocates nothing on the
/// managed heap unless the window has been resized.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    private const int FramesInFlight = 2;
    private const int JitterSteps = 16;

    // How much sharper a mip level material textures are read at while anti-aliasing shifts the
    // view. Each frame alone would shimmer, but the frames blended together settle, and the
    // picture keeps detail the blend would otherwise soften.
    private const float TemporalMipBias = -0.75f;

    /// <summary>The most skin matrices one frame can hold across every skinned draw.</summary>
    public const int MaxBones = 16384;

    /// <summary>The most reflection probes the renderer holds at once.</summary>
    public const int MaxReflectionProbes = FrameUniforms.MaxProbes;

    private const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;

    // The shadow maps are read by the lighting pass and by the volumetric fog's compute pass.
    private const PipelineStageFlags2 ShadowReadStages = SampledStage | PipelineStageFlags2.ComputeShaderBit;
    private const AccessFlags2 SampledAccess = AccessFlags2.ShaderSampledReadBit;
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const AccessFlags2 ColorWrite = AccessFlags2.ColorAttachmentWriteBit;
    private const PipelineStageFlags2 DepthStages =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
    private const AccessFlags2 DepthWrite = AccessFlags2.DepthStencilAttachmentWriteBit;

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
        // The frame's suns and placed chunks, as chunks.glsl reads them at binding 11. See GpuChunk.
        public GpuBuffer Chunks;
        public DescriptorSet Set;
    }

    /// <summary>One placed chunk, as chunks.glsl's ChunkData reads it. Must match it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuChunk
    {
        // From the world into the chunk's space, as columns of a mat3x4: column i is row i of the turn, and the move.
        public Vector4 IntoX;
        public Vector4 IntoY;
        public Vector4 IntoZ;
        // The box in the chunk's space, with its edge blend in Min's w and how far its bake has faded in, from 0 to 1, in
        // Max's w.
        public Vector4 Min;
        public Vector4 Max;
        // For each of the frame's four mask slots, the bake's channel that holds it, or -1 when none does.
        public int Mask0, Mask1, Mask2, Mask3;
        public int SkyRed, SkyGreen, SkyBlue, Sun;
        public int Tint, MaskPage, Pad1, Pad2;
    }

    // The suns' four coefficients ahead of the chunks in each frame's chunk buffer.
    private const int SunShBytes = 4 * 16;

    // Then the space's table from the frame's mask slots to its bake's channels, four ints, and then the chunks.
    private const int ChunkHeaderBytes = SunShBytes + 4 * sizeof(int);

    private readonly VulkanContext _context;
    private bool _disableVsync;
    private bool _vsyncChanged;
    // The cascade map size asked for, which the next frame rebuilds the maps at if it differs.
    private int _shadowResolution;
    // The scene's size against the window's, which the next frame rebuilds the targets at if it differs.
    private float _renderScale = 1f;
    // The largest image side the device can make, which the scene is shrunk to fit.
    private readonly int _maxImageSize;
    private readonly Vk _api;
    private readonly Frame[] _frames = new Frame[FramesInFlight];
    // How far a simpler model may stray. Shadows are soft, so they can afford more.
    private const float CameraDetailError = 1f;
    private const float ShadowDetailError = 2f;

    private readonly List<Mesh?> _meshes = [];
    private readonly Stack<int> _freeMeshes = [];
    private readonly List<PartDetail> _details = [default];
    private readonly Stack<int> _freeDetails = [];

    /// <summary>
    /// What's been destroyed while a frame might still read it. Slot i holds what was
    /// queued while frame slot i was the last one submitted; its fence says when the
    /// GPU is done with all of it. See "Resource lifetime" in docs/design.md.
    /// </summary>
    private sealed class DestroyedSlot
    {
        public readonly List<Mesh> Meshes = [];
        public readonly List<int> Textures = [];
        public readonly List<int> TextureArrays = [];
        public readonly List<int> Materials = [];
        public readonly List<IDisposable> Others = [];
    }

    private readonly DestroyedSlot[] _destroyed = [.. Enumerable.Range(0, FramesInFlight).Select(_ => new DestroyedSlot())];
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

    // The light probe atlases, which hold nothing until the first frame with the probes on, and the
    // pass that draws a few probes' faces each frame, with the sun's shadow map they use.
    private readonly ProbeVolume _lightProbes;
    private readonly ProbeCapturePass _probeUpdatePass;
    private ProbeSunShadow? _probeSun;

    // Where the GPU can trace, probes are updated with rays instead, and need neither the faces nor the sun map.
    private readonly ProbeTracePass? _probeTracePass;
    private readonly VolumetricFogVolume _volumetricFog;
    private GBuffer? _reflectionProbeTargets;

    // Whether surfaces read the light probes this frame: they're on, the GPU can draw them, and the
    // view has any.
    private bool _lightProbesInUse;

    // Latched: the probes have settled at least once since their storage was made. Reflection captures
    // hash this rather than the live ProbesSettled, which flips on every cell the camera crosses.
    private bool _probesSettledOnce;

    // The four probe cascades that follow the camera, moved to it every frame the probes are on.
    // Their tiles live in layers 0 to 3 of the light probe atlases. Made anew with the quality.
    private ProbeCascades _probeCascades = new(ProbeQuality.High);
    private ProbeQuality _probeQuality = ProbeQuality.High;

    // Which probes to update next, made with the atlases, and this frame's batch of them.
    private ProbeScheduler? _probeScheduler;
    private readonly List<ProbeId> _probeBatch = [];
    private readonly List<Vector3> _probeBatchPositions = [];
    private readonly List<float> _probeBatchSpacings = [];

    // The cascades' moves and the fixed grid's changes that this frame must clear from the atlases.
    private readonly List<ProbeInvalidation> _probeInvalidations = [];

    // What the probes were last updated for, to see what changed: the fixed grid, the lighting that
    // reaches everything, and each point and spot light by its key.
    private ProbeVolumeSettings? _probeGrid;
    private int _probeLightingKey;
    private readonly Dictionary<int, LocalLight> _probeLights = [];
    private readonly HashSet<int> _probeLightsSeen = [];
    private readonly List<int> _probeLightsGone = [];

    // How far a probe already captured moves towards a new rasterized capture: halfway, so its light
    // settles over a few updates instead of flickering. A first capture is taken whole.
    private const float RasterBlend = 0.5f;

    // A traced update sees only 128 rays, so it moves a quarter of the way: the noise of a few updates averages
    // out. A first capture is still taken whole.
    private const float TracedBlend = 0.25f;

    // A traced update whose tile's mean light changed by this share or more is taken whole, and a smaller change moves
    // in proportion, never less than TracedBlend. Big changes, such as bounce arriving, land at once, while noise
    // still averages out. Lower than 0.05 and a dim probe flickers more.
    private const float TracedSnap = 0.05f;

    // What this frame's folds fill the sky with, where a probe saw nothing.
    private float _probeEnvironmentIntensity;
    private Vector4 _probeSkySun;

    // The frame whose uniforms Render wrote last, so SampleProbeIrradiance reads what that frame did.
    private int _uniformFrame;

    /// <summary>One layer's cells to clear, as <see cref="ProbeVolume.RecordInvalidate"/> takes them.</summary>
    private readonly record struct ProbeInvalidation(int Layer, Int3 Origin, Int3 Counts, Int3 SlabStart, Int3 SlabCount);

    // Goes up with every SetEnvironment, so the light probes see the sky has changed.
    private int _environmentVersion;

    // What the last SetEnvironment gave, hashed, and the strength the last frame drew it at, for EnvironmentHash.
    private ulong _environmentContent;
    private float _environmentIntensity;

    // The clear sky and sun the environment was last baked with. The lighting hashes cover what
    // decides it through the view, so baking it again leaves the version alone.
    private SkySun _bakedSky;

    private readonly DescriptorPool _descriptorPool;
    private readonly DescriptorSetLayout _frameSetLayout;
    private readonly DescriptorSetLayout _lightingSetLayout;
    private readonly DescriptorSetLayout _threeTextureSetLayout;
    private readonly DescriptorSetLayout _fourTextureSetLayout;
    private readonly DescriptorSetLayout _twoTextureSetLayout;
    private readonly DescriptorSetLayout _oneTextureSetLayout;
    private readonly DescriptorSet _aoSet;
    private readonly DescriptorSet _aoBlurSet;
    private readonly DescriptorSet _scatterAcrossSet;
    private readonly DescriptorSet _scatterDownSet;
    private readonly DescriptorSet _lightingSet;
    private readonly DescriptorSet _taaSet;
    private readonly DescriptorSet _tonemapSet;
    private readonly DescriptorSet _finishSet;
    private readonly Sampler _sampler;
    private readonly Sampler _linearSampler;
    private readonly Sampler _mipSampler;
    private readonly Sampler _shadowSampler;

    private readonly ShadowPass _shadowPass;
    private readonly GBufferPass _gBufferPass;
    private readonly FullscreenPass _aoPass;
    private readonly FullscreenPass _aoBlurPass;
    private readonly FullscreenPass _lightingPass;
    private readonly FullscreenPass _scatterAcrossPass;
    private readonly FullscreenPass _scatterDownPass;
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
    private GpuTexture? _overlayAtlas;

    private Swapchain _swapchain;
    private GBuffer _gBuffer;
    private int _frameIndex;
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

    private bool _historyReady;
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

    // How far the frame's wind can move each material's vertices, which culling adds to the bounds.
    private WindReach _windReach;
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

    // Moving between pictures: what the frame asks for, which hold the held picture
    // belongs to, and whether the held picture and the last finished frame exist yet.
    private TransitionConstants _transition;
    private int _transitionHold;
    private int _heldNumber;
    private bool _heldReady;
    private bool _ldrReady;

    /// <summary>Vulkan validation messages so far. Always zero when validation is off.</summary>
    public int ValidationErrors => _context.ValidationErrors;

    /// <summary>
    /// Whether the GPU can trace rays, with ray queries turned on for the device. Nothing requires it: a GPU
    /// that can't, or <see cref="RendererOptions.DisableRayTracing"/>, just draws without tracing.
    /// </summary>
    public bool CanTrace => _context.CanTrace;

    /// <summary>Why <see cref="CanTrace"/> is false, such as "no VK_KHR_ray_query", or null when it is true.</summary>
    public string? NoTraceReason => _context.NoTraceReason;

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

    /// <summary>How long the last frame waited for the GPU to finish earlier work and for the display, while <see cref="MeasureGpu"/> is on.</summary>
    public double WaitMilliseconds { get; private set; }

    /// <summary>Triangles in the last frame's camera and shadow passes, while <see cref="MeasureGpu"/> is on.</summary>
    public (long Camera, long Shadows) DrawnTriangles { get; private set; }

    private DrawItem[] _cameraDraws = [];
    // Which draw each camera draw came from, and the placed chunk each camera draw is relit as, -1 for none. Only
    // filled on frames that relight a chunk.
    private int[] _cameraSources = [];
    private int[] _cameraChunks = [];

    // The frame's relit chunks, written into the frame's chunk buffer, and each draw's chunk, -1 for none. See
    // FindRelitChunks.
    private readonly GpuChunk[] _chunkData = new GpuChunk[BakeFiles.MaxChunks];
    private readonly Vector4[] _sunSh = new Vector4[4];
    private int[] _drawChunks = [];
    private int _chunkCount;
    private bool _relightChunks;
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
    private readonly (int First, int Count)[] _tileRanges = new (int, int)[LocalLights.MaxTiles];
    private int _tileCount;
    private bool _clustersNeeded;
    private readonly PipelineLayout _clusterLayout;
    private readonly Pipeline _clusterPipeline;

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

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        _api.CreateSampler(_context.Device, &samplerInfo, null, out _sampler).Check();

        var linearSamplerInfo = samplerInfo with { MagFilter = Filter.Linear, MinFilter = Filter.Linear };
        _api.CreateSampler(_context.Device, &linearSamplerInfo, null, out _linearSampler).Check();

        // Frosted glass reads the blurrier mips of the scene copy.
        var mipSamplerInfo = linearSamplerInfo with { MipmapMode = SamplerMipmapMode.Linear, MaxLod = Vk.LodClampNone };
        _api.CreateSampler(_context.Device, &mipSamplerInfo, null, out _mipSampler).Check();

        // Reversed-Z: a point is lit when it is at least as near the light as the map says.
        var shadowSamplerInfo = samplerInfo with
        {
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            CompareEnable = true,
            CompareOp = CompareOp.GreaterOrEqual,
        };
        _api.CreateSampler(_context.Device, &shadowSamplerInfo, null, out _shadowSampler).Check();

        _frameSetLayout = CreateFrameSetLayout();
        _lightingSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 15, ShaderStageFlags.FragmentBit);
        _threeTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 3, ShaderStageFlags.FragmentBit);
        _fourTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 4, ShaderStageFlags.FragmentBit);
        _twoTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 2, ShaderStageFlags.FragmentBit);
        _oneTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.UniformBuffer, FramesInFlight),
            new(DescriptorType.StorageBuffer, 9 * FramesInFlight),
            new(DescriptorType.CombinedImageSampler, 34 + 3 * FramesInFlight),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = FramesInFlight + 9,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes,
        };
        _api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool).Check();

        for (int i = 0; i < FramesInFlight; i++)
        {
            _frames[i] = CreateFrame();
        }

        _resources = new ResourceTable(_context);
        _chunkPool = new ChunkBakePool(_context, _resources, ChunkBakePool.ChunkUploadBudget, ChunkUnreadable);
        if (_context.CanTrace)
        {
            _tracing = new RayTracing(_context, _resources, MeshFor, _uploads.IsPending,
                releaseAfterSubmitted: structure => Destroying().Others.Add(structure),
                releaseAfterRecorded: used => _destroyed[_frameIndex].Others.Add(used));
            _traceScene = new TraceScene(TraceFlagsOf);
        }

        _traceDebugPass = _tracing is null ? null : new TraceDebugPass(_context, FramesInFlight, _frameSetLayout, _resources.Layout);

        _shadowResolution = ShadowPass.CheckResolution(options.ShadowResolution);
        _shadowPass = new ShadowPass(_context, _frameSetLayout, _resources, _shadowResolution);
        foreach (ref var frame in _frames.AsSpan())
        {
            WriteAtlasDescriptors(frame);
        }

        var clusterSetLayout = _frameSetLayout;
        var clusterLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &clusterSetLayout,
        };
        _api.CreatePipelineLayout(_context.Device, &clusterLayoutInfo, null, out _clusterLayout).Check();
        _clusterPipeline = PipelineFactory.CreateCompute(_context, "cluster.comp", _clusterLayout);

        _environment = new EnvironmentMap(_context);
        SetEnvironment(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.5f),
            Horizon = new Vector3(0.4f),
            Floor = new Vector3(0.2f),
        });
        _skySh = new SkySh(_context, _environment.Sky, _environment.Sampler);
        foreach (ref var frame in _frames.AsSpan())
        {
            var skyShInfo = new DescriptorBufferInfo(_skySh.Handle, 0, SkySh.Size);
            var skyShWrite = StorageWrite(frame.Set, 12, &skyShInfo);
            _api.UpdateDescriptorSets(_context.Device, 1, &skyShWrite, 0, null);
        }

        _probes = new ReflectionProbes(_context, _environment.Sampler);
        _lightProbes = new ProbeVolume(_context, _environment.Sampler, _environment.Sky);
        _probeUpdatePass = new ProbeCapturePass(_context, _frameSetLayout, _resources);
        if (_tracing is not null && _lightProbes.Supported)
        {
            _probeTracePass = new ProbeTracePass(_context, FramesInFlight, _frameSetLayout, _resources,
                ProbeBudget.For(ProbeQuality.High).Traced);
        }

        _volumetricFog = new VolumetricFogVolume(_context, _frameSetLayout, _linearSampler);

        _aoSet = AllocateSet(_threeTextureSetLayout);
        _aoBlurSet = AllocateSet(_twoTextureSetLayout);
        _scatterAcrossSet = AllocateSet(_twoTextureSetLayout);
        _scatterDownSet = AllocateSet(_twoTextureSetLayout);
        _lightingSet = AllocateSet(_lightingSetLayout);
        _taaSet = AllocateSet(_fourTextureSetLayout);
        _tonemapSet = AllocateSet(_twoTextureSetLayout);
        _finishSet = AllocateSet(_twoTextureSetLayout);
        _overlaySet = AllocateSet(_oneTextureSetLayout);
        _bloomPass = new BloomPass(_context, _linearSampler, _gBuffer.Resolved.View, _gBuffer.Display);
        _particlePass = new ParticlePass(_context, _frameSetLayout, FramesInFlight);
        _decalPass = new DecalPass(_context, _frameSetLayout, _resources, FramesInFlight);
        _transparentPass = new TransparentPass(_context, _frameSetLayout, _resources, _environment, _probes);
        _probeDebugPass = new ProbeDebugPass(_context, _frameSetLayout, _lightingSetLayout);
        _exposureMeter = new ExposureMeter(_context, _sampler);
        _fsrPass = new FsrPass(_context, _sampler, _frameSetLayout, _exposureMeter.SetLayout);
        _depthOfFieldPass = new DepthOfFieldPass(_context, _sampler, _linearSampler, _frameSetLayout);
        _depthOfFieldPass.SetTargets(DisplaySource, _gBuffer.Depth.View, _gBuffer.Extent);
        WriteTargetDescriptors();

        _gBufferPass = new GBufferPass(_context, _frameSetLayout, _resources);
        _aoPass = new FullscreenPass(_context, "ao.frag", GBuffer.AoFormat, [_frameSetLayout, _threeTextureSetLayout]);
        _aoBlurPass = new FullscreenPass(_context, "ao_blur.frag", GBuffer.AoFormat, [_twoTextureSetLayout]);
        _lightingPass = new FullscreenPass(_context, "lighting.frag", [GBuffer.HdrFormat, GBuffer.HdrFormat],
            [_frameSetLayout, _lightingSetLayout, _resources.Layout]);
        _scatterAcrossPass = new FullscreenPass(_context, "sss_blur.frag", [GBuffer.HdrFormat],
            [_frameSetLayout, _twoTextureSetLayout, _resources.Layout], BlurConstants.Size);
        _scatterDownPass = new FullscreenPass(_context, "sss_blur.frag", [GBuffer.HdrFormat],
            [_frameSetLayout, _twoTextureSetLayout, _resources.Layout], BlurConstants.Size, blend: true);
        _taaPass = new FullscreenPass(_context, "taa.frag", GBuffer.HdrFormat, [_frameSetLayout, _fourTextureSetLayout]);
        _tonemapPass = new FullscreenPass(_context, "tonemap.frag", GBuffer.LdrFormat,
            [_frameSetLayout, _twoTextureSetLayout, _exposureMeter.SetLayout]);
        _finishPass = new FullscreenPass(_context, "finish.frag", _swapchain.Format, [_frameSetLayout, _twoTextureSetLayout], TransitionConstants.Size);
        _overlayPass = new OverlayPass(_context, _swapchain.Format, _oneTextureSetLayout, _resources, FramesInFlight);
        _gpuTimer = new GpuTimer(_context, FramesInFlight);
        _bakeTimer = new GpuTimer(_context, FramesInFlight, ["Background bake"]);
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
        return AddMesh(new Mesh(vertexBuffer, indexBuffer, (uint)indices.Length)
        {
            CumulativeAreas = LightmapLayout.CumulativeAreas(vertices, indices),
        });
    }

    /// <summary>
    /// The area, in a live mesh's own units, of the triangles in an index range, for <see cref="LightmapLayout.Build"/>.
    /// Read from a running total kept when the mesh was made. Throws for a mesh that isn't live.
    /// </summary>
    internal float AreaOf(MeshHandle handle, uint firstIndex, uint indexCount) =>
        LightmapLayout.RangeArea(MeshFor(handle).CumulativeAreas, firstIndex, indexCount);

    /// <summary>The mesh behind a live handle. For tests, which read its buffers' addresses.</summary>
    internal Mesh MeshFor(MeshHandle handle) =>
        handle.Index >= 0 && handle.Index < _meshes.Count && _meshes[handle.Index] is { } mesh
            ? mesh
            : throw new ArgumentException($"Mesh {handle.Index} is not live.", nameof(handle));

    /// <summary>The Vulkan context, for tests that make buffers of their own.</summary>
    internal VulkanContext Context => _context;

    /// <summary>The acceleration structures rays are traced through, or null when the GPU can't trace.</summary>
    internal RayTracing? Tracing => _tracing;

    /// <summary>
    /// The probe cascades, where the last frame moved them. For tests, which find a probe's slot with
    /// <see cref="ProbeCascades.Slot"/>.
    /// </summary>
    internal ProbeCascades LightProbeCascades => _probeCascades;

    /// <summary>How many light probes the last frame updated: none while they're off, or settled and unchanged.</summary>
    internal int ProbeUpdatesLastFrame { get; private set; }

    /// <summary>For tests: whether the light probes' atlases exist, which they don't while the probes are off.</summary>
    internal bool ProbeStorageAllocated => _lightProbes.Allocated;

    /// <summary>
    /// Whether every probe near the camera, in the two finest cascades and the fixed grid, has been
    /// updated since its light last changed. Screenshots wait for it. False while the probes are switched off and
    /// before their first frame, but true on a GPU that can't run them, as there is nothing to wait for there.
    /// </summary>
    public bool ProbesSettled => _probeScheduler?.Settled ?? !_lightProbes.Supported;

    /// <summary>For tests: while true, no probe is updated, so what a test writes into the atlases stays.</summary>
    internal bool ProbeUpdatesPaused { get; set; }

    /// <summary>For tests: the most triangles a frame draws round its probes, as <see cref="ProbeCapturePass.MaxTrianglesPerFrame"/>.</summary>
    internal long ProbeTrianglesPerFrame { get; set; } = ProbeCapturePass.MaxTrianglesPerFrame;

    /// <summary>
    /// How many light probes follow the camera, and how many are updated a frame: see
    /// <see cref="Rendition.ProbeQuality"/>. A change makes the probes' atlases again, all uncaptured,
    /// on the next frame with the probes on, which waits for the GPU once.
    /// </summary>
    public ProbeQuality ProbeQuality
    {
        get => _probeQuality;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Probe quality can be Low, Medium or High.");
            }

            _probeQuality = value;
        }
    }

    /// <summary>For tests: clears every probe tile, in every layer, so each probe is uncaptured. Waits for the GPU.</summary>
    internal void ClearProbesForTest()
    {
        EnsureProbeStorage();
        _api.DeviceWaitIdle(_context.Device).Check();
        _context.SubmitAndWait(_lightProbes.RecordClear);
        _probesSettledOnce = false;
    }

    /// <summary>
    /// For tests that share a renderer: makes the probes' atlases, cascades and scheduler anew, as a new renderer
    /// would have them, so every probe is uncaptured and the scheduler's frames count from 0 again. Waits for the GPU.
    /// </summary>
    internal void ResetProbesForTest()
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        if (_lightProbes.Release())
        {
            WriteTargetDescriptors();
        }

        _probeScheduler = null;
        _probeBatch.Clear();
        _probeInvalidations.Clear();
        _probesSettledOnce = false;
        ProbeUpdatesLastFrame = 0;
        EnsureProbeStorage();
    }

    /// <summary>
    /// For tests: marks every probe within <paramref name="range"/> of <paramref name="center"/> for an update, as a
    /// light changing there would.
    /// </summary>
    internal void MarkProbesDirtyForTest(Vector3 center, float range) => _probeScheduler?.LightChanged(center, range);

    /// <summary>
    /// For tests: writes a probe's tiles as if it had been captured seeing <paramref name="irradiance"/>
    /// all round and nothing within 100 m, through the staging buffer and the blend, so storage can be
    /// tested without a capture. Waits for the GPU.
    /// </summary>
    internal void WriteProbeTileForTest(ProbeId id, Vector3 irradiance)
    {
        const int irradianceTexels = (ProbeVolume.IrradianceTexels + 2) * (ProbeVolume.IrradianceTexels + 2);
        const int depthTexels = (ProbeVolume.DepthTexels + 2) * (ProbeVolume.DepthTexels + 2);

        EnsureProbeStorage();
        _api.DeviceWaitIdle(_context.Device).Check();
        using var source = GpuBuffer.CreateHostVisible(_context, ProbeVolume.StagedTileBytes, BufferUsageFlags.TransferSrcBit);
        var irradianceTile = new Span<Vector4>(source.Mapped, irradianceTexels);
        irradianceTile.Fill(new Vector4(irradiance, 0f));
        var depthTile = new Span<Vector2>((Vector4*)source.Mapped + irradianceTexels, depthTexels);
        depthTile.Fill(new Vector2(100f, 100f * 100f));

        _context.SubmitAndWait(cmd =>
        {
            var copy = new BufferCopy(0, 0, ProbeVolume.StagedTileBytes);
            _api.CmdCopyBuffer(cmd, source.Handle, _lightProbes.StagingTiles, 1, &copy);
            _lightProbes.RecordBlend(cmd, [id], 1f);
        });
    }

    /// <summary>
    /// For tests: the light probes' soft light at each point, facing its normal, from the same
    /// probeIrradiance the lighting pass calls, with the uniforms of the last frame rendered. -1 where
    /// no probe can tell, which the lighting turns into the sky. Waits for the GPU, so never on the frame path.
    /// </summary>
    internal Vector3[] SampleProbeIrradiance(ReadOnlySpan<(Vector3 Point, Vector3 Normal)> samples)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        var device = _context.Device;
        int count = samples.Length;
        ulong bytes = (ulong)Math.Max(count, 1) * 32;
        using var input = GpuBuffer.CreateHostVisible(_context, bytes, BufferUsageFlags.StorageBufferBit);
        using var output = GpuBuffer.CreateHostVisible(_context, bytes / 2, BufferUsageFlags.StorageBufferBit);
        var points = new Span<Vector4>(input.Mapped, count * 2);
        for (int i = 0; i < count; i++)
        {
            points[i * 2] = new Vector4(samples[i].Point, 1f);
            points[i * 2 + 1] = new Vector4(samples[i].Normal, 0f);
        }

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(11, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(12, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };
        _api.CreateDescriptorSetLayout(device, &layoutInfo, null, out var setLayout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.StorageBuffer, 2),
            new(DescriptorType.CombinedImageSampler, 2),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        _api.CreateDescriptorPool(device, &poolInfo, null, out var pool).Check();

        var layouts = stackalloc DescriptorSetLayout[] { _frameSetLayout, setLayout };
        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, sizeof(int));
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        _api.CreatePipelineLayout(device, &pipelineLayoutInfo, null, out var pipelineLayout).Check();
        var pipeline = PipelineFactory.CreateCompute(_context, "probe_sample.comp", pipelineLayout);

        try
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            _api.AllocateDescriptorSets(device, &allocInfo, out var set).Check();

            var inputInfo = new DescriptorBufferInfo(input.Handle, 0, Vk.WholeSize);
            var outputInfo = new DescriptorBufferInfo(output.Handle, 0, Vk.WholeSize);
            var irradianceInfo = new DescriptorImageInfo(_linearSampler, _lightProbes.IrradianceView, ImageLayout.General);
            var depthInfo = new DescriptorImageInfo(_linearSampler, _lightProbes.DepthView, ImageLayout.General);
            var writes = stackalloc WriteDescriptorSet[]
            {
                new()
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &inputInfo,
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &outputInfo,
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 11, DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &irradianceInfo,
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 12, DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &depthInfo,
                },
            };
            _api.UpdateDescriptorSets(device, 4, writes, 0, null);

            var sets = stackalloc DescriptorSet[] { _frames[_uniformFrame].Set, set };
            _context.SubmitAndWait(cmd =>
            {
                int pushed = count;
                _api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
                _api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pipelineLayout, 0, 2, sets, 0, null);
                _api.CmdPushConstants(cmd, pipelineLayout, ShaderStageFlags.ComputeBit, 0, sizeof(int), &pushed);
                _api.CmdDispatch(cmd, (uint)(count + 63) / 64, 1, 1);
                var barrier = new MemoryBarrier2
                {
                    SType = StructureType.MemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                    SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                    DstStageMask = PipelineStageFlags2.HostBit,
                    DstAccessMask = AccessFlags2.HostReadBit,
                };
                var dependency = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    MemoryBarrierCount = 1,
                    PMemoryBarriers = &barrier,
                };
                _api.CmdPipelineBarrier2(cmd, &dependency);
            });

            var results = new Vector3[count];
            var read = new ReadOnlySpan<Vector4>(output.Mapped, count);
            for (int i = 0; i < count; i++)
            {
                results[i] = new Vector3(read[i].X, read[i].Y, read[i].Z);
            }

            return results;
        }
        finally
        {
            _api.DestroyPipeline(device, pipeline, null);
            _api.DestroyPipelineLayout(device, pipelineLayout, null);
            _api.DestroyDescriptorPool(device, pool, null);
            _api.DestroyDescriptorSetLayout(device, setLayout, null);
        }
    }

    /// <summary>The bindless textures and materials, which a shader that traces rays reads hits through.</summary>
    internal ResourceTable Resources => _resources;

    /// <summary>How rays treat a material, for <see cref="TraceScene.Collect"/>.</summary>
    internal TraceFlags TraceFlagsOf(MaterialHandle material) =>
        RayTracing.TraceFlagsFor(_resources.HasCutouts(material), _resources.GetFlags(material));

    private MeshHandle AddMesh(Mesh mesh)
    {
        if (_freeMeshes.Count > 0)
        {
            int slot = _freeMeshes.Pop();
            _meshes[slot] = mesh;
            return new MeshHandle(slot);
        }

        _meshes.Add(mesh);
        return new MeshHandle(_meshes.Count - 1);
    }

    /// <summary>
    /// Lets go of a mesh and its detail entries. Returns at once; the memory comes back
    /// two frames later, when no frame in flight can read it. Drawing with the handle
    /// after this is a caller mistake that isn't checked.
    /// </summary>
    public void DestroyMesh(MeshHandle mesh)
    {
        if (mesh.Index < 0 || mesh.Index >= _meshes.Count || _meshes[mesh.Index] is not { } live)
        {
            throw new ArgumentException("The mesh does not exist.", nameof(mesh));
        }

        _meshes[mesh.Index] = null;
        _freeMeshes.Push(mesh.Index);

        // A mesh made this frame may still be waiting for its bytes. That copy must not
        // run into a buffer that is about to be freed.
        _uploads.Cancel(live.Vertices);
        _uploads.Cancel(live.Indices);
        Destroying().Meshes.Add(live);

        // A background bake still tracing it would read freed buffers, so it stops and starts again from the scene.
        if (_bakeJob is { } job && _bakeFinishing is null && job.Uses(mesh))
        {
            StopBakeJob();
        }

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
        Destroying().TextureArrays.Add(array.Index);
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
        // A background bake whose materials read it would trace a freed image, so it stops and starts again.
        if (_bakeJob is { } job && _bakeFinishing is null && BakeReadsTexture(job, slot))
        {
            StopBakeJob();
        }

        _uploads.Cancel(_resources.MarkTextureDying(slot));
        Destroying().Textures.Add(slot);
    }

    // Whether a material of one of the job's draws names the texture in this slot.
    private bool BakeReadsTexture(BakeJob job, int slot)
    {
        foreach (ref readonly var draw in job.Draws)
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
        // The slot may go to a material with a different opaque flag, so no structure may keep using it.
        _tracing?.ForgetMaterial(new MaterialHandle(slot));
        _resources.MarkMaterialDying(slot);
        Destroying().Materials.Add(slot);
        if (_bakeJob is { } job && _bakeFinishing is null && job.Uses(new MaterialHandle(slot)))
        {
            StopBakeJob();
        }
    }

    /// <summary>The slot whose fence covers the frame most recently submitted.</summary>
    private DestroyedSlot Destroying() => _destroyed[(_frameIndex + FramesInFlight - 1) % FramesInFlight];

    /// <summary>Frees what was queued against a frame slot, once its fence has signalled.</summary>
    private void ReleaseDestroyed(int frameSlot)
    {
        var slot = _destroyed[frameSlot];
        foreach (var mesh in slot.Meshes)
        {
            foreach (int detail in mesh.Details)
            {
                _details[detail] = default;
                _freeDetails.Push(detail);
            }

            mesh.Dispose();
        }

        foreach (int texture in slot.Textures)
        {
            _resources.ReleaseTexture(texture);
        }

        foreach (int array in slot.TextureArrays)
        {
            _resources.ReleaseTextureArray(array);
        }

        foreach (int material in slot.Materials)
        {
            _resources.ReleaseMaterial(material);
        }

        foreach (var other in slot.Others)
        {
            other.Dispose();
        }

        slot.Meshes.Clear();
        slot.Textures.Clear();
        slot.TextureArrays.Clear();
        slot.Materials.Clear();
        slot.Others.Clear();
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

    /// <summary>Replaces what surfaces reflect. Waits for the GPU, so call this while loading.</summary>
    public void SetEnvironment(in EnvironmentDescription environment)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _environment.Bake(environment, _bakedSky);
        _environmentVersion++;
        _environmentContent = HashEnvironment(environment);
    }

    /// <summary>
    /// A hash of what a baked space's light comes from besides its draws and lights: the environment's bands and panels
    /// from the last <see cref="SetEnvironment"/>, and the clear sky's turbidity and the environment intensity of the
    /// last frame drawn. It follows what they hold, so setting the same environment again gives the same hash. See
    /// "Is the bake fresh?" in docs/design.md.
    /// </summary>
    public ulong EnvironmentHash => BakeInputHash.Hasher()
        .Add(_environmentContent)
        .Add(_bakedSky.Turbidity)
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
        get => _bakeResolver.FadeSeconds;
        set => _bakeResolver.FadeSeconds = value is >= 0f and <= MaxBakeFadeSeconds
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
    public BakeFrame? LastBakeFrame { get; private set; }

    private readonly AnimatedLights _animatedLights = new(Console.Error);
    private readonly BakeResolver _bakeResolver = new(NoBakes.Instance) { FadeSeconds = 0.5f };
    private Func<DetailHandle, BakeSphere>? _boundsOf;
    private DirectionalLight[] _bakeKeptSuns = [];
    private LocalLight[] _bakeKeptLights = [];
    private bool _drawingWithoutBakedLights;

    // How far the frame's space bake has faded in, for the frame uniforms: 1 unless one is fading in.
    private float _spaceFade = 1f;

    // Whether a bake this frame draws is still fading in: the space's, or a relit chunk's. Reflection captures see it.
    private bool _bakesFading;
    private bool _chunksFading;

    // A Baked light while its bake fades in, its radiance scaled by what the bake doesn't give yet.
    private static readonly Func<DirectionalLight, float, DirectionalLight> DimSun = static (sun, left) =>
    {
        sun.Radiance *= left;
        return sun;
    };

    private static readonly Func<LocalLight, float, LocalLight> DimLocalLight = static (light, left) =>
    {
        light.Radiance *= left;
        return light;
    };

    // Bakes is asked through this, so a bake that can't be read counts as missing until the store reloads.
    private readonly ReadableBakes _readableBakes = new();

    // The space bake whose lightmaps are on the GPU, kept while it isn't drawn so turning it back on reads nothing.
    private LoadedBake _loadedBake;

    // The space's lightmap rectangles this frame draws, by bake id: the loaded space bake's while it is drawn, or null.
    // A chunk's rectangles are kept apart, in _chunkLightmaps, because copies of one chunk name can have different bakes
    // with the same ids.
    private IReadOnlyDictionary<ulong, GpuLightmapRect>? _activeLightmaps;

    // Whether this frame draws the loaded space bake's lightmaps.
    private bool _spaceLightmapsActive;

    // The chunk bakes on the GPU, shared by every copy of a chunk.
    private readonly ChunkBakePool _chunkPool;

    /// <summary>The chunk bakes being read, uploaded or on the GPU. For tests.</summary>
    internal ChunkBakePool ChunkBakes => _chunkPool;

    // The resident chunk bakes this frame names, in the order first named.
    private readonly List<ChunkBakePool.Entry> _namedChunks = [];

    // The lightmap rectangles of each chunk this frame relights, by bake id, or null. A draw reads only its own chunk's.
    private readonly IReadOnlyDictionary<ulong, GpuLightmapRect>?[] _chunkLightmaps =
        new IReadOnlyDictionary<ulong, GpuLightmapRect>?[BakeFiles.MaxChunks];

    // A hash of the chunk bakes this frame draws, 0 for none, for the reflection probes to see one come or go.
    private ulong _chunkLightmapsHash;
    private Func<string, BakeInputs, bool>? _chunkResident;

    // While this frame draws a bake: the index, among the suns drawn, of the space's Mixed sun whose shadow is in the
    // frame's mask slot 0, or -1; and the frame's mask slot of each Mixed local light that has one, by key. A space
    // mask's unused channels hold 0, so a light missing here must never read it. See MaskSlots.
    private int _maskSun = -1;
    private readonly Dictionary<int, int> _maskChannels = [];

    // The space bake's channel for each of the frame's four mask slots: itself, or -1 where a chunk light took the
    // slot, so that light reads 1 on the space's surfaces. Written at the head of the chunk buffer every frame.
    private readonly int[] _spaceSlots = [0, 1, 2, 3];

    // MaskSlots.Assign's scratch, grown when a frame has more lights, and which chunk and channel each chunk light is.
    private (int Key, int SpaceChannel)[] _slotSpaceLights = [];
    private (int Key, int Chunk, Vector3 Position, float Range)[] _slotChunkLights = [];
    private (int Chunk, int Channel)[] _slotChunkOf = [];
    private int[] _slots = [];

    // The pool entry of each chunk this frame relights, or null.
    private readonly ChunkBakePool.Entry?[] _relitEntries = new ChunkBakePool.Entry?[BakeFiles.MaxChunks];

    // The loaded bake's probes while this frame lights the baked space with them, or null when it doesn't.
    private BakedProbes? _activeBakedProbes;

    /// <summary>
    /// The bake whose lightmaps are uploaded, and which file, inputs and store reload it came from. Channels is each
    /// listed local light's shadow mask channel, by key. Probes is its light probes, whose atlases are among Pages.
    /// </summary>
    private record struct LoadedBake(
        string? File, ulong Inputs, IBakeStore? Store, int Reloads, Dictionary<ulong, GpuLightmapRect>? Rects, List<TextureHandle> Pages,
        Dictionary<int, int>? Channels, BakedProbes? Probes);

    /// <summary>A bake's light probes: their grid, and the material table slots of their irradiance and depth atlases.</summary>
    private sealed record BakedProbes(ProbeVolumeSettings Settings, int Irradiance, int Depth);

    /// <summary>A store's bakes, less those found unreadable, which count as missing.</summary>
    private sealed class ReadableBakes : IBakeStore
    {
        public IBakeStore Inner = NoBakes.Instance;
        public int Reloads;
        public readonly HashSet<string> Unreadable = [];

        public bool IsFresh(string file, BakeInputs inputs) => !Unreadable.Contains(file) && Inner.IsFresh(file, inputs);

        public bool IsFreshQuietly(string file, BakeInputs inputs) => !Unreadable.Contains(file) && Inner.IsFreshQuietly(file, inputs);

        public LightBakeFile? Load(string file) => Inner.Load(file);

        int IBakeStore.Reloads => Reloads;
    }

    /// <summary>
    /// Makes sure the space's fresh bake has its lightmaps on the GPU, reading it when it isn't the one already loaded.
    /// False, logged once, when it can't be read; then the file counts as missing until the store reloads.
    /// </summary>
    private bool LoadSpaceBake(string space, BakeInputs inputs)
    {
        string file = _bakeResolver.SpaceFile(space);
        var store = _readableBakes.Inner;
        ulong combined = inputs.Combined;
        if (_loadedBake.File == file && _loadedBake.Inputs == combined && ReferenceEquals(_loadedBake.Store, store)
            && _loadedBake.Reloads == store.Reloads)
        {
            return true;
        }

        UnloadBake();
        LightBakeFile? bake;
        try
        {
            bake = store.Load(file);
            if (bake is not null)
            {
                CheckPages(bake, file);
            }
            else if (GivesLightmaps(store))
            {
                // Its header was fresh a moment ago, so the file was removed or renamed since, without a reload.
                throw new InvalidDataException("The file is gone, though its header was read as fresh.");
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The bake {file} can't be read, so its area is lit live: {e.Message}");
            _readableBakes.Unreadable.Add(file);
            return false;
        }

        // A store with no Load of its own still has the area baked; its surfaces take their soft light live.
        var pages = _loadedBake.Pages ?? [];
        Dictionary<ulong, GpuLightmapRect>? rects = null;
        Dictionary<int, int>? channels = null;
        BakedProbes? probes = null;
        if (bake is not null)
        {
            rects = [];
            channels = [];
            foreach (var (lightKey, channel) in bake.Channels)
            {
                channels[lightKey] = channel;
            }

            try
            {
                // CheckPages made sure a space's bake has both.
                var irradiancePages = bake.Irradiance!;
                var directionPages = bake.Direction!;
                int count = irradiancePages.Count;
                for (int page = 0; page < count; page++)
                {
                    pages.Add(CreateTexture(PageTexture(irradiancePages[page]), TextureEdges.Clamp));
                    pages.Add(CreateTexture(PageTexture(directionPages[page]), TextureEdges.Clamp));
                    pages.Add(page < bake.Mask.Count ? CreateTexture(PageTexture(bake.Mask[page]), TextureEdges.Clamp) : new TextureHandle(-1));
                }

                foreach (var (bakeId, rect) in bake.Draws)
                {
                    rects[bakeId] = new GpuLightmapRect
                    {
                        ScaleOffset = new Vector4(rect.Scale, rect.Offset.X, rect.Offset.Y),
                        Irradiance = pages[rect.Page * 3].Index,
                        Direction = pages[rect.Page * 3 + 1].Index,
                        Mask = pages[rect.Page * 3 + 2].Index,
                        Page = rect.Page,
                    };
                }

                // Its probe atlases go in the material table too, and so live and die with the lightmaps.
                if (bake is { ProbeIrradiance: { } probeIrradiance, ProbeDepth: { } probeDepth, Probes: { } probeSettings })
                {
                    pages.Add(CreateTexture(PageTexture(probeIrradiance), TextureEdges.Clamp));
                    pages.Add(CreateTexture(PageTexture(probeDepth), TextureEdges.Clamp));
                    probes = new BakedProbes(probeSettings, pages[^2].Index, pages[^1].Index);
                }
            }
            catch
            {
                // Such as running out of device memory part way: the pages made so far go the usual way, and the
                // error goes on, since it isn't the file's fault.
                ReleasePages(pages);
                throw;
            }
        }

        _loadedBake = new LoadedBake(file, combined, store, store.Reloads, rects, pages, channels, probes);
        return true;
    }

    // Whether each store type has a Load of its own, rather than the interface's, which gives no lightmaps.
    private readonly Dictionary<Type, bool> _givesLightmaps = [];

    /// <summary>
    /// Whether the store has its own <see cref="IBakeStore.Load"/>. Only a store that leaves it out may answer null for
    /// a fresh file and still have the area count as baked.
    /// </summary>
    private bool GivesLightmaps(IBakeStore store)
    {
        var type = store.GetType();
        if (!_givesLightmaps.TryGetValue(type, out bool gives))
        {
            var map = type.GetInterfaceMap(typeof(IBakeStore));
            int load = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IBakeStore.Load));
            gives = map.TargetMethods[load].DeclaringType != typeof(IBakeStore);
            _givesLightmaps[type] = gives;
        }

        return gives;
    }

    /// <summary>Lets the loaded bake's pages go, once the frames drawing them are done.</summary>
    private void UnloadBake()
    {
        if (_loadedBake.Pages is { } pages)
        {
            ReleasePages(pages);
        }

        _loadedBake = _loadedBake with { File = null, Inputs = 0, Store = null, Rects = null, Channels = null, Probes = null };
        _activeBakedProbes = null;
    }

    /// <summary>Lets a bake's pages go, once the frames drawing them are done, and empties the list.</summary>
    private void ReleasePages(List<TextureHandle> pages)
    {
        foreach (var page in pages)
        {
            if (page.Index >= 0)
            {
                DestroyTexture(page);
            }
        }

        pages.Clear();
    }

    /// <summary>Throws when a bake's pages don't fit together, as a damaged file's might not, or when it has no irradiance and direction pages, which a space needs (only a chunk may leave them out).</summary>
    private static void CheckPages(LightBakeFile bake, string file)
    {
        if (bake is not { Irradiance: { } irradiance, Direction: { } direction })
        {
            throw new InvalidDataException(
                $"The bake {file} has no irradiance and direction pages, but a space's bake needs them: only a chunk's may leave them out.");
        }

        if (direction.Count != irradiance.Count || bake.Mask.Count > irradiance.Count)
        {
            throw new InvalidDataException(
                $"The bake has {irradiance.Count} irradiance pages but {direction.Count} direction and {bake.Mask.Count} mask pages.");
        }

        var probePages = CheckProbes(bake);
        foreach (var page in irradiance.Concat(direction).Concat(bake.Mask).Concat(probePages))
        {
            if (page.Width < 1 || page.Height < 1
                || page.Pixels.Length != TextureData.LevelSize(PageFormat(page.Format), page.Width, page.Height))
            {
                throw new InvalidDataException($"A {page.Width}x{page.Height} {page.Format} page holds {page.Pixels.Length} bytes.");
            }
        }

        foreach (var (bakeId, rect) in bake.Draws)
        {
            if (rect.Page < 0 || rect.Page >= irradiance.Count)
            {
                throw new InvalidDataException(
                    $"Draw 0x{bakeId:X16} is on page {rect.Page}, but the bake has {irradiance.Count} pages.");
            }
        }
    }

    /// <summary>
    /// Throws when a bake's light probes don't fit together: their atlases and grid come all or none, the grid holds at
    /// most <see cref="BakeFiles.MaxSpaceProbes"/>, and each atlas is as wide as the live volume's and tall enough for
    /// every probe's tile, so probes.glsl reads them as it reads the live ones. Gives the atlases, to check as pages.
    /// </summary>
    private static LightBakePage[] CheckProbes(LightBakeFile bake)
    {
        if (bake is { ProbeIrradiance: null, ProbeDepth: null, Probes: null })
        {
            return [];
        }

        if (bake is not { ProbeIrradiance: { } irradiance, ProbeDepth: { } depth, Probes: { } settings })
        {
            throw new InvalidDataException(
                "The bake has some of its light probes' irradiance atlas, depth atlas and grid, but not all three.");
        }

        if (!float.IsFinite(settings.Spacing) || settings.Spacing <= 0f || !float.IsFinite(settings.Min.X + settings.Min.Y + settings.Min.Z)
            || !float.IsFinite(settings.Max.X + settings.Max.Y + settings.Max.Z))
        {
            throw new InvalidDataException($"The bake's light probes are {settings.Spacing} m apart in {settings.Min} to {settings.Max}.");
        }

        long total = ProbeGrid.Total(settings);
        if (total > BakeFiles.MaxSpaceProbes)
        {
            throw new InvalidDataException($"The bake has {total} light probes, more than the {BakeFiles.MaxSpaceProbes} a space may have.");
        }

        int rows = (int)((total + BakedProbeAtlas.Columns - 1) / BakedProbeAtlas.Columns);
        CheckAtlas(irradiance, "irradiance", BakedProbeAtlas.IrradianceAtlasSize, rows * (ProbeVolume.IrradianceTexels + 2));
        CheckAtlas(depth, "depth", BakedProbeAtlas.DepthAtlasSize, rows * (ProbeVolume.DepthTexels + 2));
        return [irradiance, depth];
    }

    private static void CheckAtlas(LightBakePage atlas, string name, int width, int minimumHeight)
    {
        if (atlas.Width != width || atlas.Height < minimumHeight)
        {
            throw new InvalidDataException(
                $"The bake's {atlas.Width}x{atlas.Height} probe {name} atlas should be {width} wide and at least {minimumHeight} tall.");
        }
    }

    internal static TextureFormat PageFormat(LightBakePageFormat format) => format switch
    {
        LightBakePageFormat.Rgba16f => TextureFormat.Rgba16f,
        LightBakePageFormat.Rgba8 => TextureFormat.Rgba8,
        LightBakePageFormat.Bc6h => TextureFormat.Bc6h,
        LightBakePageFormat.Bc7 => TextureFormat.Bc7,
        _ => throw new InvalidDataException($"A page has the unknown format {(int)format}."),
    };

    // Linear, so RGBA8 and BC7 pages are read as they were written rather than as sRGB colour.
    private static TextureData PageTexture(LightBakePage page) => new()
    {
        Width = page.Width,
        Height = page.Height,
        Kind = TextureKind.Linear,
        Format = PageFormat(page.Format),
        Mips = [page.Pixels],
    };

    /// <summary>
    /// Works out the frame's baked areas, and is true when a fresh bake takes some lights over: then
    /// <paramref name="lit"/> is the frame without them. A frame with no baked area is left alone, unhashed.
    /// </summary>
    private bool ResolveBakes(in FrameView view, ReadOnlySpan<DrawItem> draws, out FrameView lit)
    {
        lit = default;
        _maskSun = -1;
        _maskChannels.Clear();
        for (int s = 0; s < _spaceSlots.Length; s++)
        {
            _spaceSlots[s] = s;
        }

        if (view.BakedSpace is null && view.BakedChunks.IsEmpty)
        {
            LastBakeFrame = null;
            _activeLightmaps = null;
            _spaceLightmapsActive = false;
            _chunkLightmapsHash = 0;
            _activeBakedProbes = null;
            _relightChunks = false;
            _spaceFade = 1f;
            _bakesFading = false;
            // The next frame that names an area is its first, so a bake fresh then shows at once.
            _bakeResolver.ForgetArrivals();
            return false;
        }

        // A bake that couldn't be read is tried again only once the store is another or its files may have changed.
        // The chunk bakes on the GPU go too, so each is read again from the store as it is now.
        if (!ReferenceEquals(_readableBakes.Inner, Bakes) || _readableBakes.Reloads != Bakes.Reloads)
        {
            _readableBakes.Inner = Bakes;
            _readableBakes.Reloads = Bakes.Reloads;
            _readableBakes.Unreadable.Clear();
            var destroying = Destroying();
            _chunkPool.Clear(destroying.Textures, destroying.Others);
        }

        // Files the store forgot one at a time are read again, and only those: the other chunk bakes stay up.
        foreach (string file in Bakes.TakeForgotten())
        {
            _readableBakes.Unreadable.Remove(file);
            var destroying = Destroying();
            _chunkPool.Forget(file, destroying.Textures, destroying.Others);
        }

        // With baked lighting off, every bake counts as missing, so the frame is drawn as if nothing were baked.
        _bakeResolver.Store = view.Off.HasFlag(RenderEffects.BakedLighting) ? NoBakes.Instance : _readableBakes;
        _bakeResolver.ChunkResident = _chunkResident ??= ChunkResident;
        _namedChunks.Clear();
        var bakes = _bakeResolver.Resolve(view, draws, _boundsOf ??= BoundsOf, EnvironmentHash, _animatedLights);
        if (bakes.SpaceFresh && !LoadSpaceBake(view.BakedSpace!.Value.Name, bakes.Space!.Value))
        {
            // Its file is now unreadable, so the space is worked out again as unbaked: its Baked lights come back.
            _namedChunks.Clear();
            bakes = _bakeResolver.Resolve(view, draws, _boundsOf, EnvironmentHash, _animatedLights);
        }

        var spaceRects = bakes.SpaceFresh && _loadedBake.Rects is { Count: > 0 } rects ? rects : null;
        _spaceLightmapsActive = spaceRects is not null;
        _activeLightmaps = spaceRects;
        _chunkLightmapsHash = ChunkLightmapsHash();
        FindRelitChunks(view, draws.Length, bakes);
        _activeBakedProbes = bakes.SpaceFresh ? _loadedBake.Probes : null;
        if (spaceRects is not null || _relightChunks)
        {
            FindMaskSlots(view, bakes, spaceRects is not null);
        }

        LastBakeFrame = bakes;
        _spaceFade = spaceRects is not null || _activeBakedProbes is not null ? bakes.SpaceFade : 1f;
        _bakesFading = _spaceFade < 1f || _chunksFading;
        if (bakes.DroppedSuns.Count == 0 && bakes.DroppedLocalLights.Count == 0
            && bakes.FadingSuns.Count == 0 && bakes.FadingLocalLights.Count == 0)
        {
            return false;
        }

        // A Baked light whose bake is still fading in stays, dimmed as the bake comes, so the two always add up.
        lit = view;
        lit.Lights = BakeFreshness.Without(
            view.Lights, bakes.DroppedSuns, bakes.FadingSuns, DimSun, ref _bakeKeptSuns);
        lit.LocalLights = BakeFreshness.Without(
            view.LocalLights, bakes.DroppedLocalLights, bakes.FadingLocalLights, DimLocalLight, ref _bakeKeptLights);
        return true;
    }

    /// <summary>
    /// The resolver's question for a chunk whose bake is fresh in the store: is every page of it on the GPU? A store
    /// with no Load of its own has no lightmaps to wait for, so its fresh chunks count at once. Otherwise the pool is
    /// asked, which starts reading the bake the first time, and a resident bake joins the frame's named chunks.
    /// </summary>
    private bool ChunkResident(string file, BakeInputs inputs)
    {
        var store = _readableBakes.Inner;
        if (!GivesLightmaps(store))
        {
            return true;
        }

        if (_chunkPool.Use(file, store) is not { } entry)
        {
            return false;
        }

        if (!_namedChunks.Contains(entry))
        {
            _namedChunks.Add(entry);
        }

        return true;
    }

    /// <summary>
    /// Works out which placed chunks this frame relights by its sky and suns: each fresh one whose bake is on the GPU
    /// with its transfer pages. Fills their GpuChunk data, the suns' coefficients, and the chunk each draw is relit as,
    /// from the resolver's owners. A frame with none leaves <see cref="_relightChunks"/> off and is drawn as before. See
    /// "Chunk transfer" in docs/design.md.
    /// </summary>
    private void FindRelitChunks(in FrameView view, int drawCount, BakeFrame bakes)
    {
        _relightChunks = false;
        _chunksFading = false;
        _chunkCount = 0;
        var chunks = view.BakedChunks;
        var store = _readableBakes.Inner;
        if (chunks.IsEmpty || !GivesLightmaps(store))
        {
            return;
        }

        for (int c = 0; c < chunks.Length; c++)
        {
            _relitEntries[c] = null;
            _chunkLightmaps[c] = null;
            // A chunk not relit keeps last frame's _chunkData, which is harmless: its draws get chunk -1, so no shader reads it.
            if (!bakes.ChunkFresh[c] || _chunkPool.Use(bakes.ChunkFile(c), store) is not { IsResident: true } entry
                || entry.Transfer[0] < 0)
            {
                continue;
            }

            _relitEntries[c] = entry;
            _chunkLightmaps[c] = entry.Rects;
            _relightChunks = true;
            _chunksFading |= bakes.ChunkFade(c) < 1f;
            var m = chunks[c].Transform;
            var t = m.Translation;
            var transfer = entry.Transfer;
            _chunkData[c] = new GpuChunk
            {
                // A chunk only turns and moves, so the way back is the turn's transpose after taking the move off.
                IntoX = new Vector4(m.M11, m.M12, m.M13, -(t.X * m.M11 + t.Y * m.M12 + t.Z * m.M13)),
                IntoY = new Vector4(m.M21, m.M22, m.M23, -(t.X * m.M21 + t.Y * m.M22 + t.Z * m.M23)),
                IntoZ = new Vector4(m.M31, m.M32, m.M33, -(t.X * m.M31 + t.Y * m.M32 + t.Z * m.M33)),
                Min = new Vector4(chunks[c].Min, chunks[c].EdgeBlend),
                Max = new Vector4(chunks[c].Max, bakes.ChunkFade(c)),
                // No slot reads the mask until FindMaskSlots gives the chunk's lights theirs.
                Mask0 = -1, Mask1 = -1, Mask2 = -1, Mask3 = -1,
                SkyRed = transfer[0], SkyGreen = transfer[1], SkyBlue = transfer[2], Sun = transfer[3],
                Tint = transfer[4],
                MaskPage = entry.MaskPage,
            };
        }

        if (!_relightChunks)
        {
            return;
        }

        _chunkCount = chunks.Length;
        if (_drawChunks.Length < drawCount)
        {
            _drawChunks = new int[drawCount * 2];
        }

        var owners = _bakeResolver.Owners;
        for (int d = 0; d < drawCount; d++)
        {
            int owner = d < owners.Length ? owners[d] : BakeAreas.None;
            _drawChunks[d] = owner >= 0 && _relitEntries[owner] is not null ? owner : -1;
        }

        // Every sun a bake's bounce can follow, as deltas: each one's radiance times the basis towards it.
        _sunSh.AsSpan().Clear();
        foreach (var sun in view.Lights)
        {
            if (sun.Mode == LightMode.Realtime)
            {
                continue;
            }

            var towards = Vector3.Normalize(sun.Direction);
            var radiance = new Vector4(sun.Radiance, 0f);
            _sunSh[0] += ShL1.Y0 * radiance;
            _sunSh[1] += ShL1.Y1 * towards.Y * radiance;
            _sunSh[2] += ShL1.Y1 * towards.Z * radiance;
            _sunSh[3] += ShL1.Y1 * towards.X * radiance;
        }
    }

    /// <summary>
    /// A hash of the resident chunk bakes this frame names, 0 for none, so the reflection probes see one come or go.
    /// A file's name is its chunk's name and inputs, so its hash says which bake it is.
    /// </summary>
    private ulong ChunkLightmapsHash()
    {
        if (_namedChunks.Count == 0)
        {
            return 0;
        }

        ulong hash = 0;
        foreach (var chunk in _namedChunks)
        {
            hash = hash * 31 + (ulong)StringComparer.Ordinal.GetHashCode(chunk.File);
        }

        return hash == 0 ? 1 : hash;
    }

    // A chunk bake the pool couldn't read, check or put on the GPU: reported once, and missing until the store reloads.
    private void ChunkUnreadable(string file, string problem, string message)
    {
        Console.Error.WriteLine($"The bake {file} {problem}, so its area is lit live: {message}");
        _readableBakes.Unreadable.Add(file);
    }

    /// <summary>
    /// Gives each of the frame's Mixed lights with a channel in its own bake one of the frame's four mask slots, with
    /// <see cref="MaskSlots.Assign"/>. The space's first Mixed sun keeps slot 0, as
    /// <see cref="ShadowMaskChannels.Assign"/> gave it at bake time, and the space's local lights keep their channels.
    /// Each relit chunk's Mixed lights, found in its bake by their chunk-space hash, take the slots left, and the
    /// chunk's table says which of its channels each slot is. A light only counts while it is still Mixed in its bake,
    /// so one turned Realtime never reads it. See "Shadow mask channels" in docs/design.md.
    /// </summary>
    private void FindMaskSlots(in FrameView view, BakeFrame bakes, bool spaceDrawn)
    {
        int spaceCount = 0;
        int chunkCount = 0;
        var locals = view.LocalLights;
        if (_slotSpaceLights.Length < locals.Length)
        {
            _slotSpaceLights = new (int, int)[locals.Length];
            _slotChunkLights = new (int, int, Vector3, float)[locals.Length];
            _slotChunkOf = new (int, int)[locals.Length];
            _slots = new int[2 * locals.Length];
        }

        if (spaceDrawn)
        {
            for (int i = 0; i < view.Lights.Length; i++)
            {
                if (bakes.ModeOf(new LightKey(true, i)) == LightMode.Mixed)
                {
                    // The suns drawn leave out the Baked ones, so this one's index moves down past those before it.
                    // Indexed, since a foreach over the IReadOnlyList would box its enumerator every frame.
                    int dropped = 0;
                    var droppedSuns = bakes.DroppedSuns;
                    for (int d = 0; d < droppedSuns.Count; d++)
                    {
                        if (droppedSuns[d] < i)
                        {
                            dropped++;
                        }
                    }

                    _maskSun = i - dropped;
                    break;
                }
            }

            if (_loadedBake.Channels is { } channels)
            {
                foreach (var (key, channel) in channels)
                {
                    if (spaceCount < _slotSpaceLights.Length && bakes.ModeOf(new LightKey(false, key)) == LightMode.Mixed)
                    {
                        _slotSpaceLights[spaceCount++] = (key, channel);
                    }
                }
            }
        }

        if (_relightChunks)
        {
            var owners = _bakeResolver.LightOwners;
            var hashes = _bakeResolver.LightChunkHashes;
            for (int i = 0; i < owners.Length && i < locals.Length; i++)
            {
                int c = owners[i];
                if (c < 0 || _relitEntries[c] is not { } entry
                    || bakes.ModeOf(new LightKey(false, locals[i].Key)) != LightMode.Mixed)
                {
                    continue;
                }

                foreach (var (hash, channel) in entry.Channels)
                {
                    if (hash == hashes[i])
                    {
                        _slotChunkLights[chunkCount] = (locals[i].Key, c, locals[i].Position, locals[i].Range);
                        _slotChunkOf[chunkCount] = (c, channel);
                        chunkCount++;
                        break;
                    }
                }
            }
        }

        var slots = _slots.AsSpan(0, spaceCount + chunkCount);
        MaskSlots.Assign(_maskSun >= 0 ? 0 : -1, _slotSpaceLights.AsSpan(0, spaceCount),
            _slotChunkLights.AsSpan(0, chunkCount), slots, bakes.Unslotted);

        for (int i = 0; i < spaceCount; i++)
        {
            _maskChannels[_slotSpaceLights[i].Key] = slots[i];
        }

        for (int i = 0; i < chunkCount; i++)
        {
            int slot = slots[spaceCount + i];
            if (slot < 0)
            {
                continue;
            }

            var (c, channel) = _slotChunkOf[i];
            _maskChannels[_slotChunkLights[i].Key] = slot;
            SetMaskSlot(ref _chunkData[c], slot, channel);
            _spaceSlots[slot] = -1;
        }
    }

    // Says that the frame's mask slot is the chunk bake's channel.
    private static void SetMaskSlot(ref GpuChunk chunk, int slot, int channel)
    {
        switch (slot)
        {
            case 0:
                chunk.Mask0 = channel;
                break;
            case 1:
                chunk.Mask1 = channel;
                break;
            case 2:
                chunk.Mask2 = channel;
                break;
            default:
                chunk.Mask3 = channel;
                break;
        }
    }

    // How long one submit of the path tracer may run, below Windows' two-second GPU timeout with room to spare.
    private static readonly TimeSpan BakeBatchBudget = TimeSpan.FromMilliseconds(80);

    private Baker? _baker;

    /// <summary>
    /// Makes what a bake needs, the baker and, unless <paramref name="probes"/> is false, the probe volume a space's
    /// probes fold through, so <see cref="CreateSpaceJob"/> and <see cref="CreateChunkJob"/> can start a bake without
    /// waiting for the GPU. Waits for the GPU the first time; does nothing once it has run, or when the GPU can't trace
    /// rays. Call it outside a frame. <see cref="Bake"/> and <see cref="BakeChunk"/> call it themselves; a chunk has no
    /// probes, so <see cref="BakeChunk"/> passes false and works on a GPU the probe fold can't run on.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The GPU can trace rays but lacks the storage image formats a bake needs, or, with <paramref name="probes"/>, what
    /// the live probes fold with.
    /// </exception>
    internal void PrepareBaking(bool probes = true)
    {
        if (_tracing is null)
        {
            return;
        }

        _baker ??= new Baker(_context, this);
        if (probes)
        {
            _baker.EnsureProbeVolume(_environment.Sky, _environment.Sampler);
        }
    }

    // The baker PrepareBaking made, and its probe volume when the bake has probes. A job never makes them itself,
    // since that waits for the GPU.
    private (Baker Baker, ProbeVolume? Volume) PreparedBaker(string name, bool probes)
    {
        if (_tracing is null)
        {
            throw new InvalidOperationException("Baking needs a GPU that can trace rays, and this one can't.");
        }

        if (_baker is not { } baker || (probes && baker.ProbeVolume is null))
        {
            throw new InvalidOperationException(
                $"The bake of \"{name}\" can't start yet: baking isn't prepared, and preparing it waits for the GPU, which a bake job never does. Call PrepareBaking first, outside a frame.");
        }

        return (baker, baker.ProbeVolume);
    }

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
    public byte[] Bake(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings)
    {
        var (space, probeSettings) = CheckSpace(view, settings);
        long started = Stopwatch.GetTimestamp();
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        _api.DeviceWaitIdle(_context.Device).Check();
        DropBackgroundBakes();
        _uploads.Flush();
        BakeSky(view);
        PrepareBaking();
        using var job = SpaceJob(view, draws, settings, space, probeSettings, started);
        return RunJob(job);
    }

    /// <summary>
    /// The bake <see cref="Bake"/> makes, as a job to step, without waiting for the GPU: it records nothing until its
    /// first step. The frame's sky and uploads are taken as they are, so call it after a frame has drawn this view.
    /// The draws and lights are copied, so the job doesn't need the frame's spans. Call <see cref="PrepareBaking"/>
    /// first.
    /// </summary>
    /// <exception cref="InvalidOperationException">As <see cref="Bake"/>, or baking isn't prepared.</exception>
    /// <exception cref="ArgumentException">As <see cref="Bake"/>.</exception>
    internal BakeJob CreateSpaceJob(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings)
    {
        var (space, probeSettings) = CheckSpace(view, settings);
        long started = Stopwatch.GetTimestamp();
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        return SpaceJob(view, draws, settings, space, probeSettings, started);
    }

    // Steps a job to the end, a submit at a time, with as many passes in each as fit the batch budget, then makes its
    // file.
    private byte[] RunJob(BakeJob job)
    {
        int batch = 1;
        while (true)
        {
            var result = BakeStepResult.Continue;
            long stepStarted = Stopwatch.GetTimestamp();
            _context.SubmitAndWait(cmd => result = job.Step(cmd, batch));
            if (result == BakeStepResult.Done)
            {
                return job.Finish();
            }

            // As many passes as fit the budget at the rate this step ran, never more than double at once. A step that
            // traced nothing starts the next stage's passes over from one, since its passes may cost something else.
            int count = job.PassesInLastStep;
            if (count == 0)
            {
                batch = 1;
                continue;
            }

            double perPass = Math.Max(Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds / count, 1e-3);
            batch = Math.Clamp((int)(BakeBatchBudget.TotalMilliseconds / perPass), 1, Math.Max(count * 2, 1));
        }
    }

    // What the renderer bakes by itself, and the queue that picks what is next. See "Background bakes" in
    // docs/design.md.
    private AutoBake? _autoBake;
    private BakeQueue? _bakeQueue;

    // Set when PrepareBaking refused for the AutoBake set, so frames bake nothing until another is set.
    private bool _autoBakeUnavailable;

    // This frame's wants, grown when a frame names more areas.
    private BakeWant[] _bakeWants = new BakeWant[16];

    // The running background bake: what the queue started, at which quality. Kept while its file is made on the
    // thread pool, so no other bake sets up on the baker's pages before it lets them go.
    private BakeJob? _bakeJob;
    private BakeWant _bakeJobWant;
    private BakeQuality _bakeJobQuality;
    private FinishingBake? _bakeFinishing;

    // Finishes on the thread pool, until the render thread takes their results; and those results.
    private readonly List<FinishingBake> _bakeFinishes = [];
    private readonly ConcurrentQueue<FinishingBake> _bakesFinished = new();

    // Stopped jobs whose steps a frame in flight may still run, freed with the frame slot they were queued against.
    private readonly HashSet<BakeJob> _retiringBakes = [];

    // How many passes the next trace slice records, and which run of measurements may change it: a step that traces
    // nothing starts the count over, and a slice measured from before then says nothing about the passes now.
    private int _bakePasses = 1;
    private int _bakePassEpoch;

    // Frames submitted, and the count after which a step that copied results back has landed.
    private long _framesSubmitted;
    private long _bakeAwaitUntil;

    // Each frame slot's slice: whether it was timed, its passes and its epoch.
    private readonly GpuTimer _bakeTimer;
    private readonly bool[] _sliceTimed = new bool[FramesInFlight];
    private readonly int[] _slicePasses = new int[FramesInFlight];
    private readonly int[] _sliceEpoch = new int[FramesInFlight];
    private float _lastSliceMilliseconds;

    // Areas whose bake failed, each reported once.
    private readonly HashSet<string> _bakeFailuresLogged = new(StringComparer.Ordinal);

    /// <summary>A background bake whose file is being made on the thread pool.</summary>
    private sealed class FinishingBake(BakeJob job, BakeWant want, BakeQuality quality)
    {
        public readonly BakeJob Job = job;
        public readonly BakeWant Want = want;
        public readonly BakeQuality Quality = quality;
        public Task? Task;
        public byte[]? Bytes;
        public Exception? Error;

        // Set on the render thread when the bake was stopped while finishing: its file is dropped.
        public bool Cancelled;
    }

    /// <summary>A stopped job, freed once the frame slot it was queued against is done.</summary>
    private sealed class RetiredBake(Renderer renderer, BakeJob job) : IDisposable
    {
        public void Dispose()
        {
            renderer._retiringBakes.Remove(job);
            job.Dispose();
        }
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
        get => _autoBake;
        set
        {
            StopBakeJob();
            _autoBake = value;
            _bakeQueue = value is null ? null : new BakeQueue(value.Qualities);
            _autoBakeUnavailable = false;
            if (value is null || _tracing is null)
            {
                return;
            }

            try
            {
                PrepareBaking(probes: value.Spaces);
            }
            catch (InvalidOperationException e)
            {
                _autoBakeUnavailable = true;
                Console.Error.WriteLine($"Nothing is baked in the background: {e.Message}");
            }
        }
    }

    /// <summary>
    /// How the background bake is getting on, or null when none runs and no area waits for one. While areas wait and
    /// none runs yet, its name is empty and its fraction 0. See <see cref="AutoBake"/>.
    /// </summary>
    public BakeProgress? BakeProgress => _bakeJob is { } job
        ? new BakeProgress(job.Name, _bakeFinishing is not null ? 1f : job.Progress, _bakeQueue?.Waiting ?? 0,
            _lastSliceMilliseconds)
        : _bakeQueue is { Waiting: > 0 } queue
            ? new BakeProgress("", 0f, queue.Waiting, _lastSliceMilliseconds)
            : null;

    /// <summary>How many background bake slices have been measured on the GPU, for tests.</summary>
    internal int BakeSlicesMeasured { get; private set; }

    /// <summary>How many bake jobs are made and not yet disposed, for tests.</summary>
    internal int LiveBakeJobs => _baker?.JobPools ?? 0;

    /// <summary>The passes the next trace slice of the background bake records, for tests.</summary>
    internal int BakeSlicePasses => _bakePasses;

    /// <summary>Hands the files finished on the thread pool to <see cref="AutoBake"/>'s Done, on the render thread.</summary>
    private void TakeFinishedBakes()
    {
        while (_bakesFinished.TryDequeue(out var finished))
        {
            _bakeFinishes.Remove(finished);

            // Every step ran before the job was done, so the GPU is finished with it.
            finished.Job.Dispose();
            if (finished.Cancelled)
            {
                continue;
            }

            _bakeJob = null;
            _bakeFinishing = null;
            var want = finished.Want;
            _bakeQueue?.Finished(want.File, want.Inputs, finished.Quality);
            if (finished.Error is { } error)
            {
                ReportBakeFailure(want, error);
                continue;
            }

            _autoBake?.Done(new BakeDone(want.File, want.Name, finished.Job.Inputs, finished.Quality, finished.Bytes!));
        }
    }

    /// <summary>
    /// Tells the queue which areas this frame names whose bake is missing or stale, stops the running bake when its
    /// area is named with other inputs, and makes the job the queue starts. Runs once a frame, from the frame as
    /// drawn with every light, after <see cref="ResolveBakes"/>.
    /// </summary>
    private void UpdateBackgroundBake(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (_autoBake is not { } auto || _bakeQueue is not { } queue || _autoBakeUnavailable || _tracing is null)
        {
            return;
        }

        int count = 0;
        bool cancel = false;
        if (LastBakeFrame is { } bakes && _animatedLights.Settled)
        {
            var store = _readableBakes.Inner;
            if (auto.Spaces && view.BakedSpace is { } space && bakes.Space is { } spaceInputs)
            {
                string file = _bakeResolver.SpaceFile(space.Name);
                cancel |= queue.Named(file, spaceInputs);
                if (queue.Wants(store, file, spaceInputs))
                {
                    AddBakeWant(ref count, new BakeWant(file, space.Name, spaceInputs, -1, (space.Min + space.Max) * 0.5f));
                }
            }

            var chunks = view.BakedChunks;
            for (int c = 0; c < chunks.Length && c < bakes.Chunks.Count; c++)
            {
                if (bakes.ChunkPending(c) || bakes.ChunkStaticDraws(c) == 0)
                {
                    continue;
                }

                var inputs = bakes.Chunks[c];
                string file = bakes.ChunkFile(c);
                cancel |= queue.Named(file, inputs);
                if (queue.Wants(store, file, inputs))
                {
                    var chunk = chunks[c];
                    var centre = Vector3.Transform((chunk.Min + chunk.Max) * 0.5f, chunk.Transform);
                    AddBakeWant(ref count, new BakeWant(file, chunk.Name, inputs, c, centre));
                }
            }
        }

        var start = queue.Update(_bakeWants.AsSpan(0, count), view.CameraPosition, out bool cancelled, out var quality);
        if (cancel || cancelled)
        {
            // The queue already forgot it, and may have started the next bake, so it isn't told again.
            StopBakeJob(tellQueue: false);
        }

        if (start is not { } want)
        {
            return;
        }

        // The queue starts a bake only when none runs; a job left here would never be freed.
        if (_bakeJob is not null)
        {
            StopBakeJob(tellQueue: false);
        }

        try
        {
            _bakeJob = want.Chunk < 0
                ? CreateSpaceJob(view, draws, new BakeSettings(view.BakedSpace!.Value.TexelsPerMetre, quality))
                : CreateChunkJob(view, draws, want.Chunk, new BakeSettings(view.BakedChunks[want.Chunk].TexelsPerMetre, quality));
            _bakeJobWant = want;
            _bakeJobQuality = quality;
            _bakePasses = 1;
            _bakePassEpoch++;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Never started again with these inputs, so a bake that can't be made doesn't loop.
            queue.Finished(want.File, want.Inputs, quality);
            ReportBakeFailure(want, e);
        }
    }

    private void AddBakeWant(ref int count, in BakeWant want)
    {
        if (count == _bakeWants.Length)
        {
            Array.Resize(ref _bakeWants, count * 2);
        }

        _bakeWants[count++] = want;
    }

    private void ReportBakeFailure(in BakeWant want, Exception error)
    {
        if (_bakeFailuresLogged.Add(want.File))
        {
            Console.Error.WriteLine($"The background bake of \"{want.Name}\" failed, so its area stays lit live: {error.Message}");
        }
    }

    /// <summary>
    /// Records the running background bake's next slice into the frame's command buffer, between timestamps, with as
    /// many trace passes as the last measured slice says fit the budget. Skips the frame while a step's readback is
    /// still on its way, and while the last bake still holds the baker's pages.
    /// </summary>
    private void RecordBakeSlice(CommandBuffer cmd)
    {
        _sliceTimed[_frameIndex] = false;
        if (_bakeJob is not { } job || _bakeFinishing is not null || _framesSubmitted < _bakeAwaitUntil
            || (!job.Started && _baker!.PagesClaimed))
        {
            return;
        }

        bool timed = _bakeTimer.IsSupported;
        if (timed)
        {
            _bakeTimer.Begin(cmd, _frameIndex);
        }

        BakeStepResult result;
        try
        {
            result = job.Step(cmd, _bakePasses);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // What the step recorded rides this frame, so the job goes with this frame's slot.
            var want = _bakeJobWant;
            _bakeQueue?.Finished(want.File, want.Inputs, _bakeJobQuality);
            RetireBakeJob(job, _destroyed[_frameIndex]);
            _bakeJob = null;
            ReportBakeFailure(want, e);
            return;
        }
        finally
        {
            if (timed)
            {
                _bakeTimer.Mark(cmd);
                _bakeTimer.End();
            }
        }

        int passes = job.PassesInLastStep;
        if (passes == 0)
        {
            // The next stage's passes may cost something else, so they start over from one.
            _bakePasses = 1;
            _bakePassEpoch++;
        }

        _sliceTimed[_frameIndex] = timed;
        _slicePasses[_frameIndex] = passes;
        _sliceEpoch[_frameIndex] = _bakePassEpoch;
        switch (result)
        {
            case BakeStepResult.AwaitGpu:
                // This frame is the next submitted; its fence has passed once as many more frames have been.
                _bakeAwaitUntil = _framesSubmitted + FramesInFlight;
                break;
            case BakeStepResult.Done:
                StartFinishingBake(job);
                break;
        }
    }

    /// <summary>
    /// Reads the slice timed in this frame slot, whose fence has passed, and sizes the next trace slice from it: as
    /// many passes as fit the budget at the rate it ran, never more than double.
    /// </summary>
    private void MeasureBakeSlice()
    {
        if (!_sliceTimed[_frameIndex])
        {
            return;
        }

        _sliceTimed[_frameIndex] = false;
        if (!_bakeTimer.Collect(_frameIndex))
        {
            return;
        }

        float milliseconds = (float)_bakeTimer.Times[0].Milliseconds;
        _lastSliceMilliseconds = milliseconds;
        BakeSlicesMeasured++;
        int passes = _slicePasses[_frameIndex];
        if (passes == 0 || _sliceEpoch[_frameIndex] != _bakePassEpoch || _autoBake is not { } auto)
        {
            return;
        }

        double perPass = Math.Max(milliseconds / passes, 1e-4);
        _bakePasses = (int)Math.Clamp(auto.BudgetMilliseconds / perPass, 1, Math.Max(passes * 2, 1));
    }

    // Makes the finished job's file on the thread pool, since encoding the pages takes longer than a frame.
    private void StartFinishingBake(BakeJob job)
    {
        var finishing = new FinishingBake(job, _bakeJobWant, _bakeJobQuality);
        _bakeFinishing = finishing;
        _bakeFinishes.Add(finishing);
        finishing.Task = Task.Run(() =>
        {
            try
            {
                finishing.Bytes = job.Finish();
            }
            catch (Exception e)
            {
                finishing.Error = e;
            }

            _bakesFinished.Enqueue(finishing);
        });
    }

    /// <summary>
    /// Stops the running background bake, if any, without finishing it; the queue starts its area again while a frame
    /// names it. Steps already submitted may still run, so the job is freed with the last submitted frame. Pass
    /// <paramref name="tellQueue"/> false when the queue itself called the bake off, since it may already have started
    /// the next one, which telling it would end too.
    /// </summary>
    private void StopBakeJob(bool tellQueue = true)
    {
        if (_bakeJob is not { } job)
        {
            return;
        }

        if (_bakeFinishing is { } finishing)
        {
            // Its file is being made; it is dropped, and the job freed when the thread pool is done with it.
            finishing.Cancelled = true;
            _bakeFinishing = null;
        }
        else
        {
            RetireBakeJob(job, Destroying());
        }

        _bakeJob = null;
        if (tellQueue)
        {
            _bakeQueue?.Stopped();
        }
    }

    // A job that never stepped holds nothing the GPU reads, so it goes at once.
    private void RetireBakeJob(BakeJob job, DestroyedSlot slot)
    {
        if (!job.Started)
        {
            job.Dispose();
            return;
        }

        _retiringBakes.Add(job);
        slot.Others.Add(new RetiredBake(this, job));
    }

    /// <summary>
    /// Frees every background bake once the GPU is idle, for a whole bake, which needs the baker's pages, and for
    /// <see cref="Dispose"/>: waits for the files being made, drops them, and stops the running bake.
    /// </summary>
    private void DropBackgroundBakes()
    {
        StopBakeJob();
        foreach (var finishing in _bakeFinishes)
        {
            finishing.Cancelled = true;
            finishing.Task?.Wait();
        }

        while (_bakesFinished.TryDequeue(out var finished))
        {
            finished.Job.Dispose();
        }

        _bakeFinishes.Clear();
        foreach (var job in _retiringBakes)
        {
            // Disposing twice is harmless, so the slot's later release changes nothing.
            job.Dispose();
        }

        _retiringBakes.Clear();
    }

    // The checks every bake of the view's space makes before it starts.
    private static (BakedSpace Space, ProbeVolumeSettings Probes) CheckSpace(in FrameView view, BakeSettings settings)
    {
        if (view.BakedSpace is not { } space)
        {
            throw new InvalidOperationException("Bake needs a baked space in the frame; call view.BakedLighting first.");
        }

        if (settings.TexelsPerMetre != space.TexelsPerMetre)
        {
            throw new ArgumentException(
                $"The baked space \"{space.Name}\" is lit at {space.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, so it can't be baked at {settings.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)}: no frame would find that bake fresh.",
                nameof(settings));
        }

        // The space's probes, laid out as the live probes are, so the runtime copies their tiles straight in.
        var probeSettings = new ProbeVolumeSettings(space.Min, space.Max, space.ProbeSpacing);
        long probeCount = ProbeGrid.Total(probeSettings);
        if (probeCount > BakeFiles.MaxSpaceProbes)
        {
            throw new InvalidOperationException(
                $"The baked space \"{space.Name}\" holds {probeCount.ToString(CultureInfo.InvariantCulture)} probes, but a bake holds at most {BakeFiles.MaxSpaceProbes.ToString(CultureInfo.InvariantCulture)}.");
        }

        return (space, probeSettings);
    }

    // Resolves the space's inputs, lays out its draws and takes its lights, and makes the job that bakes them. Never
    // waits: the baker and its probe volume come from PrepareBaking.
    private BakeJob SpaceJob(
        in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings, BakedSpace space,
        ProbeVolumeSettings probeSettings, long started)
    {
        // A resolver of its own, so the frame's resolver keeps what the last frame drew. It still tells the tracker
        // where each light is, so a light that moved between frames is known to be moving.
        var inputs = new BakeResolver(NoBakes.Instance)
            .Resolve(view, draws, _boundsOf ??= BoundsOf, EnvironmentHash, _animatedLights).Space!.Value;

        var chunks = view.BakedChunks;
        var owned = new List<DrawItem>();
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0
                && BakeAreas.OwnerOfDraw(BakeAreas.Transform(BoundsOf(draw.Detail), draw.Transform), space, chunks) == BakeAreas.Space)
            {
                owned.Add(draw);
            }
        }

        var layout = LightmapLayout.Build(CollectionsMarshal.AsSpan(owned), AreaOf, settings, GetsLightmap);
        if (layout.Left.Count > 0)
        {
            throw new InvalidOperationException(
                $"{layout.Left.Count.ToString(CultureInfo.InvariantCulture)} of the {owned.Count.ToString(CultureInfo.InvariantCulture)} static draws in the baked space \"{space.Name}\" don't fit in {LightmapLayout.MaxPages.ToString(CultureInfo.InvariantCulture)} lightmap pages of {LightmapLayout.PageSize.ToString(CultureInfo.InvariantCulture)} texels, so they would get no baked light. Use fewer than {settings.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, or bake a smaller space.");
        }

        // The lights the bake takes: Mixed and Baked ones that hold still, a local light only in the space's box.
        var suns = view.Lights;
        var locals = view.LocalLights;
        var lights = new List<GpuBakeLight>();
        var sunIndex = new int[suns.Length];
        var localIndex = new int[locals.Length];
        var sunsForMask = suns.ToArray();
        var localsForMask = locals.ToArray();
        for (int i = 0; i < suns.Length; i++)
        {
            sunIndex[i] = -1;
            if (suns[i].Mode != LightMode.Realtime && !_animatedLights.IsAnimated(new LightKey(true, i)))
            {
                sunIndex[i] = lights.Count;
                lights.Add(GpuBakeLight.Sun(suns[i], view.Shadows.LightAngle));
            }
            else
            {
                sunsForMask[i].Mode = LightMode.Realtime;
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            localIndex[i] = -1;
            if (locals[i].Mode != LightMode.Realtime && !_animatedLights.IsAnimated(new LightKey(false, locals[i].Key))
                && BakeAreas.OwnerOfLight(locals[i].Position, space, chunks) == BakeAreas.Space)
            {
                localIndex[i] = lights.Count;
                lights.Add(GpuBakeLight.Local(locals[i]));
            }
            else
            {
                localsForMask[i].Mode = LightMode.Realtime;
            }
        }

        // Each Mixed light is measured in its mask channel. Local lights that don't overlap can share one, so the channel
        // goes on each light rather than each channel naming one light. The sun's channel 0 is implied, so only local
        // lights are listed.
        var sunChannels = new int[suns.Length];
        var localChannels = new int[locals.Length];
        var unassigned = new List<int>();
        ShadowMaskChannels.Assign(space, sunsForMask, localsForMask, sunChannels, localChannels, unassigned);
        var bakeLights = CollectionsMarshal.AsSpan(lights);
        var channels = new List<(int LightKey, int Channel)>();
        for (int i = 0; i < suns.Length; i++)
        {
            if (sunChannels[i] >= 0)
            {
                bakeLights[sunIndex[i]].MaskChannel = sunChannels[i];
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            if (localChannels[i] >= 0)
            {
                bakeLights[localIndex[i]].MaskChannel = localChannels[i];
                channels.Add((locals[i].Key, localChannels[i]));
            }
        }

        var (baker, volume) = PreparedBaker(space.Name, probes: true);
        var area = new AreaJob(draws.ToArray(), bakeLights.ToArray(), false, layout, view.EnvironmentIntensity, true,
            probeSettings);
        var skipped = unassigned.ToArray();
        var quality = settings.Quality;
        return new BakeJob(baker, _context, BakeFiles.Space(space.Name), space.Name, inputs, settings, area,
            _environment.Sky, _environment.Sampler, volume,
            result =>
            {
                var report = new LightBakeReport(skipped, result.InvalidShare, result.Passes, Stopwatch.GetElapsedTime(started));
                return new LightBakeFile(inputs, quality, layout.Draws, result.Irradiance, result.Direction, result.Mask,
                    result.ProbeIrradiance, result.ProbeDepth, probeSettings, channels, report);
            });
    }

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
    public byte[] BakeChunk(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings)
    {
        CheckChunk(view, chunk, settings);
        long started = Stopwatch.GetTimestamp();
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        var resolved = ResolveChunk(view, draws, chunk);
        _api.DeviceWaitIdle(_context.Device).Check();
        DropBackgroundBakes();
        _uploads.Flush();
        BakeSky(view);
        PrepareBaking(probes: false);
        using var job = ChunkJob(view, draws, chunk, settings, resolved, started);
        return RunJob(job);
    }

    /// <summary>
    /// The bake <see cref="BakeChunk"/> makes, as a job to step, without waiting for the GPU: it records nothing until
    /// its first step. The frame's sky and uploads are taken as they are, so call it after a frame has drawn this view.
    /// The draws and lights are copied, so the job doesn't need the frame's spans. Call <see cref="PrepareBaking"/>
    /// first.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">As <see cref="BakeChunk"/>.</exception>
    /// <exception cref="ArgumentException">As <see cref="BakeChunk"/>.</exception>
    /// <exception cref="InvalidOperationException">As <see cref="BakeChunk"/>, or baking isn't prepared.</exception>
    internal BakeJob CreateChunkJob(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings)
    {
        CheckChunk(view, chunk, settings);
        long started = Stopwatch.GetTimestamp();
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        var resolved = ResolveChunk(view, draws, chunk);
        return ChunkJob(view, draws, chunk, settings, resolved, started);
    }

    // The checks every bake of a chunk makes before it starts.
    private static void CheckChunk(in FrameView view, int chunk, BakeSettings settings)
    {
        var chunks = view.BakedChunks;
        if (chunk < 0 || chunk >= chunks.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk), chunk, string.Create(CultureInfo.InvariantCulture,
                $"The frame has {chunks.Length} baked {(chunks.Length == 1 ? "chunk" : "chunks")}, so chunk {chunk} doesn't exist."));
        }

        var baked = chunks[chunk];
        if (settings.TexelsPerMetre != baked.TexelsPerMetre)
        {
            throw new ArgumentException(
                $"The baked chunk \"{baked.Name}\" is lit at {baked.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, so it can't be baked at {settings.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)}: no frame would find that bake fresh.",
                nameof(settings));
        }
    }

    // A chunk's inputs, and which draws are its, with the same resolver the space's bake uses, so the chunk takes exactly
    // the draws its inputs name.
    private (BakeInputs Inputs, BakeResolver Resolver, HashSet<ulong> Ids) ResolveChunk(
        in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk)
    {
        var resolver = new BakeResolver(NoBakes.Instance);
        var inputs = resolver.Resolve(view, draws, _boundsOf ??= BoundsOf, EnvironmentHash, _animatedLights)
            .ChunkInputs[chunk];
        var ids = new HashSet<ulong>(resolver.ChunkIds(chunk));
        if (ids.Count == 0)
        {
            throw new InvalidOperationException(
                $"The baked chunk \"{view.BakedChunks[chunk].Name}\" owns no static draws, so there is nothing to bake. A static draw belongs to the chunk whose box holds its centre, unless the baked space's box touches it.");
        }

        return (inputs, resolver, ids);
    }

    // Moves the chunk's draws and lights into its space, lays them out on its page, and makes the job that bakes them.
    // Never waits: the baker comes from PrepareBaking.
    private BakeJob ChunkJob(
        in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings,
        (BakeInputs Inputs, BakeResolver Resolver, HashSet<ulong> Ids) resolved, long started)
    {
        var chunks = view.BakedChunks;
        var baked = chunks[chunk];
        var (inputs, resolver, ids) = resolved;

        // The chunk's draws, in its own space. The rest of the world neither blocks nor bounces its light. Only this
        // copy's: every copy of a chunk has the same ids, so an id alone would take the other copies' draws too.
        var owners = resolver.Owners;
        var owned = new List<DrawItem>();
        bool glows = false;
        for (int d = 0; d < draws.Length; d++)
        {
            ref readonly var draw = ref draws[d];
            if (d < owners.Length && owners[d] == chunk && draw.BakeId != 0 && ids.Contains(draw.BakeId))
            {
                var moved = draw;
                moved.Transform = BakeAreas.Relative(draw.Transform, baked.Transform);
                owned.Add(moved);
                glows |= _resources.Glows(draw.Material);
            }
        }

        var layout = LightmapLayout.BuildSingle(CollectionsMarshal.AsSpan(owned), AreaOf, baked.TexelsPerMetre, GetsLightmap);
        if (layout.Left.Count > 0)
        {
            throw new InvalidOperationException(
                $"{layout.Left.Count.ToString(CultureInfo.InvariantCulture)} of the {owned.Count.ToString(CultureInfo.InvariantCulture)} static draws in the baked chunk \"{baked.Name}\" don't fit in one lightmap page of {LightmapLayout.PageSize.ToString(CultureInfo.InvariantCulture)} texels, so they would get no baked light. Use fewer than {baked.TexelsPerMetre.ToString(CultureInfo.InvariantCulture)} texels a metre, or make the chunk smaller.");
        }

        // The chunk's own lights, Mixed and Baked ones that hold still, moved into its space. Every other light is
        // Realtime as far as the mask channels go, so only these get one.
        var locals = view.LocalLights;
        var forMask = locals.ToArray();
        var lightOf = new int[locals.Length];
        var lights = new List<GpuBakeLight>();
        for (int i = 0; i < locals.Length; i++)
        {
            lightOf[i] = -1;
            if (locals[i].Mode != LightMode.Realtime && !_animatedLights.IsAnimated(new LightKey(false, locals[i].Key))
                && BakeAreas.OwnerOfLight(locals[i].Position, view.BakedSpace, chunks) == chunk)
            {
                var light = locals[i];
                var (px, py, pz) = BakeAreas.IntoChunk(baked.Transform, light.Position.X, light.Position.Y, light.Position.Z, 1.0);
                var (dx, dy, dz) = BakeAreas.IntoChunk(baked.Transform, light.Direction.X, light.Direction.Y, light.Direction.Z, 0.0);
                light.Position = new Vector3((float)px, (float)py, (float)pz);
                light.Direction = new Vector3((float)dx, (float)dy, (float)dz);
                lightOf[i] = lights.Count;
                lights.Add(GpuBakeLight.Local(light));
            }
            else
            {
                forMask[i].Mode = LightMode.Realtime;
            }
        }

        var maskChannels = new int[locals.Length];
        var unassigned = new List<int>();
        ShadowMaskChannels.AssignChunk(baked, forMask, maskChannels, unassigned);
        var bakeLights = lights.ToArray();
        var chunkChannels = new List<(ulong LightHash, int Channel)>();
        for (int i = 0; i < locals.Length; i++)
        {
            if (maskChannels[i] >= 0 && lightOf[i] >= 0)
            {
                bakeLights[lightOf[i]].MaskChannel = maskChannels[i];
                chunkChannels.Add((BakeInputHash.Light(locals[i], baked.Transform), maskChannels[i]));
            }
        }

        // No sky and no sun in the plain light: the transfer carries them, for whatever sky the chunk is placed under.
        bool plainLight = lights.Count > 0 || glows;
        var (baker, _) = PreparedBaker(baked.Name, probes: false);
        var area = new AreaJob(owned.ToArray(), bakeLights, true, layout, 0f, plainLight, null);
        var skipped = unassigned.ToArray();
        var quality = settings.Quality;
        return new BakeJob(baker, _context, BakeFiles.Chunk(baked.Name, inputs.Combined), baked.Name, inputs, settings,
            area, _environment.Sky, _environment.Sampler, null,
            result =>
            {
                var report = new LightBakeReport(skipped, result.InvalidShare, result.Passes, Stopwatch.GetElapsedTime(started));
                return new LightBakeFile(inputs, quality, layout.Draws, result.Irradiance, result.Direction, result.Mask,
                    null, null, null, [], report)
                {
                    Transfer = result.Transfer,
                    ChunkChannels = chunkChannels,
                };
            });
    }

    // Whether a material may have a lightmap: not one that sways in the wind or is see-through.
    private bool GetsLightmap(MaterialHandle material) =>
        _resources.GetWind(material) == 0f && (_resources.GetFlags(material) & MaterialFlags.Transparent) == 0;

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
    /// Bakes the environment again with the view's clear sky when it or its sun has changed. A
    /// gradient sky gives the same empty <see cref="SkySun"/> whatever the sun does, so that never bakes.
    /// </summary>
    private void BakeSky(in FrameView view)
    {
        _environmentIntensity = view.EnvironmentIntensity;
        var sky = SkyModel.Sun(view);
        if (sky == _bakedSky)
        {
            return;
        }

        _api.DeviceWaitIdle(_context.Device).Check();
        _environment.Rebake(sky);
        _bakedSky = sky;
        SkyRebakes++;
    }

    /// <summary>How many times <see cref="BakeSky"/> has baked the environment again, for tests.</summary>
    internal int SkyRebakes { get; private set; }

    /// <summary>Projects the sky cube to L1 now and reads it back, coefficient by coefficient, for tests.</summary>
    internal Vector4[] ProjectSky() => _skySh.Project();

    /// <summary>The sun's disc the last frame drawn from the camera wrote, leaving out probe faces, for tests.</summary>
    internal Vector4 LastSunDisc { get; private set; }

    /// <summary>
    /// Hands a texture's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be sampled in that same frame. <paramref name="edges"/> says what is
    /// read past the texture's edge, for every material that uses it: an atlas such as a
    /// <see cref="Material.MaskTexture"/> wants <see cref="TextureEdges.Clamp"/>.
    /// </summary>
    public TextureHandle CreateTexture(TextureData data, TextureEdges edges = TextureEdges.Repeat)
    {
        var texture = new GpuTexture(_context, data.Width, data.Height, data.Kind, (uint)data.Mips.Length, 1, format: data.Format);
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

        var texture = new GpuTexture(_context, first.Width, first.Height, first.Kind, (uint)first.Mips.Length, (uint)layers.Length, true, first.Format);
        _uploads.Add(texture.CreateStaging(layers), texture);
        return _resources.AddTextureArray(texture);
    }

    /// <summary>
    /// Replaces the image that <see cref="FrameView.Overlay"/> quads read from. Only its
    /// alpha is used. Waits for the GPU, so it isn't for every frame.
    /// </summary>
    public void SetOverlayAtlas(TextureData data)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _overlayAtlas?.Dispose();
        _overlayAtlas = new GpuTexture(_context, data);
        WriteImages(_overlaySet, [new(_linearSampler, _overlayAtlas.View, ImageLayout.ShaderReadOnlyOptimal)]);
    }

    /// <summary>
    /// Photographs the scene from a probe, so surfaces in its box reflect what is really
    /// there. A probe usually sits inside an object, so <paramref name="visible"/> should
    /// leave that object out while <paramref name="shadowCasters"/> keeps it, which still
    /// puts its shadow in the picture. The view supplies the lights and shadow settings.
    /// This takes a moment and waits for the GPU, so call it while loading, or when
    /// <see cref="ReflectionLightingHash(in FrameView)"/> changes. The sun's shadows are framed on
    /// the probe's box, whatever the view's shadow centre. At most <see cref="MaxReflectionProbes"/> fit.
    /// </summary>
    public ReflectionProbeHandle CaptureReflectionProbe(
        in ReflectionProbe probe,
        in FrameView view,
        ReadOnlySpan<DrawItem> visible,
        ReadOnlySpan<DrawItem> shadowCasters)
    {
        if (_probes.Count == MaxReflectionProbes)
        {
            throw new InvalidOperationException($"Only {MaxReflectionProbes} reflection probes are supported.");
        }

        CaptureProbeFaces(probe, view, visible, shadowCasters);
        return _probes.Add(probe);
    }

    /// <summary>
    /// Photographs the scene again for a probe <see cref="CaptureReflectionProbe"/> made, which can
    /// also move it or change its box. Surfaces reflect the new picture from the next frame.
    /// </summary>
    public void RecaptureReflectionProbe(
        ReflectionProbeHandle handle,
        in ReflectionProbe probe,
        in FrameView view,
        ReadOnlySpan<DrawItem> visible,
        ReadOnlySpan<DrawItem> shadowCasters)
    {
        if ((uint)handle.Index >= (uint)_probes.Count)
        {
            throw new ArgumentException($"Reflection probe {handle.Index} does not exist; there are {_probes.Count}.", nameof(handle));
        }

        CaptureProbeFaces(probe, view, visible, shadowCasters);
        _probes.Replace(handle, probe);
    }

    /// <summary>How many reflection probes have been captured and not cleared.</summary>
    public int ReflectionProbeCount => _probes.Count;

    /// <summary>Forgets every reflection probe, so surfaces reflect only the sky from the next frame.</summary>
    public void ClearReflectionProbes() => _probes.Clear();

    /// <summary>
    /// A number that changes whenever something that lights a reflection probe's picture changes,
    /// so capture the probes again when it does. It covers the suns, the sky and background, the sun
    /// shadow's softness and cascades, the ambient occlusion settings, the light probes and whether
    /// they have settled at least once since they were made, the lightmaps loaded and whether one is still fading in
    /// (see <see cref="BakeFadeSeconds"/>), and the switches for sun shadows,
    /// materials and ambient occlusion. Moving things or the camera doesn't change it. Point and spot
    /// lights aren't in it, because captures leave them out, and nor are the contact shadow and light
    /// shadow switches, which only touch what captures leave out.
    /// </summary>
    public int ReflectionLightingHash(in FrameView view)
    {
        // Light probes fill in over many frames, and reflections captured before they first settle look wrong. Only the
        // first settle counts: the live ProbesSettled turns false on every cell the camera crosses, and recapturing
        // every reflection probe then would wait for the GPU twice a cell.
        // A capture draws the lightmaps and the bake's probes, so a bake loaded, dropped or swapped for another changes
        // what it sees, and so does a chunk bake arriving or going. A bake that arrives on screen is still fading in
        // then, so a capture would keep its half-faded picture; the fade ending changes the hash once more, and the
        // capture after it sees the bake whole, as one made when the level loads with the bake saved does.
        ulong lightmaps = (_spaceLightmapsActive || _activeBakedProbes is not null ? _loadedBake.Inputs : 0) ^ _chunkLightmapsHash;
        return HashCode.Combine(
            ReflectionLightingHash(view, _environmentVersion), _lightProbesInUse, _probesSettledOnce, lightmaps, _bakesFading);
    }

    /// <summary>
    /// The part of <see cref="ReflectionLightingHash(in FrameView)"/> that the view alone decides: the
    /// suns' directions and colours, the environment and its intensity, the clear sky's turbidity, the
    /// background, the sun shadow's softness and cascades, the ambient occlusion settings, the light probe
    /// settings, and the switches that change what a capture photographs. The scene's geometry and the
    /// camera aren't in it: captures frame the sun's shadow on their own box (<see cref="CaptureShadows"/>).
    /// </summary>
    internal static int ReflectionLightingHash(in FrameView view, int environmentVersion)
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
        hash.Add(view.BackgroundRadiance);
        hash.Add(view.ShowEnvironment);
        hash.Add(view.Shadows.Radius > 0f);
        hash.Add(view.Shadows.Cascades);
        hash.Add(view.Shadows.LightAngle);

        // Plain materials and wear and dirt change what colour the bounced light picks up, and multiscatter how
        // brightly rough metals shine in what a probe photographs.
        hash.Add(view.Off & (RenderEffects.SunShadows | RenderEffects.Materials | RenderEffects.MultiscatterSpecular
            | RenderEffects.WearAndDirt | RenderEffects.AmbientOcclusion));
        hash.Add(view.LightProbes);
        hash.Add(view.Occlusion);
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
        hash.Add(view.Shadows.Radius > 0f);
        hash.Add(off & (RenderEffects.SunShadows | RenderEffects.Materials | RenderEffects.LightShadows));
        return hash.ToHashCode();
    }

    /// <summary>
    /// Makes the light probes' atlases on the first frame with the probes on, again after a change of
    /// <see cref="ProbeQuality"/>, and frees them when the probes are switched off. Each of those waits
    /// for the GPU, so this runs before the frame's fence, and an ordinary frame changes nothing.
    /// </summary>
    private void ManageProbeStorage()
    {
        if (_off.HasFlag(RenderEffects.LightProbes) || !_lightProbes.Supported)
        {
            if (_lightProbes.Release())
            {
                WriteTargetDescriptors();
            }

            _probeScheduler = null;
            _probeBatch.Clear();
            _probesSettledOnce = false;
            _probeInvalidations.Clear();
            _lightProbesInUse = false;
            ProbeUpdatesLastFrame = 0;
            return;
        }

        EnsureProbeStorage();
    }

    /// <summary>
    /// Makes the atlases for <see cref="ProbeQuality"/> if they aren't already, and with them new
    /// cascades and a new scheduler, so every probe starts uncaptured. Waits for the GPU when it makes them.
    /// </summary>
    private void EnsureProbeStorage()
    {
        if (!_lightProbes.Supported || !_lightProbes.EnsureStorage(_probeQuality))
        {
            return;
        }

        _probeCascades = new ProbeCascades(_probeQuality);
        _probeScheduler = new ProbeScheduler(_probeCascades, 0);
        _probesSettledOnce = false;
        _probeSun ??= new ProbeSunShadow(_context, _resources.GetFlags);
        _probeBatch.Clear();
        _probeGrid = null;
        _probeLightingKey = 0;
        _probeLights.Clear();
        _probeInvalidations.Clear();

        WriteTargetDescriptors();
        _probeUpdatePass.WriteSet(
            new DescriptorImageInfo(_linearSampler, _lightProbes.IrradianceView, ImageLayout.General),
            new DescriptorImageInfo(_linearSampler, _lightProbes.DepthView, ImageLayout.General),
            new DescriptorImageInfo(_shadowSampler, _probeSun.View, ImageLayout.ShaderReadOnlyOptimal));
        _probeTracePass?.WriteSet(
            new DescriptorImageInfo(_linearSampler, _lightProbes.IrradianceView, ImageLayout.General),
            new DescriptorImageInfo(_linearSampler, _lightProbes.DepthView, ImageLayout.General),
            _lightProbes.StagingTiles,
            new DescriptorImageInfo(_environment.Sampler, _environment.Sky, ImageLayout.ShaderReadOnlyOptimal));
    }

    /// <summary>
    /// Plans this frame's light probe work, before its uniforms are written: moves the cascades with
    /// the camera, tells the scheduler what changed, picks the batch of probes to update and where they
    /// are, and plans the probes' sun shadow map. Returns how many instances the work will draw.
    /// </summary>
    private int PlanProbes(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        _probeBatch.Clear();
        _probeBatchPositions.Clear();
        _probeBatchSpacings.Clear();
        if (_probeScheduler is not { } scheduler)
        {
            _lightProbesInUse = false;
            return 0;
        }

        _lightProbesInUse = true;

        // The cells the cascades left are cleared on the GPU, and the new probes in their slots are uncaptured.
        var scroll = _probeCascades.Move(view.CameraPosition);
        scheduler.Scrolled(scroll);
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var x = scroll.Slabs[c * 3];
            var y = scroll.Slabs[c * 3 + 1];
            var z = scroll.Slabs[c * 3 + 2];
            if (x.Count == 0 && y.Count == 0 && z.Count == 0)
            {
                continue;
            }

            var layout = _probeCascades.Layout(c);
            _probeInvalidations.Add(new ProbeInvalidation(c, _probeCascades.Origin(c),
                new Int3(layout.CountX, layout.CountY, layout.CountZ),
                new Int3(x.Start, y.Start, z.Start), new Int3(x.Count, y.Count, z.Count)));
        }

        // A new box for the fixed grid starts it again, with its old probes cleared.
        if (view.LightProbes != _probeGrid)
        {
            _probeGrid = view.LightProbes;
            if (_probeGrid is { } grid)
            {
                var (countX, countY, countZ) = ProbeGrid.Counts(grid);
                scheduler.SetFixedGrid(grid.Min, countX, countY, countZ, grid.Spacing);
            }
            else
            {
                scheduler.ClearFixedGrid();
            }

            _probeInvalidations.Add(new ProbeInvalidation(ProbeVolume.FixedGridLayer, default,
                new Int3(ProbeGrid.MaxProbes, 1, 1), default, new Int3(ProbeGrid.MaxProbes, 0, 0)));
        }

        // The suns and the sky reach every probe; a point or spot light only those in its range.
        int lightingKey = ProbeLightingKey(view, _environmentVersion, _off);
        if (lightingKey != _probeLightingKey)
        {
            _probeLightingKey = lightingKey;
            scheduler.EverythingChanged();
        }

        FollowProbeLights(view.LocalLights, scheduler);

        var camera = view.CameraPosition;
        var forward = -new Vector3(view.View.M13, view.View.M23, view.View.M33);
        // Rays need no faces to draw into, so the traced path takes a bigger batch, as many as there are staging slots.
        bool traced = _probeTracePass is not null;
        int budget = traced
            ? Math.Min(ProbeBudget.For(_probeQuality).Traced, _lightProbes.StagingSlots)
            : Math.Min(ProbeBudget.For(_probeQuality).Raster, Math.Min(_lightProbes.FaceSlots, _lightProbes.StagingSlots));
        if (!ProbeUpdatesPaused)
        {
            // The scheduler refills its list on every call, and the triangle budget below may cut this frame's copy short.
            // Once the near probes are settled, the traced path refreshes only a quarter of its budget a frame.
            int refresh = traced ? ProbeBudget.For(_probeQuality).TracedRefresh : budget;
            var next = scheduler.Next(budget, camera, forward, refresh);
            for (int i = 0; i < next.Count; i++)
            {
                _probeBatch.Add(next[i]);
            }
        }

        if (_probeBatch.Count == 0)
        {
            return 0;
        }

        foreach (var id in _probeBatch)
        {
            if (id.Cascade == ProbeScheduler.FixedGrid)
            {
                var grid = _probeGrid!.Value;
                _probeBatchPositions.Add(ProbeGrid.Position(grid, id.Slot));
                _probeBatchSpacings.Add(grid.Spacing);
            }
            else
            {
                _probeBatchPositions.Add(_probeCascades.Position(id.Cascade, id.Slot));
                _probeBatchSpacings.Add(_probeCascades.Layout(id.Cascade).Spacing);
            }
        }

        var sky = SkyModel.Sun(view);
        _probeEnvironmentIntensity = view.EnvironmentIntensity;
        _probeSkySun = new Vector4(sky.Direction, sky.Turbidity);

        // Traced probes find the sun with shadow rays, through the frame's top-level structure, and draw nothing.
        if (traced)
        {
            return 0;
        }

        // The probes' own sun shadow map reaches across the coarsest cascade, which holds every probe.
        Vector3? sun = null;
        if (view.Shadows.Radius > 0f && !_off.HasFlag(RenderEffects.SunShadows))
        {
            int lights = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
            for (int i = 0; i < lights; i++)
            {
                if (view.Lights[i].CastsShadow)
                {
                    sun = view.Lights[i].Direction;
                    break;
                }
            }
        }

        var coarsest = _probeCascades.Layout(ProbeCascades.Count - 1);
        float reach = 0.5f * coarsest.Spacing * new Vector3(coarsest.CountX, coarsest.CountY, coarsest.CountZ).Length();
        _probeSun!.Plan(sun, camera, reach, _frameNumber);

        var (taken, captured) = _probeUpdatePass.Prepare(draws, _details,
            CollectionsMarshal.AsSpan(_probeBatchPositions), CollectionsMarshal.AsSpan(_probeBatchSpacings), ProbeTrianglesPerFrame);

        // Probes past the frame's triangle budget stay as they are in the scheduler, so a later frame picks them.
        if (taken < _probeBatch.Count)
        {
            _probeBatch.RemoveRange(taken, _probeBatch.Count - taken);
        }

        return captured + (_probeSun.Redraw ? draws.Length : 0);
    }

    /// <summary>
    /// Marks the probes near each point and spot light that appeared, went out, or changed its place,
    /// range, colour, cone or shadow since last frame, both where it was and where it is now.
    /// </summary>
    private void FollowProbeLights(ReadOnlySpan<LocalLight> lights, ProbeScheduler scheduler)
    {
        _probeLightsSeen.Clear();
        foreach (ref readonly var light in lights)
        {
            _probeLightsSeen.Add(light.Key);
            if (_probeLights.TryGetValue(light.Key, out var old))
            {
                if (SameProbeLight(old, light))
                {
                    continue;
                }

                scheduler.LightChanged(old.Position, old.Range);
            }

            scheduler.LightChanged(light.Position, light.Range);
            _probeLights[light.Key] = light;
        }

        if (_probeLights.Count == _probeLightsSeen.Count)
        {
            return;
        }

        _probeLightsGone.Clear();
        foreach (var (key, old) in _probeLights)
        {
            if (!_probeLightsSeen.Contains(key))
            {
                scheduler.LightChanged(old.Position, old.Range);
                _probeLightsGone.Add(key);
            }
        }

        foreach (int key in _probeLightsGone)
        {
            _probeLights.Remove(key);
        }
    }

    private static bool SameProbeLight(in LocalLight a, in LocalLight b) =>
        a.Position == b.Position
        && a.Range == b.Range
        && a.Radiance == b.Radiance
        && a.Radius == b.Radius
        && a.Direction == b.Direction
        && a.InnerAngle == b.InnerAngle
        && a.OuterAngle == b.OuterAngle
        && a.CastsShadow == b.CastsShadow;

    /// <summary>
    /// Records this frame's light probe work, after the lights' shadow tiles: clears the cells the
    /// cascades left, then draws the batch's probes, folds their faces and blends them into the atlases.
    /// Where the GPU can trace, the batch's probes are traced through <paramref name="traced"/> instead.
    /// </summary>
    private void RecordProbeUpdate(
        CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<DrawItem> draws, InstanceBuffer instances, TopLevelStructure? traced)
    {
        foreach (var invalidation in _probeInvalidations)
        {
            _lightProbes.RecordInvalidate(cmd, invalidation.Layer, invalidation.Origin,
                invalidation.Counts.X, invalidation.Counts.Y, invalidation.Counts.Z, invalidation.SlabStart, invalidation.SlabCount);
        }

        _probeInvalidations.Clear();

        var batch = _probeBatch;
        ProbeUpdatesLastFrame = batch.Count;
        if (batch.Count == 0 || _probeScheduler is not { } scheduler)
        {
            return;
        }

        if (_probeTracePass is { } tracePass && traced is { } scene)
        {
            _lightProbes.BeginFolds(cmd);
            tracePass.Record(cmd, batch, scene, _tracing!, _frameIndex, frameSet,
                CollectionsMarshal.AsSpan(_probeBatchPositions), CollectionsMarshal.AsSpan(_probeBatchSpacings),
                (uint)scheduler.Frame, _probeEnvironmentIntensity, _probeSkySun);
            _lightProbes.RecordBlend(cmd, batch, TracedBlend, TracedSnap);
            scheduler.Updated(batch);
            _probesSettledOnce |= scheduler.Settled;
            return;
        }

        _probeSun!.Record(_api, cmd, _shadowPass, frameSet, draws, _meshes, _details, instances);
        _probeUpdatePass.Record(cmd, _lightProbes, frameSet, draws, _meshes, instances);
        _lightProbes.BeginFolds(cmd);
        for (int i = 0; i < batch.Count; i++)
        {
            _lightProbes.RecordFold(cmd, i, _probeBatchSpacings[i], _probeEnvironmentIntensity, _probeSkySun);
        }

        _lightProbes.RecordBlend(cmd, batch, RasterBlend);
        scheduler.Updated(batch);
        _probesSettledOnce |= scheduler.Settled;
    }

    /// <summary>
    /// The shadow settings a capture uses: the view's, with the sharpest map round the box being
    /// captured rather than round the camera, so moving the camera doesn't change the picture.
    /// Shadows that are off stay off.
    /// </summary>
    internal static ShadowSettings CaptureShadows(in ShadowSettings shadows, Vector3 boxMin, Vector3 boxMax)
    {
        var framed = shadows;
        if (shadows.Radius > 0f)
        {
            framed.Center = (boxMin + boxMax) * 0.5f;
            framed.Radius = MathF.Max(Vector3.Distance(boxMin, boxMax) * 0.5f, 0.01f);
        }

        return framed;
    }

    private void CaptureProbeFaces(in ReflectionProbe probe, in FrameView view, ReadOnlySpan<DrawItem> visible, ReadOnlySpan<DrawItem> shadowCasters)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _uploads.Flush();
        BakeSky(view);

        // The passes are bound to the screen's targets, so small ones stand in for a while. Rebinding
        // them forgets the held transition picture, so that is put back after.
        var screenTargets = _gBuffer;
        bool heldReady = _heldReady;
        bool ldrReady = _ldrReady;
        bool historyReady = _historyReady;
        var screenOff = _off;
        _reflectionProbeTargets ??= new GBuffer(_context, new Extent2D(ReflectionProbes.FaceSize, ReflectionProbes.FaceSize));
        _gBuffer = _reflectionProbeTargets;
        WriteTargetDescriptors();

        // The picture follows the view's switches, which Render hasn't applied yet this frame.
        _off = view.Off & RenderEffects.All;

        // The probe's targets have no history, so RecordScene readies an empty one each face.
        // The screen's history is kept, and so is the flag, which the finally puts back.
        _historyReady = false;
        try
        {
            var faceView = view;
            faceView.Shadows = CaptureShadows(view.Shadows, probe.BoxMin, probe.BoxMax);
            CaptureFaces(faceView, probe.Position, visible, shadowCasters, _probes.Capture);
        }
        finally
        {
            _off = screenOff;
            _gBuffer = screenTargets;
            WriteTargetDescriptors();
            _heldReady = heldReady;
            _ldrReady = ldrReady;
            _historyReady = historyReady;
        }
    }

    /// <summary>
    /// Draws the scene from <paramref name="position"/> into each face of <paramref name="cube"/>,
    /// whose size the current targets must have, waiting for each face.
    /// </summary>
    private void CaptureFaces(
        in FrameView view,
        Vector3 position,
        ReadOnlySpan<DrawItem> visible,
        ReadOnlySpan<DrawItem> shadowCasters,
        CaptureCube cube)
    {
        const float probeNear = 0.05f;

        // The camera directions behind each cube face. A cube map is mirrored compared
        // with a camera, so z swaps here and lookups swap it back in lighting.frag.
        ReadOnlySpan<(Vector3 Forward, Vector3 Up)> faces =
        [
            (Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitX, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ),
            (-Vector3.UnitY, -Vector3.UnitZ),
            (-Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitZ, Vector3.UnitY),
        ];

        ref Frame frame = ref _frames[_frameIndex];
        var fence = frame.InFlight;
        var faceView = view;
        faceView.CameraPosition = position;
        faceView.Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, probeNear);
        faceView.BackgroundFadeStart = 0f;
        faceView.BackgroundFadeEnd = 0f;

        // Probes see the decals too, so a stain's damp shine shows in a reflection. The GPU is idle, so
        // this frame's buffer is free. Decals don't count towards a probe's lighting hash, so they never
        // cause a capture of their own.
        PrepareDecals(view);
        _decalPass.Upload(_frameIndex);

        for (int face = 0; face < faces.Length; face++)
        {
            faceView.View = Matrix4x4.CreateLookAt(position, position + faces[face].Forward, faces[face].Up);
            bool castsShadow = WriteUniforms(frame, faceView, Matrix4x4.Identity, Vector2.Zero, 0, false, capturingProbe: true, []);
            bool drawShadows = castsShadow;
            WriteBones(frame, view.Bones);
            ResetInstances(frame, visible.Length + (drawShadows ? shadowCasters.Length * _shadowCascades : 0));

            _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
            var cmd = frame.CommandBuffer;
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _api.BeginCommandBuffer(cmd, &beginInfo).Check();
            RecordScene(cmd, frame.Set, visible, drawShadows ? shadowCasters : [], shadowsSelected: false,
                TransparentView.From(faceView.View, faceView.Projection));
            cube.RecordFace(cmd, _gBuffer.Hdr.Image, face);

            _api.EndCommandBuffer(cmd).Check();

            _api.ResetFences(_context.Device, 1, &fence).Check();
            var commandInfo = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = cmd,
            };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandInfo,
            };
            _api.QueueSubmit2(_context.Queue, 1, &submit, fence).Check();
            _api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue).Check();
        }
    }

    /// <summary>
    /// Adds a surface shader from compiled SPIR-V. It decides how a material's surface
    /// looks and is lit like any other. See docs/design.md for how to write one. This is
    /// slow, so call it while loading.
    /// </summary>
    public SurfaceShaderHandle CreateSurfaceShader(byte[] spirv)
    {
        const uint spirvMagic = 0x07230203;
        if (spirv.Length < 20 || spirv.Length % sizeof(uint) != 0 || BitConverter.ToUInt32(spirv, 0) != spirvMagic)
        {
            throw new ArgumentException("This is not compiled SPIR-V code.", nameof(spirv));
        }

        return _gBufferPass.AddShader(spirv);
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
            _tracing!.ForgetMaterial(handle);
        }
    }

    private void ValidateMaterial(in Material material)
    {
        if (material.Subsurface.Problem(material.Clearcoat, material.Flags) is { } problem)
        {
            throw new ArgumentException($"The material's {problem}", nameof(material));
        }

        if ((material.Flags & MaterialFlags.Foliage) != 0 && Material.TranslucencyProblem(material.Translucency) is { } translucency)
        {
            throw new ArgumentException($"The material's {translucency}", nameof(material));
        }

        if (Material.WindProblem(material.Wind) is { } wind)
        {
            throw new ArgumentException($"The material's {wind}", nameof(material));
        }

        if (material.Shader is { } shader)
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

            materials[i] = CreateMaterial(finish is null ? material : finish(i, material));
        }

        // A posed skeleton can reach well outside the rest pose, so a skinned part's
        // culling sphere is doubled rather than culled too early at the screen's edge.
        float radiusScale = model.Skeleton is null ? 1f : 2f;
        return CreateDetailedMesh(model.Vertices, model.Indices, model.Parts, materials, radiusScale: radiusScale);
    }

    /// <summary>
    /// The material a cooked model's material makes, with its texture indices turned into <paramref name="textures"/>'
    /// handles and -1 for none. The mask textures come separately, from <see cref="ModelData.MaskOf"/>.
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
            details[i] = AddDetail(detail with { Radius = detail.Radius * radiusScale });
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
        _meshes[mesh.Index]!.Details = details;
        foreach (ref var draw in draws.AsSpan())
        {
            draw.Mesh = mesh;
        }

        return draws;
    }

    private int AddDetail(in PartDetail detail)
    {
        if (_freeDetails.Count > 0)
        {
            int slot = _freeDetails.Pop();
            _details[slot] = detail;
            return slot;
        }

        _details.Add(detail);
        return _details.Count - 1;
    }

    /// <summary>
    /// The sphere around a part, in the model's units, before its draw's transform. A part without detail is the
    /// point at its origin.
    /// </summary>
    public BakeSphere BoundsOf(DetailHandle detail)
    {
        var part = _details[detail.Index];
        return new BakeSphere(part.Center, part.Radius);
    }

    public void Render(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (view.Width <= 0 || view.Height <= 0)
        {
            return; // Minimized: there is nothing to present to.
        }

        bool traceView = view.DebugView == DebugView.Trace;
        if (traceView && _tracing is null)
        {
            throw new InvalidOperationException(
                $"The trace debug view needs a GPU that can trace rays, and this one can't ({_context.NoTraceReason}).");
        }

        _traceView = traceView;
        _probeView = view.DebugView == DebugView.Probes;

        // The trace debug view shows colours as they are, so nothing that blends, blurs or brightens them runs.
        var viewOff = _traceView ? view.Off | TraceViewOff : view.Off;

        // FSR draws the picture at the window's size, so starting or stopping it changes the targets.
        bool upscaling = _upscalerUsed == Upscaler.Fsr && _renderScale <= 1f && !viewOff.HasFlag(RenderEffects.AntiAliasing);
        bool upscalingChanged = upscaling != _upscaling;
        _upscaling = upscaling;
        if (_vsyncChanged || (uint)view.Width != _swapchain.Extent.Width || (uint)view.Height != _swapchain.Extent.Height)
        {
            Resize((uint)view.Width, (uint)view.Height);
        }
        else if (upscalingChanged || (SceneExtent(_swapchain.Extent) is var scene
            && (scene.Width != _gBuffer.Extent.Width || scene.Height != _gBuffer.Extent.Height)))
        {
            // Frames in flight still read the targets being replaced. Changing the scale or upscaler is rare.
            _api.DeviceWaitIdle(_context.Device).Check();
            RebuildTargets();
        }

        if (_shadowResolution != _shadowPass.Resolution)
        {
            // Frames in flight still read the maps being replaced. Changing this is rare.
            _api.DeviceWaitIdle(_context.Device).Check();
            _shadowPass.Resize(_shadowResolution);
            WriteTargetDescriptors();
        }

        // Metering is left out of the shaders unless the view asked for it, so the fixed exposure stays as it was.
        _off = Exposure.ShaderOff(view.Post, viewOff & RenderEffects.All);
        _windReach = new WindReach(Wind.Strength(view.Wind, _off), _resources.SwayOf);
        if (!_volumetricFog.HasTargets && VolumetricFog.Pack(view.Fog, _off, false, view.Water.Absorption != Vector3.Zero).X > 0f)
        {
            // The fog's volumes are made the first time it's drawn. Frames in flight bind the stand-in.
            _api.DeviceWaitIdle(_context.Device).Check();
            _volumetricFog.SetSize((int)_gBuffer.Extent.Width, (int)_gBuffer.Extent.Height);
            WriteTargetDescriptors();
        }
        else if (_volumetricFog.HasTargets && (_off & (RenderEffects.Fog | RenderEffects.VolumetricFog)) != 0)
        {
            // Switched off, the volumes are freed, and made again with no history if it's switched back on.
            // Frames in flight still read them. Flipping the switch is rare.
            _api.DeviceWaitIdle(_context.Device).Check();
            _volumetricFog.Release();
            WriteTargetDescriptors();
        }

        _depthOfField = DepthOfFieldConstants.For(view.Post, _off, (int)_gBuffer.Extent.Width);
        bool antiAliased = !_off.HasFlag(RenderEffects.AntiAliasing);
        if (antiAliased != _readsAntiAliased)
        {
            // Frames in flight still read the sets being rewritten. Changing this is rare.
            _api.DeviceWaitIdle(_context.Device).Check();
            _readsAntiAliased = antiAliased;
            _bloomPass.SetSource(DisplaySource);
            WriteImages(_tonemapSet,
            [
                new(_sampler, DisplaySource, ImageLayout.ShaderReadOnlyOptimal),
                new(_linearSampler, _bloomPass.Result, ImageLayout.ShaderReadOnlyOptimal),
            ]);
            _exposureMeter.SetSource(DisplaySource, _gBuffer.Display);
            _depthOfFieldPass.SetSource(DisplaySource, _gBuffer.Depth.View);
        }

        // Checked before anything is recorded, so a decal that can't be drawn throws with the frame untouched.
        PrepareDecals(view);
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.BakedChunks);
        if (!_drawingWithoutBakedLights)
        {
            // Before the bakes are resolved, so a file Done saves can be fresh this very frame.
            TakeFinishedBakes();

            // Worked out once, from the original view: a frame drawn again without its baked sun keeps the sky that sun
            // lights, and its disc.
            BakeSky(view);
            bool relit = ResolveBakes(view, draws, out var withoutBakedLights);

            // From the frame with every light, since a bake takes the lights a fresh bake would drop.
            UpdateBackgroundBake(view, draws);
            if (relit)
            {
                // A fresh bake has taken some lights over, so the frame is drawn without them.
                _drawingWithoutBakedLights = true;
                try
                {
                    Render(withoutBakedLights, draws);
                }
                finally
                {
                    _drawingWithoutBakedLights = false;
                }

                return;
            }
        }

        ManageProbeStorage();

        ref Frame frame = ref _frames[_frameIndex];
        var fence = frame.InFlight;
        long waitStart = Stopwatch.GetTimestamp();
        _api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue).Check();
        ReleaseDestroyed(_frameIndex);
        MeasureBakeSlice();

        var swapchainHandle = _swapchain.Handle;
        uint imageIndex = 0;
        var acquired = _context.SwapchainApi.AcquireNextImage(
            _context.Device, swapchainHandle, ulong.MaxValue, frame.ImageAvailable, default, &imageIndex);
        if (acquired == Result.ErrorOutOfDateKhr)
        {
            Resize((uint)view.Width, (uint)view.Height);
            return;
        }

        if (acquired != Result.SuboptimalKhr)
        {
            acquired.Check();
        }

        bool timed = MeasureGpu && _gpuTimer.IsSupported;
        if (MeasureGpu)
        {
            WaitMilliseconds = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        }

        if (timed)
        {
            _gpuTimer.Collect(_frameIndex);
        }

        _api.ResetFences(_context.Device, 1, &fence).Check();

        // A shift of less than a pixel, different every frame, lets anti-aliasing gather
        // detail from between the pixels.
        // Without anti-aliasing to gather it, the shift would only make the picture shake.
        // The pixels are the scene's, which the render scale may make smaller or larger than the window's.
        var viewProjection = view.View * view.Projection;
        // FSR takes as many shifts as it needs to land on every window pixel, more the smaller the scene.
        var scenePixels = new Vector2(_gBuffer.Extent.Width, _gBuffer.Extent.Height);
        int jitterPhases = _upscaling ? Upscale.JitterPhaseCount((float)_gBuffer.Extent.Width / _gBuffer.Display.Width) : JitterSteps;
        var jitter = Upscale.Jitter(_frameNumber, jitterPhases);
        var shift = antiAliased ? jitter * 2f / scenePixels : Vector2.Zero;
        var previous = _historyReady ? _previousViewProjection : viewProjection;
        if (_upscaling)
        {
            float milliseconds = (view.Time - _previousTime) * 1000f;
            _fsrFrame = new FsrFrame(
                jitter,
                _historyReady && milliseconds > 0f ? milliseconds : 1000f / 60f,
                Reset: !_historyReady,
                CameraNear(view.Projection),
                VerticalFov(view.Projection));
        }

        // Before the uniforms, which carry the probes' sun shadow map and whether surfaces read the probes.
        int probeInstances = PlanProbes(view, draws);

        if (_casterBounds.Length < draws.Length)
        {
            _casterBounds = new Vector4[draws.Length * 2];
        }

        int casters = DrawSelection.Bounds(draws, _details, _casterBounds, _windReach);
        bool castsShadow = WriteUniforms(
            frame, view, previous, shift, _frameNumber, _historyReady, capturingProbe: false, _casterBounds.AsSpan(0, casters));
        _uniformFrame = _frameIndex;
        WriteBones(frame, view.Bones);
        _motion.Begin(draws, view.Bones, _historyReady,
            new Span<Matrix4x4>((Matrix4x4*)frame.Bones.Mapped + view.Bones.Length, 2 * MaxBones - view.Bones.Length));
        _previousViewProjection = viewProjection;
        _previousTime = view.Time;
        _frameNumber++;
        _transition = TransitionConstants.From(view.Transition);
        _transitionHold = view.Transition.Hold;
        var (cameraCount, shadowCount) = SelectDraws(view, viewProjection, draws, castsShadow);
        int tileDrawCount = SelectTileDraws(draws);
        ResetInstances(frame, cameraCount + shadowCount + tileDrawCount + probeInstances);
        // The space's slots go every frame, so a frame drawing only the space reads its mask as it always has.
        _spaceSlots.AsSpan().CopyTo(new Span<int>((byte*)frame.Chunks.Mapped + SunShBytes, _spaceSlots.Length));
        if (_relightChunks)
        {
            _sunSh.AsSpan().CopyTo(new Span<Vector4>(frame.Chunks.Mapped, _sunSh.Length));
            _chunkData.AsSpan(0, _chunkCount).CopyTo(new Span<GpuChunk>((byte*)frame.Chunks.Mapped + ChunkHeaderBytes, _chunkCount));
        }

        _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
        var cmd = frame.CommandBuffer;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _api.BeginCommandBuffer(cmd, &beginInfo).Check();
        _uploads.Record(cmd, _destroyed[_frameIndex].Others);
        // This frame's share of the chunk bakes going up, and those no copy named this frame let go once it is done.
        _chunkPool.Record(cmd, _destroyed[_frameIndex].Textures, _destroyed[_frameIndex].Others);
        // A slice of the background bake, after the uploads so a mesh made this frame is in its scene.
        RecordBakeSlice(cmd);
        // One top-level structure a frame, shared by the trace debug view and the traced probe updates, made only on
        // a frame that traces something.
        TopLevelStructure? traced = null;
        if (_traceView || (_probeTracePass is not null && _probeBatch.Count > 0))
        {
            // After the uploads, so a mesh made this frame is traced this frame. From here until the submit below,
            // nothing may destroy or change a material: see RayTracing.TopLevel.
            traced = _tracing!.TopLevel(cmd, _traceScene!.Collect(draws));
        }

        int overlayQuads = _overlayAtlas is null ? 0 : _overlayPass.Upload(_frameIndex, view.Overlay);
        UploadParticles(view);

        _decalPass.Upload(_frameIndex);
        if (timed)
        {
            _gpuTimer.Begin(cmd, _frameIndex);
        }

        // Worked out here, just before the frame is recorded, so a frame that is never drawn doesn't move the clock.
        _exposureStep = _exposureClock.Next(
            !_off.HasFlag(RenderEffects.AutoExposure), view.Time, Exposure.Speed(view.Post.AdaptationSpeed));
        RecordFrame(cmd, frame.Set, imageIndex, _cameraDraws.AsSpan(0, cameraCount), draws, overlayQuads, traced);
        _gpuTimer.End();
        _api.EndCommandBuffer(cmd).Check();

        var renderFinished = _swapchain.RenderFinished[imageIndex];
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
        _api.QueueSubmit2(_context.Queue, 1, &submit, fence).Check();
        _framesSubmitted++;

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

        _frameIndex = (_frameIndex + 1) % FramesInFlight;
    }

    /// <summary>
    /// Renders a frame and also returns it as RGBA bytes, row by row from the top. It
    /// works on a hidden window. Allocates and waits for the GPU, so it isn't for the game loop.
    /// </summary>
    /// <summary>Leaves out what each pass can't see, and swaps in simpler models where nobody will notice.</summary>
    private (int Camera, int Shadows) SelectDraws(
        in FrameView view, in Matrix4x4 viewProjection, ReadOnlySpan<DrawItem> draws, bool castsShadow)
    {
        if (_cameraDraws.Length < draws.Length)
        {
            _cameraDraws = new DrawItem[draws.Length * 2];
            for (int i = 0; i < _shadowDraws.Length; i++)
            {
                _shadowDraws[i] = new DrawItem[draws.Length * 2];
            }
        }

        var (pixelsAtOneUnit, pixelsPerUnit) = Projection.PixelScale(view.Projection, (int)_gBuffer.Extent.Height);
        var camera = new DetailView(viewProjection, view.CameraPosition, pixelsAtOneUnit, pixelsPerUnit, CameraDetailError);
        int cameraCount;
        if (_relightChunks)
        {
            if (_cameraSources.Length < _cameraDraws.Length)
            {
                _cameraSources = new int[_cameraDraws.Length];
                _cameraChunks = new int[_cameraDraws.Length];
            }

            cameraCount = DrawSelection.Select(camera, draws, _details, _cameraDraws, _windReach, _cameraSources);
            for (int i = 0; i < cameraCount; i++)
            {
                _cameraChunks[i] = _drawChunks[_cameraSources[i]];
            }
        }
        else
        {
            cameraCount = DrawSelection.Select(camera, draws, _details, _cameraDraws, _windReach);
        }

        int shadowCount = 0;
        Array.Clear(_shadowCounts);
        if (castsShadow)
        {
            for (int cascade = 0; cascade < _shadowCascades; cascade++)
            {
                float texelsPerUnit = _shadowPass.Resolution / (2f * ShadowPass.CascadeRadius(view.Shadows, cascade));
                var shadow = new DetailView(
                    _shadowMatrices[cascade], view.CameraPosition, 0f, texelsPerUnit, ShadowDetailError);
                _shadowCounts[cascade] = DrawSelection.Select(shadow, draws, _details, _shadowDraws[cascade], _windReach);
                shadowCount += _shadowCounts[cascade];
            }
        }

        if (MeasureGpu)
        {
            long shadowTriangles = 0;
            for (int cascade = 0; cascade < _shadowCascades; cascade++)
            {
                shadowTriangles += CountTriangles(_shadowDraws[cascade].AsSpan(0, _shadowCounts[cascade]));
            }

            DrawnTriangles = (CountTriangles(_cameraDraws.AsSpan(0, cameraCount)), shadowTriangles);
        }

        return (cameraCount, shadowCount);
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
        _particleCount = _particlePass.Upload(_frameIndex, view.Particles, view.CameraPosition, forward);
        _transparentView = TransparentView.From(view.View, view.Projection);
    }

    /// <summary>
    /// Picks what each shadow tile of the atlas can see, one after another in
    /// <see cref="_tileDraws"/>, and returns how many there are in all.
    /// </summary>
    private int SelectTileDraws(ReadOnlySpan<DrawItem> draws)
    {
        var tiles = _localLights.Tiles;
        int total = 0;
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            if (_tileDraws.Length < total + draws.Length)
            {
                Array.Resize(ref _tileDraws, Math.Max(_tileDraws.Length * 2, total + draws.Length));
            }

            ref readonly var view = ref tiles[tile];
            var shadow = new DetailView(view.Matrix, view.Eye, view.PixelsAtOneUnit, 0f, ShadowDetailError, far: true);
            int count = DrawSelection.Select(shadow, draws, _details, _tileDraws.AsSpan(total), _windReach);
            _tileRanges[tile] = (total, count);
            total += count;
        }

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

        _api.DeviceWaitIdle(_context.Device).Check();

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
        _api.DeviceWaitIdle(_context.Device).Check();
        _uploads.Discard();
        DropBackgroundBakes();
        for (int slot = 0; slot < FramesInFlight; slot++)
        {
            ReleaseDestroyed(slot);
        }

        // Its pages already in the material table go with the table.
        _chunkPool.Dispose();

        _traceDebugPass?.Dispose();
        _baker?.Dispose();
        _tracing?.Dispose();
        _gpuTimer.Dispose();
        _bakeTimer.Dispose();
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
        _lightingPass.Dispose();
        _scatterAcrossPass.Dispose();
        _scatterDownPass.Dispose();
        _aoBlurPass.Dispose();
        _aoPass.Dispose();
        _gBufferPass.Dispose();
        _shadowPass.Dispose();
        _api.DestroyPipeline(_context.Device, _clusterPipeline, null);
        _api.DestroyPipelineLayout(_context.Device, _clusterLayout, null);
        _probes.Dispose();
        _lightProbes.Dispose();
        _volumetricFog.Dispose();
        _probeUpdatePass.Dispose();
        _probeTracePass?.Dispose();
        _probeSun?.Dispose();
        _reflectionProbeTargets?.Dispose();
        _skySh.Dispose();
        _environment.Dispose();
        _resources.Dispose();

        foreach (var mesh in _meshes)
        {
            mesh?.Dispose();
        }

        foreach (ref var frame in _frames.AsSpan())
        {
            frame.Uniforms.Dispose();
            frame.Bones.Dispose();
            frame.Instances.Dispose();
            frame.Lights.Dispose();
            frame.ShadowTiles.Dispose();
            frame.Clusters.Dispose();
            frame.Chunks.Dispose();
            _api.DestroySemaphore(_context.Device, frame.ImageAvailable, null);
            _api.DestroyFence(_context.Device, frame.InFlight, null);
            _api.DestroyCommandPool(_context.Device, frame.CommandPool, null);
        }

        _api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _oneTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _twoTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _threeTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _fourTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _lightingSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _frameSetLayout, null);
        _api.DestroySampler(_context.Device, _shadowSampler, null);
        _api.DestroySampler(_context.Device, _linearSampler, null);
        _api.DestroySampler(_context.Device, _mipSampler, null);
        _api.DestroySampler(_context.Device, _sampler, null);
        _gBuffer.Dispose();
        _swapchain.Dispose();
        _context.Dispose();
    }

    private void RecordFrame(
        CommandBuffer cmd,
        DescriptorSet frameSet,
        uint imageIndex,
        ReadOnlySpan<DrawItem> draws,
        ReadOnlySpan<DrawItem> sceneDraws,
        int overlayQuads,
        TopLevelStructure? traced)
    {
        var targets = _gBuffer;
        // The picture's size from anti-aliasing on: the scene's, or the window's once FSR has rebuilt it.
        var extent = targets.Display;
        var swapchainImage = _swapchain.Images[imageIndex];

        // Ldr still holds the last frame, so it's kept before this frame draws over it.
        if (_transitionHold != _heldNumber || !_heldReady)
        {
            RecordHold(cmd, targets, copyLastFrame: _transitionHold != _heldNumber && _ldrReady);
            _heldNumber = _transitionHold;
        }

        RecordScene(cmd, frameSet, draws, [], shadowsSelected: true, _transparentView, _particleCount, _motion, traced, sceneDraws, updateProbes: true,
            probeView: _probeView, relightChunks: _relightChunks);

        // Anti-aliasing blends in the last frame, and reflections read it, so it's kept for
        // the next while either wants it. Without anti-aliasing the lit image is the frame.
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
        else if (!_off.HasFlag(RenderEffects.Reflections))
        {
            RecordHistoryCopy(cmd, targets.Hdr.Image, drawnJustNow: false);
        }
        else
        {
            _historyReady = false;
        }

        _gpuTimer.Mark(cmd);

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

        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, ColorStage, AccessFlags2.None, ColorStage, ColorWrite);

        // From here on the picture is the window's size, which the scene may not be.
        var window = _swapchain.Extent;
        ReadOnlySpan<DescriptorSet> finishSets = [frameSet, _finishSet];
        var transition = _transition;
        transition.Style.Z = extent.Width != window.Width || extent.Height != window.Height ? 1f : 0f;
        _finishPass.Record(cmd, _swapchain.Views[imageIndex], window, finishSets,
            new ReadOnlySpan<byte>(&transition, (int)TransitionConstants.Size));
        _ldrReady = true;
        _gpuTimer.Mark(cmd);

        if (overlayQuads > 0)
        {
            _overlayPass.Record(cmd, _swapchain.Views[imageIndex], window, _overlaySet, _frameIndex, overlayQuads);
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
        _heldReady = true;
    }

    /// <summary>
    /// Keeps this frame for the next one, and leaves it ready to sample. The frame is still
    /// a target when it was <paramref name="drawnJustNow"/>, and already ready to sample when not.
    /// </summary>
    private void RecordHistoryCopy(CommandBuffer cmd, Image frame, bool drawnJustNow)
    {
        var targets = _gBuffer;
        if (drawnJustNow)
        {
            _api.Transition(cmd, frame, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, ColorStage, ColorWrite,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        }
        else
        {
            _api.Transition(cmd, frame, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal, SampledStage, SampledAccess,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        }

        _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, SampledStage, SampledAccess,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        _api.CmdCopyImage(cmd, frame, ImageLayout.TransferSrcOptimal,
            targets.History.Image, ImageLayout.TransferDstOptimal, 1, &region);

        _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit, SampledStage, SampledAccess);
        _api.Transition(cmd, frame, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit, SampledStage, SampledAccess);
        _historyReady = true;
    }

    /// <summary>
    /// Draws and lights the scene into the HDR target, for frames and reflection probe captures
    /// alike. A frame's shadow draws were picked per cascade by <see cref="SelectDraws"/>;
    /// a probe capture draws <paramref name="shadowCasters"/> into every cascade.
    /// Probes leave particles out, since a puff of dust shouldn't stay in a reflection.
    /// Only a frame passes <paramref name="motion"/>: where its draws stood last frame, for motion
    /// vectors. A probe's motion is never read, so its draws stand still. Only a frame passes
    /// <paramref name="probeDraws"/>, the whole scene's draws, and updates this frame's batch of
    /// light probes from them.
    /// </summary>
    private void RecordScene(
        CommandBuffer cmd,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        ReadOnlySpan<DrawItem> shadowCasters,
        bool shadowsSelected,
        in TransparentView view,
        int particleCount = 0,
        MotionHistory? motion = null,
        TopLevelStructure? traced = null,
        ReadOnlySpan<DrawItem> probeDraws = default,
        bool updateProbes = false,
        bool probeView = false,
        bool relightChunks = false)
    {
        var targets = _gBuffer;
        var instances = _frames[_frameIndex].Instances;
        var extent = targets.Extent;

        // The lighting pass binds history even before there is any. Probe captures have their
        // own small targets, whose history is never drawn, so they clear the flag while they run.
        if (!_historyReady)
        {
            _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
        }

        // Targets are shared between frames in flight, so each first use waits for the
        // previous frame's sampling to finish. Contents are discarded via Undefined.
        // When no sun casts a shadow the maps aren't drawn, or even cleared, and nothing
        // reads them, but the lighting pass still binds them.
        var shadowMap = _shadowPass.Target.Image;
        if (_sunShadowsDrawn)
        {
            _api.Transition(cmd, shadowMap, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, ShadowReadStages, SampledAccess, DepthStages,
                DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit, layerCount: ShadowPass.MaxCascades);
            for (int cascade = 0; cascade < _shadowCascades; cascade++)
            {
                var casters = shadowsSelected ? _shadowDraws[cascade].AsSpan(0, _shadowCounts[cascade]) : shadowCasters;
                _shadowPass.Record(cmd, frameSet, casters, _meshes, _details, instances, cascade);
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

        // The lighting pass binds the atlas even when no light has a tile in it.
        var atlas = _shadowPass.Atlas.Image;
        if (_tileCount > 0)
        {
            _api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, ShadowReadStages, SampledAccess, DepthStages,
                DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);
            _shadowPass.RecordTiles(cmd, frameSet, _localLights.Tiles, _tileDraws, _tileRanges.AsSpan(0, _tileCount), _meshes, _details, instances);
            _api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, ShadowReadStages, SampledAccess);
        }
        else
        {
            _api.Transition(cmd, atlas, ImageAspectFlags.DepthBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, ShadowReadStages, SampledAccess, ShadowReadStages, SampledAccess);
        }

        _gpuTimer.Mark(cmd);

        // After the lights' tiles, which the probes' captures read for point and spot light shadows.
        if (updateProbes)
        {
            RecordProbeUpdate(cmd, frameSet, probeDraws, instances, traced);
        }

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

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Emissive.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Motion.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Baked.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Mask.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, DepthStages, AccessFlags2.DepthStencilAttachmentReadBit, DepthStages,
            DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);

        // Reflection captures draw the space's lightmaps too, so they see its baked light as the camera does. Only the
        // camera's draws know their chunk, so only they draw a chunk's bake, relit by the frame's sky and suns: its sky
        // is projected first, for writeBaked. A capture lights a chunk's surfaces live.
        if (relightChunks)
        {
            _skySh.Record(cmd);
            instances.Chunks = _cameraChunks;
            instances.ChunkLightmaps = _chunkLightmaps;
        }

        instances.History = motion;
        instances.Lightmaps = _activeLightmaps;
        _gBufferPass.Record(cmd, targets, frameSet, draws, _meshes, _details, instances);
        instances.History = null;
        instances.Lightmaps = null;
        instances.Chunks = null;
        instances.ChunkLightmaps = null;
        bool decalsDrawn = RecordDecals(cmd, targets, frameSet);

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Emissive.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Motion.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Baked.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Mask.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        if (!decalsDrawn)
        {
            _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
                ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, SampledStage, SampledAccess);
        }

        _gpuTimer.Mark(cmd);

        // Left out, the lighting pass reads no occlusion, but it still binds the target.
        if (_off.HasFlag(RenderEffects.AmbientOcclusion))
        {
            _api.Transition(cmd, targets.AoBlurred.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
            _gpuTimer.Mark(cmd);
            _gpuTimer.Mark(cmd);
        }
        else
        {
            ReadOnlySpan<DescriptorSet> aoSets = [frameSet, _aoSet];
            _api.BeginColorTarget(cmd, targets.Ao.Image);
            _aoPass.Record(cmd, targets.Ao.View, extent, aoSets);
            _api.EndColorTarget(cmd, targets.Ao.Image);
            _gpuTimer.Mark(cmd);

            ReadOnlySpan<DescriptorSet> aoBlurSets = [_aoBlurSet];
            _api.BeginColorTarget(cmd, targets.AoBlurred.Image);
            _aoBlurPass.Record(cmd, targets.AoBlurred.View, extent, aoBlurSets);
            _api.EndColorTarget(cmd, targets.AoBlurred.Image);
            _gpuTimer.Mark(cmd);
        }

        if (_traceView && traced is { } scene)
        {
            // The trace debug view writes Hdr in place of lighting, and nothing blends over it. Depth is left read-only
            // as the transparent pass would leave it, for the passes after.
            _traceDebugPass!.Record(cmd, _frameIndex, _tracing!, scene, frameSet, _resources.Set, targets.Hdr, extent);
            _gpuTimer.Mark(cmd);
            _gpuTimer.Mark(cmd);
            _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
                AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
            _gpuTimer.Mark(cmd);
            return;
        }

        _api.BeginColorTarget(cmd, targets.Hdr.Image);
        _api.BeginColorTarget(cmd, targets.Scatter.Image);

        ReadOnlySpan<DescriptorSet> lightingSets = [frameSet, _lightingSet, _resources.Set];
        _lightingPass.Record(cmd, [targets.Hdr.View, targets.Scatter.View], extent, lightingSets, []);
        _api.EndColorTarget(cmd, targets.Scatter.Image);
        _gpuTimer.Mark(cmd);

        // The diffuse light of skin and wax spreads across the screen, then down it and
        // onto the lit image. Nothing to spread when no material scatters.
        if (_resources.AnySubsurface)
        {
            var across = BlurConstants.Across;
            ReadOnlySpan<DescriptorSet> acrossSets = [frameSet, _scatterAcrossSet, _resources.Set];
            _api.BeginColorTarget(cmd, targets.ScatterBlurred.Image);
            _scatterAcrossPass.Record(cmd, targets.ScatterBlurred.View, extent, acrossSets,
                new ReadOnlySpan<byte>(&across, (int)BlurConstants.Size));
            _api.EndColorTarget(cmd, targets.ScatterBlurred.Image);

            var down = BlurConstants.Down;
            ReadOnlySpan<DescriptorSet> downSets = [frameSet, _scatterDownSet, _resources.Set];
            _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
                ColorWrite | AccessFlags2.ColorAttachmentReadBit);
            _scatterDownPass.Record(cmd, targets.Hdr.View, extent, downSets,
                new ReadOnlySpan<byte>(&down, (int)BlurConstants.Size));
        }

        _gpuTimer.Mark(cmd);

        // Glass and particles blend over the lit image, furthest first. Depth is read-only
        // throughout: glass tests against it, and both read it to see what's behind them.
        _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
            AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
        _transparentPass.Record(cmd, targets, frameSet, draws, _meshes, _details, instances, view,
            _particlePass, _frameIndex, particleCount, _particleCamera, _windReach);

        // The probe debug view draws its spheres over everything, glass included. With the probes off there are none,
        // and no atlases to read.
        if (probeView && _lightProbesInUse)
        {
            uint spheres = (uint)_probeCascades.Layout(0).Total;
            if (_probeGrid is { } grid)
            {
                var (x, y, z) = ProbeGrid.Counts(grid);
                spheres += (uint)(x * y * z);
            }

            _probeDebugPass.Record(cmd, targets, frameSet, _lightingSet, spheres);
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

        _decalPass.Record(cmd, targets, frameSet, _frameIndex);
        return true;
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

    /// <summary>Returns whether a light casts a shadow this frame.</summary>
    private bool WriteUniforms(
        in Frame frame,
        in FrameView view,
        in Matrix4x4 previousViewProjection,
        Vector2 shift,
        int frameNumber,
        bool historyValid,
        bool capturingProbe,
        ReadOnlySpan<Vector4> casters)
    {
        var viewProjection = view.View * view.Projection * Matrix4x4.CreateTranslation(shift.X, shift.Y, 0f);
        Matrix4x4.Invert(viewProjection, out var inverse);

        var uniforms = (FrameUniforms*)frame.Uniforms.Mapped;
        uniforms->ViewProjection = viewProjection;
        uniforms->View = view.View;
        uniforms->InverseViewProjection = inverse;
        uniforms->PreviousViewProjection = previousViewProjection;
        uniforms->FrameNumber = frameNumber;
        uniforms->HistoryValid = historyValid ? 1 : 0;
        uniforms->ShowEnvironment = capturingProbe || view.ShowEnvironment ? 1 : 0;
        uniforms->Grade = new Vector4(
            MathF.Pow(2f, view.Post.Exposure), 1f + view.Post.Contrast, 1f + view.Post.Saturation, view.Post.Vignette);
        uniforms->Finish = new Vector4(view.Post.Sharpen, view.Post.Grain, shift.X, shift.Y);
        (uniforms->Lift, uniforms->Gain) = PostGrade.Tint(view.Post);
        uniforms->WhiteBalance = PostGrade.WhiteBalance(view.Post.Temperature, view.Post.Tint);
        uniforms->Curve = PostGrade.Curve(view.Post);
        (uniforms->HazeOrigin, uniforms->HazeColor) = Haze.Pack(view.CameraPosition, view.BackgroundFadeOrigin, view.BackgroundFadeColor);
        uniforms->Occlusion = Occlusion.Pack(view.Occlusion);
        uniforms->Fog = Fog.Pack(view.Fog, _off, capturingProbe, underwater: view.Water.Absorption != Vector3.Zero);
        var volumetric = VolumetricFog.Pack(view.Fog, _off, capturingProbe, underwater: view.Water.Absorption != Vector3.Zero);
        if (!_volumetricFog.HasTargets)
        {
            volumetric = Vector4.Zero;
        }

        volumetric.W = historyValid && _volumetricFog.HistoryReady ? 1f : 0f;
        uniforms->Volumetric = volumetric;
        uniforms->Puddles = Puddles.Pack(view.Puddles, _off);
        uniforms->Wind = Wind.Pack(view.Wind, _off);
        _volumetricDrawn = volumetric.X > 0f;
        if (!capturingProbe && !_volumetricDrawn)
        {
            // A frame without the fog leaves nothing for the next to carry on from.
            _volumetricFog.Skip();
        }

        uniforms->ProbeCount = 0;
        if (!capturingProbe)
        {
            _probes.CopyToUniforms(uniforms);
        }

        // The fixed grid, read wherever it has a probe updated. A reflection probe's capture leaves
        // the light probes out, as before.
        if (!capturingProbe && _lightProbesInUse && view.LightProbes is { } lightProbes)
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
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var origin = _probeCascades.Origin(c);
            uniforms->ProbeCascadeOrigins[c] = new Vector4(origin.X, origin.Y, origin.Z, _probeCascades.Layout(c).Spacing);
        }

        bool readCascades = !capturingProbe && _lightProbesInUse && _lightProbes.CascadesWritten;
        var cascadeLayout = _probeCascades.Layout(0);
        uniforms->ProbeCascadeCounts = new Vector4(
            cascadeLayout.CountX, cascadeLayout.CountY, cascadeLayout.CountZ, readCascades ? 1f : 0f);

        // Inside the baked space's box, a fresh bake's probes give the soft light, blended into the live light over one
        // probe spacing in from the box's faces, or, with BakedProbeBand off for a test, cut off hard at them. The
        // shader's box is the probe grid's Min..Max, which is the space's box: the space's box is in the bake's input
        // hash, so a fresh bake's grid matches it.
        if (_activeBakedProbes is { } baked)
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
        uniforms->BakedFade = new Vector4(_spaceFade, 0f, 0f, 0f);

        uniforms->CameraPosition = new Vector4(view.CameraPosition, 1f);
        uniforms->BackgroundRadiance = new Vector4(view.BackgroundRadiance, view.EnvironmentIntensity);

        // Probes leave the sun's disc out: their rough levels would scatter it into specks, and
        // the sun's own light already gives surfaces its highlight. The sky is the one BakeSky worked out from the
        // original view, so a frame drawn without its baked sun still shows that sun's disc.
        var sky = _bakedSky;
        uniforms->SkySun = new Vector4(sky.Direction, sky.Turbidity);
        var disc = new Vector4(capturingProbe ? Vector3.Zero : sky.DiscRadiance, sky.DiscCosine);
        uniforms->SunDisc = disc;
        if (!capturingProbe)
        {
            LastSunDisc = disc;
        }

        // Moving points two texels of the first cascade off their surface stops them
        // shadowing themselves. The lighting pass works this out per cascade itself.
        float normalOffset = view.Shadows.Radius * 4f / _shadowPass.Resolution;
        uniforms->Parameters = new Vector4(
            view.BackgroundFadeStart, view.BackgroundFadeEnd, view.Shadows.LightAngle, normalOffset);

        ref readonly var water = ref view.Water;
        uniforms->WaterAbsorption = new Vector4(water.Absorption, water.Absorption != Vector3.Zero ? 1f : 0f);
        uniforms->WaterShallow = new Vector4(water.ShallowColor, water.SurfaceHeight);
        uniforms->WaterDeep = new Vector4(water.DeepColor, water.ShaftStrength);
        uniforms->WaterLight = new Vector4(water.CausticStrength, water.CausticScale, water.ShaftDistance, 0f);

        int count = Math.Min(view.Lights.Length, FrameUniforms.MaxLights);
        uniforms->LightCount = count;
        uniforms->Time = view.Time;
        uniforms->PreviousTime = historyValid ? _previousTime : view.Time;
        _shadowCascades = ShadowPass.CascadeCount(view.Shadows);
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
                uniforms->ShadowRadii = FitCascades(view, direction, fitToView: !capturingProbe, casters, out uniforms->ShadowFade, out uniforms->ShadowDepthScales);
                uniforms->ShadowFade.W = ShadowPass.ContactLength(view.Shadows, _off, capturingProbe);
                for (int cascade = 0; cascade < _shadowCascades; cascade++)
                {
                    uniforms->ShadowMatrices[cascade] = _shadowMatrices[cascade];
                }

                castsShadow = true;
            }

            uniforms->Lights[i] = new GpuLight
            {
                Direction = new Vector4(direction, casts ? 1f : 0f),
                Radiance = new Vector4(light.Radiance, i == _maskSun ? 0f : -1f),
            };
        }

        // With no sun shadow there are no maps to look up, so nothing walks the cascades.
        uniforms->ShadowCascades = castsShadow ? _shadowCascades : 0;
        uniforms->Off = (int)_off | (_traceView && !capturingProbe ? DebugViewBit : 0);
        uniforms->MipBias = capturingProbe || _off.HasFlag(RenderEffects.AntiAliasing) ? 0f
            : _upscaling ? FsrMipBias(_gBuffer.Extent.Width, _gBuffer.Display.Width)
            : TemporalMipBias;
        _sunShadowsDrawn = castsShadow;
        WriteLocalLights(frame, uniforms, view, capturingProbe);

        // Probe updates light their faces with every point and spot light of the frame, and see the
        // sun's shadow through a map of their own, which is zero when there is none this frame.
        uniforms->ProbeLightCount = capturingProbe ? 0 : _localLights.ProbeLightCount;
        uniforms->ProbeSunMatrix = !capturingProbe && _probeSun is not null ? _probeSun.Matrix : default;
        return castsShadow;
    }

    /// <summary>
    /// Picks the point and spot lights the view needs and their shadow tiles, and writes
    /// them where the shaders read them. Probe captures leave them out, so a reflection
    /// shows the room by the sun and sky alone.
    /// </summary>
    private void WriteLocalLights(in Frame frame, FrameUniforms* uniforms, in FrameView view, bool capturingProbe)
    {
        if (view.LocalLights.Length > LocalLight.MaxPerFrame)
        {
            throw new ArgumentException(
                $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights, not {view.LocalLights.Length}.", nameof(view));
        }

        if (capturingProbe)
        {
            _tileCount = 0;
            _clustersNeeded = false;
            uniforms->LocalLightCount = 0;
            return;
        }

        _localLights.Prepare(
            view.LocalLights,
            view.View,
            view.Projection,
            (int)_gBuffer.Extent.Height,
            new Span<GpuLocalLight>(frame.Lights.Mapped, LocalLight.MaxPerFrame),
            new Span<GpuShadowTile>(frame.ShadowTiles.Mapped, LocalLights.MaxTiles),
            shadows: !_off.HasFlag(RenderEffects.LightShadows),
            maskChannels: _maskChannels);
        _tileCount = _localLights.TileCount;
        _clustersNeeded = _localLights.LightCount > 0;
        uniforms->LocalLightCount = _localLights.LightCount;
        uniforms->Clusters = new Vector4(_localLights.NearDepth, _localLights.FarDepth, _localLights.Parallel ? 1f : 0f, 0f);
        uniforms->Screen = new Vector4(_gBuffer.Extent.Width, _gBuffer.Extent.Height, 1f / _gBuffer.Extent.Width, 1f / _gBuffer.Extent.Height);
    }

    /// <summary>
    /// Places each cascade's map. In a perspective view each ball takes the next slice of
    /// what the camera sees, starting at the camera, so none of it is spent behind. Its radius
    /// never changes as the camera turns, and its centre moves in whole texels, so edges don't
    /// shimmer. A parallel view, or a probe's capture, keeps every cascade centred on the
    /// settings' centre. Returns
    /// each cascade's radius, and how shadows fade out with distance.
    /// </summary>
    private Vector4 FitCascades(
        in FrameView view, Vector3 direction, bool fitToView, ReadOnlySpan<Vector4> casters, out Vector4 fade, out Vector4 depthScales)
    {
        var radii = Vector4.Zero;
        depthScales = Vector4.One;
        float? spread = fitToView ? ShadowPass.Spread(view.Projection) : null;
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
        _api.DeviceWaitIdle(_context.Device).Check();
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
        _gBuffer = new GBuffer(_context, scene, _upscaling ? _swapchain.Extent : scene);
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
                _gBuffer = new GBuffer(_context, scene);
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
            _volumetricFog.SetSize((int)_gBuffer.Extent.Width, (int)_gBuffer.Extent.Height);
        }

        WriteTargetDescriptors();
        _historyReady = false;
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
            _context, (ulong)(ChunkHeaderBytes + BakeFiles.MaxChunks * sizeof(GpuChunk)), BufferUsageFlags.StorageBufferBit);
        frame.Set = AllocateSet(_frameSetLayout);

        var chunksInfo = new DescriptorBufferInfo(frame.Chunks.Handle, 0, frame.Chunks.Size);
        var uniformInfo = new DescriptorBufferInfo(frame.Uniforms.Handle, 0, (ulong)sizeof(FrameUniforms));
        var bonesInfo = new DescriptorBufferInfo(frame.Bones.Handle, 0, frame.Bones.Size);
        var lightsInfo = new DescriptorBufferInfo(frame.Lights.Handle, 0, frame.Lights.Size);
        var tilesInfo = new DescriptorBufferInfo(frame.ShadowTiles.Handle, 0, frame.ShadowTiles.Size);
        var clustersInfo = new DescriptorBufferInfo(frame.Clusters.Handle, 0, frame.Clusters.Size);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.Set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &uniformInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.Set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bonesInfo,
            },
            StorageWrite(frame.Set, 3, &lightsInfo),
            StorageWrite(frame.Set, 4, &tilesInfo),
            StorageWrite(frame.Set, 5, &clustersInfo),
            StorageWrite(frame.Set, 11, &chunksInfo),
        };
        _api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);

        frame.Instances = new InstanceBuffer(_context);
        WriteInstanceDescriptor(frame);
        return frame;
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

    private static WriteDescriptorSet StorageWrite(DescriptorSet set, uint binding, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = info,
    };

    /// <summary>The shadow atlas, read once with a depth comparison and once as plain depth.</summary>
    private void WriteAtlasDescriptors(in Frame frame)
    {
        var compare = new DescriptorImageInfo(_shadowSampler, _shadowPass.Atlas.View, ImageLayout.ShaderReadOnlyOptimal);
        var depth = new DescriptorImageInfo(_sampler, _shadowPass.Atlas.View, ImageLayout.ShaderReadOnlyOptimal);
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
        var info = new DescriptorImageInfo(_linearSampler, _volumetricFog.Result, ImageLayout.General);
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
        var info = new DescriptorBufferInfo(frame.Instances.Handle, 0, frame.Instances.Size);
        var previousInfo = new DescriptorBufferInfo(frame.Instances.PreviousHandle, 0, frame.Instances.PreviousSize);
        var lightmapInfo = new DescriptorBufferInfo(frame.Instances.LightmapHandle, 0, frame.Instances.LightmapSize);
        var writes = stackalloc WriteDescriptorSet[]
        {
            StorageWrite(frame.Set, 2, &info),
            StorageWrite(frame.Set, 9, &previousInfo),
            StorageWrite(frame.Set, 10, &lightmapInfo),
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

    /// <summary>
    /// The frame uniforms at binding 0, the skin matrices at binding 1 and each copy's
    /// model matrix at binding 2, as Shaders/common.glsl, skinning.glsl and instances.glsl
    /// read them. Bindings 3 to 5 hold the point and spot lights, their shadow tiles and
    /// the cluster lists, and 6 and 7 the shadow atlas, as Shaders/light_data.glsl and
    /// local_shadows.glsl read them, 8 the volumetric fog's summed volume, as
    /// volumetric.glsl reads it, 9 where each copy stood last frame, as instances.glsl
    /// reads it for motion vectors, and 10 where each copy reads its baked light. 11 holds
    /// the frame's suns and placed chunks and 12 the sky projected by SkySh, as
    /// chunks.glsl reads them, to relight every copy of a chunk's bake.
    /// </summary>
    private DescriptorSetLayout CreateFrameSetLayout()
    {
        const ShaderStageFlags everyStage = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.UniformBuffer, 1, everyStage),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(3, DescriptorType.StorageBuffer, 1, everyStage),
            new(4, DescriptorType.StorageBuffer, 1, everyStage),
            new(5, DescriptorType.StorageBuffer, 1, everyStage),
            new(6, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit),
            new(7, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(8, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(9, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(11, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
            new(12, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 13,
            PBindings = bindings,
        };
        _api.CreateDescriptorSetLayout(_context.Device, &info, null, out var layout).Check();
        return layout;
    }

    private DescriptorSetLayout CreateSetLayout(DescriptorType type, int bindingCount, ShaderStageFlags stages)
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[bindingCount];
        for (int i = 0; i < bindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, type, 1, stages);
        }

        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)bindingCount,
            PBindings = bindings,
        };
        _api.CreateDescriptorSetLayout(_context.Device, &info, null, out var layout).Check();
        return layout;
    }

    private DescriptorSet AllocateSet(DescriptorSetLayout layout)
    {
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        _api.AllocateDescriptorSets(_context.Device, &info, out var set).Check();
        return set;
    }

    /// <summary>Points the fullscreen passes at the current render targets.</summary>
    private void WriteTargetDescriptors()
    {
        var depth = new DescriptorImageInfo(_sampler, _gBuffer.Depth.View, ImageLayout.ShaderReadOnlyOptimal);
        var normal = new DescriptorImageInfo(_sampler, _gBuffer.Normal.View, ImageLayout.ShaderReadOnlyOptimal);
        _decalPass.WriteTargets(depth, new DescriptorImageInfo(_sampler, _gBuffer.NormalCopy.View, ImageLayout.ShaderReadOnlyOptimal));

        var shadowDepth = new DescriptorImageInfo(_sampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal);

        WriteImages(_aoSet, [depth, normal, shadowDepth]);
        WriteImages(_aoBlurSet,
        [
            new(_sampler, _gBuffer.Ao.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
        ]);
        WriteImages(_lightingSet,
        [
            new(_sampler, _gBuffer.BaseColor.View, ImageLayout.ShaderReadOnlyOptimal),
            normal,
            depth,
            shadowDepth,
            new(_shadowSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Reflections, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Emissive.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.AoBlurred.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _gBuffer.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _probes.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _lightProbes.IrradianceView, ImageLayout.General),
            new(_linearSampler, _lightProbes.DepthView, ImageLayout.General),
            new(_sampler, _gBuffer.Baked.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Mask.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);

        // Glass and particles read depth while it's also attached for glass to test against.
        var testedDepth = new DescriptorImageInfo(_sampler, _gBuffer.Depth.View, ImageLayout.DepthReadOnlyOptimal);
        _transparentPass.WriteTargets(new(_mipSampler, _gBuffer.SceneColor.View, ImageLayout.ShaderReadOnlyOptimal), testedDepth);

        WriteImages(_scatterAcrossSet,
        [
            new(_sampler, _gBuffer.Scatter.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
        ]);
        WriteImages(_scatterDownSet,
        [
            new(_sampler, _gBuffer.ScatterBlurred.View, ImageLayout.ShaderReadOnlyOptimal),
            depth,
        ]);
        _particlePass.WriteTargets(
            testedDepth,
            new(_shadowSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Reflections, ImageLayout.ShaderReadOnlyOptimal));

        // Glass has just used depth for testing, and it stays in that layout.
        WriteImages(_taaSet,
        [
            new(_sampler, _gBuffer.Hdr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _gBuffer.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Depth.View, ImageLayout.DepthReadOnlyOptimal),
            new(_sampler, _gBuffer.Motion.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        WriteImages(_tonemapSet,
        [
            new(_sampler, DisplaySource, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _bloomPass.Result, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        _exposureMeter.SetSource(DisplaySource, _gBuffer.Display);
        WriteImages(_finishSet,
        [
            new(_linearSampler, _gBuffer.Ldr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _gBuffer.Held.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);

        _volumetricFog.SetSources(
            new(_shadowSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_environment.Sampler, _environment.Reflections, ImageLayout.ShaderReadOnlyOptimal));
        foreach (ref var frame in _frames.AsSpan())
        {
            WriteVolumetricDescriptor(frame);
        }

        // New targets start empty, so neither a held picture nor a last frame survives them.
        _heldReady = false;
        _ldrReady = false;
    }

    private void WriteImages(DescriptorSet set, ReadOnlySpan<DescriptorImageInfo> images)
    {
        var writes = stackalloc WriteDescriptorSet[images.Length];
        fixed (DescriptorImageInfo* imagesPtr = images)
        {
            for (int i = 0; i < images.Length; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imagesPtr[i],
                };
            }

            _api.UpdateDescriptorSets(_context.Device, (uint)images.Length, writes, 0, null);
        }
    }
}
