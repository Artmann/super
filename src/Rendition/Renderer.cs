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
        public DescriptorSet Set;
    }

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
    private readonly EnvironmentMap _environment;
    private readonly ReflectionProbes _probes;

    // The light probe volume, the small targets its faces are drawn to, and this frame's state:
    // whether its faces are being drawn now, whether surfaces read it, and whether the sun's
    // maps from the first face of a batch still hold, since every probe uses the same ones.
    private readonly ProbeVolume _lightProbes;
    private readonly VolumetricFogVolume _volumetricFog;
    private GBuffer? _lightProbeTargets;
    private GBuffer? _reflectionProbeTargets;
    private bool _capturingLightProbes;
    private bool _lightProbesInUse;
    private bool _keepShadowMaps;

    // Goes up with every SetEnvironment, so the light probes see the sky has changed.
    private int _environmentVersion;

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
    private bool _historyReady;
    private Matrix4x4 _previousViewProjection;
    // Last frame's time and where its keyed draws stood, for motion vectors.
    private float _previousTime;
    private readonly MotionHistory _motion = new();

    // The effects this frame leaves out, and whether tonemapping and bloom are bound to the
    // anti-aliased image or, with anti-aliasing left out, straight to the lit one.
    private RenderEffects _off;

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
        _lightingSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 14, ShaderStageFlags.FragmentBit);
        _threeTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 3, ShaderStageFlags.FragmentBit);
        _fourTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 4, ShaderStageFlags.FragmentBit);
        _twoTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 2, ShaderStageFlags.FragmentBit);
        _oneTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.UniformBuffer, FramesInFlight),
            new(DescriptorType.StorageBuffer, 6 * FramesInFlight),
            new(DescriptorType.CombinedImageSampler, 32 + 3 * FramesInFlight),
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
        _probes = new ReflectionProbes(_context, _environment.Sampler);
        _lightProbes = new ProbeVolume(_context, _sampler);
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
    }

    /// <summary>
    /// Hands a mesh's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be drawn in that same frame.
    /// </summary>
    public MeshHandle CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var vertexBuffer = GpuBuffer.CreateDeviceLocalStaged(_context, vertices, BufferUsageFlags.VertexBufferBit, out var vertexStaging);
        _uploads.Add(vertexStaging, vertexBuffer);
        var indexBuffer = GpuBuffer.CreateDeviceLocalStaged(_context, indices, BufferUsageFlags.IndexBufferBit, out var indexStaging);
        _uploads.Add(indexStaging, indexBuffer);
        return AddMesh(new Mesh(vertexBuffer, indexBuffer, (uint)indices.Length));
    }

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
        _uploads.Cancel(_resources.MarkTextureDying(slot));
        Destroying().Textures.Add(slot);
    }

    private void QueueMaterialDestroy(int slot)
    {
        _resources.MarkMaterialDying(slot);
        Destroying().Materials.Add(slot);
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
    }

    /// <summary>
    /// Bakes the environment again with the view's clear sky when it or its sun has changed. A
    /// gradient sky gives the same empty <see cref="SkySun"/> whatever the sun does, so that never bakes.
    /// </summary>
    private void BakeSky(in FrameView view)
    {
        var sky = SkyModel.Sun(view);
        if (sky == _bakedSky)
        {
            return;
        }

        _api.DeviceWaitIdle(_context.Device).Check();
        _environment.Rebake(sky);
        _bakedSky = sky;
    }

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
        return _resources.AddTexture(texture, edges);
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
    /// they have settled, and the switches for sun shadows, materials and ambient occlusion. Moving
    /// things or the camera doesn't change it. Point and spot lights aren't in it, because captures
    /// leave them out, and nor are the contact shadow and light shadow switches, which only touch
    /// what captures leave out.
    /// </summary>
    public int ReflectionLightingHash(in FrameView view)
    {
        // Light probes fill in over a few frames, and probes captured before then look wrong.
        bool lightProbesSettled = _lightProbesInUse && _lightProbes.Schedule.Round > 2;
        return HashCode.Combine(ReflectionLightingHash(view, _environmentVersion), _lightProbesInUse, lightProbesSettled);
    }

    /// <summary>The part of <see cref="ReflectionLightingHash(in FrameView)"/> that the view alone decides.</summary>
    internal static int ReflectionLightingHash(in FrameView view, int environmentVersion) =>
        HashCode.Combine(LightingHash(view, environmentVersion), view.Occlusion, view.Off & RenderEffects.AmbientOcclusion);

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
        // them forgets the held transition picture, so that is put back after, as light probes do.
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
    /// whose size the current targets must have, waiting for each face. With a
    /// <paramref name="lightProbe"/> index, the faces are then folded into that light probe.
    /// </summary>
    private void CaptureFaces(
        in FrameView view,
        Vector3 position,
        ReadOnlySpan<DrawItem> visible,
        ReadOnlySpan<DrawItem> shadowCasters,
        CaptureCube cube,
        int lightProbe = -1,
        float lightProbeSpacing = 0f)
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
            bool drawShadows = castsShadow && !_keepShadowMaps;
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
            if (lightProbe >= 0 && face == faces.Length - 1)
            {
                _lightProbes.RecordFold(cmd, lightProbe, lightProbeSpacing);
            }

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

            // Light probes all use maps round their volume's centre, whatever their position,
            // so the rest of the batch keeps the first face's.
            if (_capturingLightProbes && castsShadow)
            {
                _keepShadowMaps = true;
            }
        }
    }

    /// <summary>
    /// Captures the next few light probes of the view's volume, if any are due, before the frame
    /// is drawn. Up to <see cref="ProbeVolume.ProbesPerFrame"/> a frame, so a volume fills in over
    /// a few frames rather than in one long stall. Then decides whether this frame's surfaces read them.
    /// </summary>
    private void CaptureLightProbes(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        _lightProbesInUse = false;
        if (view.LightProbes is not { } settings || _off.HasFlag(RenderEffects.LightProbes) || !_lightProbes.Supported)
        {
            return;
        }

        var schedule = _lightProbes.Schedule;
        schedule.Update(settings, LightingHash(view, _environmentVersion));
        var (first, count) = schedule.Batch(ProbeVolume.ProbesPerFrame);
        if (count > 0)
        {
            // Frames in flight still read the targets and sets the captures borrow.
            _api.DeviceWaitIdle(_context.Device).Check();
            _uploads.Flush();

            var screenTargets = _gBuffer;
            bool heldReady = _heldReady;
            bool ldrReady = _ldrReady;
            var screenOff = _off;
            bool historyReady = _historyReady;
            _lightProbeTargets ??= new GBuffer(_context, new Extent2D(ProbeVolume.FaceSize, ProbeVolume.FaceSize));
            _gBuffer = _lightProbeTargets;
            WriteTargetDescriptors();

            // A probe keeps diffuse light only, and its faces are too small for screen-space occlusion.
            _off |= RenderEffects.AmbientOcclusion | RenderEffects.Reflections;
            _capturingLightProbes = true;

            // The capture's targets have no history, so RecordScene readies an empty one each face.
            _historyReady = false;
            try
            {
                var volumeView = view;
                volumeView.Shadows = CaptureShadows(view.Shadows, settings.Min, settings.Max);
                for (int i = 0; i < count; i++)
                {
                    int probe = first + i;
                    CaptureFaces(volumeView, ProbeGrid.Position(settings, probe), draws, draws, _lightProbes.Capture, probe, settings.Spacing);
                }
            }
            finally
            {
                _capturingLightProbes = false;
                _keepShadowMaps = false;
                _off = screenOff;
                _gBuffer = screenTargets;
                WriteTargetDescriptors();
                _heldReady = heldReady;
                _ldrReady = ldrReady;
                _historyReady = historyReady;
            }

            schedule.Captured(count);
        }

        _lightProbesInUse = schedule.Ready;
    }

    /// <summary>
    /// Sums up everything that lights the light probes: the suns' directions and colours, the
    /// environment and its intensity, the clear sky's turbidity, the background, and whether the sun
    /// casts a shadow and how soft it is. When it changes, the probes are captured again. The scene's geometry isn't in it,
    /// so moving things doesn't change it, and nor does the camera: captures frame the sun's shadow
    /// on their own box (<see cref="CaptureShadows"/>), so the view's shadow centre and radius are left out.
    /// </summary>
    internal static int LightingHash(in FrameView view, int environmentVersion)
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
            | RenderEffects.WearAndDirt));
        hash.Add(view.LightProbes);
        return hash.ToHashCode();
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
        _resources.UpdateMaterial(handle, material);
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

    public void Render(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (view.Width <= 0 || view.Height <= 0)
        {
            return; // Minimized: there is nothing to present to.
        }

        // FSR draws the picture at the window's size, so starting or stopping it changes the targets.
        bool upscaling = _upscalerUsed == Upscaler.Fsr && _renderScale <= 1f && !view.Off.HasFlag(RenderEffects.AntiAliasing);
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
        _off = Exposure.ShaderOff(view.Post, view.Off & RenderEffects.All);
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
        BakeSky(view);
        CaptureLightProbes(view, draws);

        ref Frame frame = ref _frames[_frameIndex];
        var fence = frame.InFlight;
        long waitStart = Stopwatch.GetTimestamp();
        _api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue).Check();
        ReleaseDestroyed(_frameIndex);

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
        if (_casterBounds.Length < draws.Length)
        {
            _casterBounds = new Vector4[draws.Length * 2];
        }

        int casters = DrawSelection.Bounds(draws, _details, _casterBounds, _windReach);
        bool castsShadow = WriteUniforms(
            frame, view, previous, shift, _frameNumber, _historyReady, capturingProbe: false, _casterBounds.AsSpan(0, casters));
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
        ResetInstances(frame, cameraCount + shadowCount + tileDrawCount);

        _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
        var cmd = frame.CommandBuffer;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _api.BeginCommandBuffer(cmd, &beginInfo).Check();
        _uploads.Record(cmd, _destroyed[_frameIndex].Others);
        int overlayQuads = _overlayAtlas is null ? 0 : _overlayPass.Upload(_frameIndex, view.Overlay);
        UploadParticles(view);

        // Light probe captures may have packed this frame's decals over again, but they are the same ones.
        PrepareDecals(view);
        _decalPass.Upload(_frameIndex);
        if (timed)
        {
            _gpuTimer.Begin(cmd, _frameIndex);
        }

        // Worked out here, just before the frame is recorded, so a frame that is never drawn doesn't move the clock.
        _exposureStep = _exposureClock.Next(
            !_off.HasFlag(RenderEffects.AutoExposure), view.Time, Exposure.Speed(view.Post.AdaptationSpeed));
        RecordFrame(cmd, frame.Set, imageIndex, _cameraDraws.AsSpan(0, cameraCount), overlayQuads);
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
        int cameraCount = DrawSelection.Select(camera, draws, _details, _cameraDraws, _windReach);

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
        Render(view, draws);
        _capture = null;
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
        for (int slot = 0; slot < FramesInFlight; slot++)
        {
            ReleaseDestroyed(slot);
        }

        _gpuTimer.Dispose();
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
        _lightProbeTargets?.Dispose();
        _reflectionProbeTargets?.Dispose();
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
        int overlayQuads)
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

        RecordScene(cmd, frameSet, draws, [], shadowsSelected: true, _transparentView, _particleCount, _motion);

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
    /// Draws and lights the scene into the HDR target, for frames and probe captures
    /// alike. A frame's shadow draws were picked per cascade by <see cref="SelectDraws"/>;
    /// a probe capture draws <paramref name="shadowCasters"/> into every cascade.
    /// Probes leave particles out, since a puff of dust shouldn't stay in a reflection.
    /// Only a frame passes <paramref name="motion"/>: where its draws stood last frame, for motion
    /// vectors. A probe's motion is never read, so its draws stand still.
    /// </summary>
    private void RecordScene(
        CommandBuffer cmd,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        ReadOnlySpan<DrawItem> shadowCasters,
        bool shadowsSelected,
        in TransparentView view,
        int particleCount = 0,
        MotionHistory? motion = null)
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
        // A batch of light probes draws the maps once and the rest keep them as they are.
        var shadowMap = _shadowPass.Target.Image;
        if (_sunShadowsDrawn && !_keepShadowMaps)
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
        else if (!_sunShadowsDrawn)
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
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, DepthStages, AccessFlags2.DepthStencilAttachmentReadBit, DepthStages,
            DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);

        // A light probe must see the backs of surfaces to know it is inside a wall.
        instances.History = motion;
        _gBufferPass.Record(cmd, targets, frameSet, draws, _meshes, _details, instances, allTwoSided: _capturingLightProbes);
        instances.History = null;
        bool decalsDrawn = RecordDecals(cmd, targets, frameSet);

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Emissive.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Motion.Image, ImageAspectFlags.ColorBit,
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

        _api.BeginColorTarget(cmd, targets.Hdr.Image);
        _api.BeginColorTarget(cmd, targets.Scatter.Image);

        ReadOnlySpan<DescriptorSet> lightingSets = [frameSet, _lightingSet, _resources.Set];
        _lightingPass.Record(cmd, [targets.Hdr.View, targets.Scatter.View], extent, lightingSets, []);
        _api.EndColorTarget(cmd, targets.Scatter.Image);
        _gpuTimer.Mark(cmd);

        // The diffuse light of skin and wax spreads across the screen, then down it and
        // onto the lit image. Nothing to spread when no material scatters, and a light probe
        // keeps the scattered light as it is, as its alpha holds distance.
        if (_resources.AnySubsurface && !_capturingLightProbes)
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
        // A light probe leaves them out, as blending would spoil the distances in its alpha.
        if (!_capturingLightProbes)
        {
            _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
                ColorWrite | AccessFlags2.ColorAttachmentReadBit);
            _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
                AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
            _transparentPass.Record(cmd, targets, frameSet, draws, _meshes, _details, instances, view,
                _particlePass, _frameIndex, particleCount, _particleCamera, _windReach);
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

        // A light probe capture reads the probes only in its second round, to add one more bounce.
        uniforms->ProbeCapture = _capturingLightProbes ? 1 : 0;
        if (view.LightProbes is { } lightProbes && (_capturingLightProbes || _lightProbesInUse))
        {
            var (x, y, z) = ProbeGrid.Counts(lightProbes);
            bool lookup = _capturingLightProbes ? _lightProbes.Schedule.CapturesBounce : _lightProbesInUse;
            uniforms->LightProbeMin = new Vector4(lightProbes.Min, lightProbes.Spacing);
            uniforms->LightProbeCounts = new Vector4(x, y, z, lookup ? 1f : 0f);
        }
        else
        {
            uniforms->LightProbeMin = Vector4.Zero;
            uniforms->LightProbeCounts = Vector4.Zero;
        }

        uniforms->CameraPosition = new Vector4(view.CameraPosition, 1f);
        uniforms->BackgroundRadiance = new Vector4(view.BackgroundRadiance, view.EnvironmentIntensity);

        // Probes leave the sun's disc out: their rough levels would scatter it into specks, and
        // the sun's own light already gives surfaces its highlight.
        var sky = SkyModel.Sun(view);
        uniforms->SkySun = new Vector4(sky.Direction, sky.Turbidity);
        uniforms->SunDisc = new Vector4(capturingProbe ? Vector3.Zero : sky.DiscRadiance, sky.DiscCosine);

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
                Radiance = new Vector4(light.Radiance, 0f),
            };
        }

        // With no sun shadow there are no maps to look up, so nothing walks the cascades.
        uniforms->ShadowCascades = castsShadow ? _shadowCascades : 0;
        uniforms->Off = (int)_off;
        uniforms->MipBias = capturingProbe || _off.HasFlag(RenderEffects.AntiAliasing) ? 0f
            : _upscaling ? FsrMipBias(_gBuffer.Extent.Width, _gBuffer.Display.Width)
            : TemporalMipBias;
        _sunShadowsDrawn = castsShadow;
        WriteLocalLights(frame, uniforms, view, capturingProbe);
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
            shadows: !_off.HasFlag(RenderEffects.LightShadows));
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

    private void Resize(uint width, uint height)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _gBuffer.Dispose();
        _swapchain.Dispose();
        _swapchain = new Swapchain(_context, width, height, _disableVsync);
        _vsyncChanged = false;
        CreateTargets();
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
        frame.Set = AllocateSet(_frameSetLayout);

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
        };
        _api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);

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
        var writes = stackalloc WriteDescriptorSet[]
        {
            StorageWrite(frame.Set, 2, &info),
            StorageWrite(frame.Set, 9, &previousInfo),
        };
        _api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
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
    /// volumetric.glsl reads it, and 9 where each copy stood last frame, as instances.glsl
    /// reads it for motion vectors.
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
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 10,
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
            new(_environment.Sampler, _environment.Sky, ImageLayout.ShaderReadOnlyOptimal),
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
