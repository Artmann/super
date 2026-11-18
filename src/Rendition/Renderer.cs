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
    public const int MaxBones = FrameSlots.MaxBones;

    /// <summary>The most reflection probes the renderer holds at once.</summary>
    public const int MaxReflectionProbes = ReflectionProbeOrder.MaxProbes;

    private readonly VulkanContext _context;
    // The largest image side the device can make, which the scene is shrunk to fit.
    private readonly int _maxImageSize;
    private readonly Vk _api;
    private readonly FrameSlots _frames;
    // How far a simpler model may stray from the camera's view. Shadows, which are soft, afford more.
    private const float CameraDetailError = 1f;

    private readonly FrameRing _ring = new();
    private readonly MeshStore _meshStore = new();
    private readonly RetiredResources _retired;
    private readonly UploadQueue _uploads;
    private readonly ResourceTable _resources;
    private readonly RayTracing? _tracing;
    private readonly TraceScene? _traceScene;
    private readonly TraceDebugPass? _traceDebugPass;

    // What this frame leaves out, and the debug view it shows.
    private readonly FrameSwitches _switches = new();
    private readonly EnvironmentSky _sky;
    private readonly ReflectionProbes _probes;

    private readonly FogStage _fog;

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

    // The view's lights with the sun coloured by the physical sky's air; see SkyModel.Tinted.
    private DirectionalLight[] _tintedLights = [];

    private readonly DescriptorKit _kit;
    private readonly HistoryCopier _copier;
    private readonly OcclusionStage _occlusion;
    private readonly LightingStage _lighting;

    private readonly ShadowPass _shadowPass;

    // The G-buffer and the decals, and what blends over the lit image.
    private readonly OpaqueStage _opaque;
    private readonly BlendStage _blend;

    // The camera's view as SelectDraws made it, which decides whose depth the G-buffer pass draws first.
    private DetailView _cameraView;

    private DetailView? DepthFirstView => _switches.Off.HasFlag(RenderEffects.DepthFirst) ? null : _cameraView;

    // From the lit image to the window: anti-aliasing, the lens, bloom and tonemapping, the held picture, and the finish.
    private readonly ResolveStage _resolve;
    private readonly PostStage _post;
    private readonly HeldPicture _held;
    private readonly FinishStage _finish;
    private readonly GpuTimer _gpuTimer;
    private readonly GpuWorkCounter? _gpuWork;

    private Swapchain _swapchain;
    private GBuffer _gBuffer;
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

    /// <summary>OFF_DEBUG_VIEW in common.glsl: tonemapping and the finish pass pass colours through untouched.</summary>
    private const int DebugViewBit = 1 << 30;

    /// <summary>
    /// OFF_FADES in common.glsl, for <see cref="RenderEffects.Fades"/>, which is past the bits the shaders read. It
    /// takes the bit of <see cref="RenderEffects.DepthFirst"/>, which only the CPU reads.
    /// </summary>
    private const int FadesOffBit = 1 << 29;

    /// <summary>The effects the shaders read through <c>frame.off</c>, an <c>int</c>. The renderer leaves out the rest itself.</summary>
    private const RenderEffects ShadedEffects = (RenderEffects)0xFFFF_FFFFL;

    // Whether tonemapping and bloom are bound to the anti-aliased image or, with anti-aliasing left out, straight to
    // the lit one.
    private bool _readsAntiAliased = true;

    // Whether FSR draws the picture now: only with anti-aliasing on and the scene no larger than the window.
    private bool _upscaling;
    private readonly FsrPass _fsrPass;
    private FsrFrame _fsrFrame;


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

    /// <summary>How the renderer draws: the quality and cost settings that take effect on the next frame.</summary>
    public RenderSettings Settings { get; }

    /// <summary>The FSR version in use, such as "FSR 3.1.4", or null while FSR isn't drawing the picture.</summary>
    public string? UpscalerVersion => _upscaling ? _fsrPass.Version : null;

    /// <summary>Times from the newest finished frame, while <see cref="RenderSettings.MeasureGpu"/> is on. Empty if the GPU can't time passes.</summary>
    public ReadOnlySpan<GpuPassTime> GpuTimes => _gpuTimer.Times;

    /// <summary>
    /// The shaders each pass ran in the newest finished frame, while <see cref="RenderSettings.MeasureGpu"/> is on. Empty if the GPU
    /// can't count them. A pass that is switched off runs none.
    /// </summary>
    public ReadOnlySpan<GpuPassWork> GpuWork => _gpuWork is { } counter ? counter.Work : default;

    /// <summary>How long the last frame waited for the GPU to finish earlier work and for the display, while <see cref="RenderSettings.MeasureGpu"/> is on.</summary>
    public double WaitMilliseconds { get; private set; }

    /// <summary>Triangles in the last frame's camera and shadow passes, while <see cref="RenderSettings.MeasureGpu"/> is on.</summary>
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

    // The world-space ball round each draw this frame, for fitting sun shadows to what casts them.
    private Vector4[] _casterBounds = [];

    // The sun's cascades, and the point and spot lights with their shadow tiles.
    private readonly SunShadows _sunShadows;
    private readonly LocalLightShadows _localShadows;

    public Renderer(RendererOptions options)
    {
        _context = new VulkanContext(options);
        _api = _context.Api;
        _uploads = new UploadQueue(_context);

        Settings = new RenderSettings(options);
        _swapchain = new Swapchain(_context, (uint)options.Width, (uint)options.Height, !Settings.Vsync);
        _api.GetPhysicalDeviceProperties(_context.PhysicalDevice, out var deviceProperties);
        _maxImageSize = (int)Math.Min(deviceProperties.Limits.MaxImageDimension2D, int.MaxValue);
        _gBuffer = new GBuffer(_context, SceneExtent(_swapchain.Extent));

        _kit = new DescriptorKit(_context);
        _frames = new FrameSlots(_context, _kit);

        _resources = new ResourceTable(_context);
        _retired = new RetiredResources(_ring, _meshStore, _resources);
        _chunkPool = new ChunkBakePool(_context, _resources, ChunkBakePool.ChunkUploadBudget, _readableBakes.ChunkUnreadable);
        if (_context.CanTrace)
        {
            _tracing = new RayTracing(_context, _resources, (MeshFor, _uploads.IsPending),
                (AfterSubmitted: structure => _retired.AfterLastSubmitted.FreedOthers.Add(structure),
                AfterRecorded: used => _retired.AfterRecording.FreedOthers.Add(used)));
            _traceScene = new TraceScene(TraceFlagsOf);
        }

        _traceDebugPass = _tracing is null ? null : new TraceDebugPass(_context, _tracing, FrameRing.Count, _kit.FrameSetLayout, _resources.Layout);

        _shadowPass = new ShadowPass(_context, _kit.FrameSetLayout, _resources, Settings.ShadowResolution);
        _frames.WriteAtlas(_shadowPass.Atlas.View);

        _sunShadows = new SunShadows(_api, _shadowPass, _meshStore, _switches);
        _localShadows = new LocalLightShadows(_context, _kit.FrameSetLayout, _shadowPass, _meshStore, _switches);

        _sky = new EnvironmentSky(_context, _ring);
        _frames.WriteSkySh(_sky.Sh);

        _probes = new ReflectionProbes(_context, _sky.Map.Sampler, FrameRing.Count);
        var captureScene = new CaptureScene(_meshStore.MeshSlots, _meshStore.DetailSlots, _shadowPass);
        _reflectionCapture = new ReflectionCapturePass(_context, _kit.FrameSetLayout, _resources, captureScene,
            new DescriptorImageInfo(_sky.Map.Sampler, _sky.Map.Sky, ImageLayout.ShaderReadOnlyOptimal));
        var probeVolume = new ProbeVolume(_context, _sky.Map.Sampler, _sky.Map.Sky);
        LightProbes = new LightProbes(_context, _sky.Map, _kit,
            new LightProbes.Sources(_resources, _tracing, captureScene, _ring, probeVolume));
        Settings.Probes = LightProbes;
        ProbeTests = new ProbeTestHooks(_context, LightProbes, _kit, () => _frames[_uniformFrame].Set, WriteTargetDescriptors);

        _fog = new FogStage(_context, _kit, _switches);

        _copier = new HistoryCopier(_api);
        _occlusion = new OcclusionStage(_context, _kit, _copier);
        _lighting = new LightingStage(_context, _kit,
            new LightingStage.Sources(_resources, _sky.Map, _probes, probeVolume, _shadowPass));
        // Counting each pass's shaders needs a device feature; without it the frame is only timed.
        _gpuWork = _context.Abilities.PipelineStatistics ? new GpuWorkCounter(_context, FrameRing.Count, GpuTimer.FramePasses) : null;
        _gpuTimer = new GpuTimer(_context, FrameRing.Count, counter: _gpuWork);
        _resolve = new ResolveStage(_context, _kit, _copier);
        _post = new PostStage(_context, _kit, _gpuTimer, _gBuffer, DisplaySource);
        _held = new HeldPicture(_api);
        _finish = new FinishStage(_context, _kit, _ring, _gpuTimer, (_swapchain.Format, _resources));
        _blend = new BlendStage(_context, _kit, (_ring, _gpuTimer, _meshStore), (_resources, _sky.Map, _probes), _switches);
        _opaque = new OpaqueStage(_context, _kit, _ring, _meshStore, (_resources, _switches));
        _fsrPass = new FsrPass(_context, _kit.NearestSampler, _kit.FrameSetLayout, _post.Meter.SetLayout);
        WriteTargetDescriptors();

        _areaInputs = new AreaInputs(_meshStore.PartBounds, () => EnvironmentHash);
        var spaceBake = new SpaceBake(_readableBakes, data => CreateTexture(data, TextureEdges.Clamp), DestroyTexture);
        _frameBakes = new FrameBakes(_readableBakes, _chunkPool, _retired, _areaInputs, spaceBake);
        BakeJobs = new BakeJobs(_context, new PreparedBakers(_context, _sky.Map, () => new Baker(_context, this)),
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

    /// <summary>The bindless textures and materials, which a shader that traces rays reads hits through.</summary>
    internal ResourceTable Resources => _resources;

    /// <summary>How rays treat a material, for <see cref="TraceScene.Collect"/>.</summary>
    internal TraceFlags TraceFlagsOf(MaterialHandle material) =>
        TraceEncoding.TraceFlagsFor(_resources.Materials[material].Cutouts, _resources.Materials[material].Flags);

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
        if (_resources.Defaults.Holds(texture) || !_resources.Textures.IsLive(texture.Index))
        {
            throw new ArgumentException("The texture does not exist.", nameof(texture));
        }

        QueueTextureDestroy(texture.Index);
    }

    /// <summary>Lets go of a texture array, with the same rules as <see cref="DestroyTexture"/>.</summary>
    public void DestroyTextureArray(TextureArrayHandle array)
    {
        if (_resources.Defaults.Holds(array) || !_resources.TextureArrays.IsLive(array.Index))
        {
            throw new ArgumentException("The texture array does not exist.", nameof(array));
        }

        _uploads.Cancel(_resources.TextureArrays.MarkDying(array.Index));
        _retired.AfterLastSubmitted.FreedTextureArrays.Add(array.Index);
    }

    /// <summary>Lets go of a material slot. Draws that still use it are a caller mistake.</summary>
    public void DestroyMaterial(MaterialHandle material)
    {
        if (!_resources.Materials.IsLive(material))
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
            if (!_resources.Materials.IsLive(part.Material))
            {
                continue;
            }

            foreach (int texture in _resources.Materials.TexturesOf(part.Material))
            {
                var handle = new TextureHandle(texture);
                if (texture >= 0 && !_resources.Defaults.Holds(handle) && _resources.Textures.IsLive(handle.Index))
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

        _uploads.Cancel(_resources.Textures.MarkDying(slot));
        _retired.AfterLastSubmitted.FreedTextures.Add(slot);
    }

    // Whether a material of one of the job's draws names the texture in this slot.
    private bool BakeReadsTexture(BakeJob job, int slot)
    {
        foreach (ref readonly var draw in job.Area.Draws.AsSpan())
        {
            if (!_resources.Materials.IsLive(draw.Material))
            {
                continue;
            }

            foreach (int texture in _resources.Materials.TexturesOf(draw.Material))
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
        _resources.Materials.MarkDying(slot);
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
        _sky.Set(environment);
    }

    /// <summary>
    /// A hash of what a baked space's light comes from besides its draws and lights: the environment's bands and panels
    /// from the last <see cref="SetEnvironment"/>, and the clear sky's turbidity and the environment intensity of the
    /// last frame drawn. It follows what they hold, so setting the same environment again gives the same hash. See
    /// "Is the bake fresh?" in docs/design.md.
    /// </summary>
    public ulong EnvironmentHash => _sky.Hash;

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
        _sky.Follow(view);
        _sky.Flush();
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

    /// <summary>
    /// Uploads a photographed sky, cooked from an equirectangular .hdr, for <see cref="AnalyticSky.Image"/>. It waits
    /// for the upload and stays until the renderer is disposed. See "A photographed sky" in docs/rendering.md.
    /// </summary>
    public SkyImageHandle CreateSkyImage(SkyImageData data) => _sky.Create(data);

    /// <summary>How many times the environment has been drawn again for a new sky, for tests.</summary>
    internal int SkyRebakes => _sky.Rebakes;

    /// <summary>Projects the sky cube to L1 now and reads it back, coefficient by coefficient, for tests.</summary>
    internal Vector4[] ProjectSky() => _sky.Sh.Project();

    /// <summary>What the last frame's uniforms gave, for tests.</summary>
    internal LastFrame Last { get; } = new();

    /// <summary>
    /// Hands a texture's bytes to the next frame's command buffer rather than waiting for
    /// the GPU, so it can be sampled in that same frame. <paramref name="edges"/> says what is
    /// read past the texture's edge, for every material that uses it: an atlas such as a
    /// <see cref="MaterialWeathering.Mask"/> wants <see cref="TextureEdges.Clamp"/>.
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
    /// Replaces the image that <see cref="FrameExtras.Overlay"/> quads read from. Only its
    /// alpha is used. Waits for the GPU, so it isn't for every frame.
    /// </summary>
    public void SetOverlayAtlas(TextureData data) => _finish.SetOverlayAtlas(data);

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
            _blend.WriteProbes(_sky.Map.Sampler, _probes.Cubes.View);
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
            ReflectionLightingHash(view, _sky.Version), LightProbes.InUse, LightProbes.SettledOnce, _frameBakes.DrawnKey,
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
        hash.Add(view.Environment.Intensity);
        hash.Add(view.Environment.Sky.Turbidity);
        hash.Add(view.Environment.Sky.Image);
        hash.Add(view.Environment.Sky.Rotation);
        hash.Add(view.Environment.Sky.Exposure);
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
        if (_switches.Off.HasFlag(RenderEffects.LightProbes) || !LightProbes.Volume.Supported)
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
        LightProbes.Plan(view, draws, _switches.DrawnLocalLights(view),
            new ProbePlanInputs(ProbeLightingKey(view, _sky.Version, _switches.Off), _switches.ShadowSun(view), _frameNumber));

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
        if (_switches.Off.HasFlag(RenderEffects.Reflections) || !_reflectionCapture.Supported
            || !_probes.TakeNext(out int slot, out var probe))
        {
            return 0;
        }

        _reflectionCaptureSlot = slot;
        _reflectionCaptureProbe = probe;
        return _reflectionCapture.Plan(draws, probe, view, _switches.ShadowSun(view), _frameNumber);
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

        return _opaque.Pass.AddShader(spirv, name);
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
        return _resources.Materials.Add(material);
    }

    /// <summary>
    /// Changes an existing material, for things that change colour during play. Unlike
    /// <see cref="CreateMaterial"/>, this doesn't use up one of the limited material slots.
    /// </summary>
    public void UpdateMaterial(MaterialHandle handle, in Material material)
    {
        ValidateMaterial(material);
        // A destroyed material's structures are already gone, so only a live one is checked.
        bool traced = _tracing is not null && _resources.Materials.IsLive(handle);
        var opaqueBefore = traced ? TraceEncoding.GeometryFlagsFor(_resources.Materials[handle].Cutouts, _resources.Materials[handle].Flags) : default;
        _resources.Materials.Update(handle, material);
        if (traced
            && TraceEncoding.GeometryFlagsFor(_resources.Materials[handle].Cutouts, _resources.Materials[handle].Flags) != opaqueBefore)
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

        if (material.Custom.Shader is { } shader)
        {
            ValidateShader(material, shader);
        }

        ValidateShaderTextures(material);
    }

    // Throws for a surface shader that doesn't exist, or on a transparent material, which can't use one yet.
    private void ValidateShader(in Material material, SurfaceShaderHandle shader)
    {
        if (shader.Index <= 0 || shader.Index >= _opaque.Pass.ShaderCount)
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
            if (material.Custom.Textures[i] is { } texture && !_resources.Textures.IsLive(texture.Index))
            {
                throw new ArgumentException($"The material's shader texture {i} does not exist.", nameof(material));
            }

            if (material.Custom.Arrays[i] is { } array && !_resources.TextureArrays.IsLive(array.Index))
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
                material.Weathering = new MaterialWeathering { Mask = textures[masks.Texture], Wear = masks.Wear, Dirt = masks.Dirt };
            }

            if (model.OcclusionOf(i) is >= 0 and int occlusion)
            {
                material.Textures.Occlusion = textures[occlusion];
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
            Textures = new MaterialTextures
            {
                BaseColor = Texture(source.BaseColorTexture, textures),
                Normal = Texture(source.NormalTexture, textures),
                Surface = Texture(source.SurfaceTexture, textures),
                Emissive = Texture(source.EmissiveTexture, textures),
            },
            Emissive = source.Emissive,
            Clearcoat = source.Clearcoat,
            Height = new MaterialHeight { Texture = Texture(source.HeightTexture, textures), Depth = source.HeightDepth },
            Detail = new MaterialDetail
            {
                Color = Texture(source.DetailColorTexture, textures),
                Normal = Texture(source.DetailNormalTexture, textures),
                Scale = source.DetailScale,
            },
            Flags = source.Flags,
            Glass = new MaterialGlass { Ior = source.Ior },
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

        var viewOff = _switches.TakeDebugView(view, _tracing is not null, _context.Abilities.NoTraceReason);
        KeepTargets(view, viewOff);
        TakeEffects(view, viewOff);
        bool antiAliased = !_switches.Off.HasFlag(RenderEffects.AntiAliasing);
        KeepDisplaySource(antiAliased);

        CheckDraws(view, draws);
        if (!_drawingWithoutBakedLights && DrewWithoutBakedLights(view, draws))
        {
            return;
        }

        ManageProbeStorage();

        ref FrameSlot frame = ref _frames[_ring.Index];
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
    /// Makes the swapchain, the targets and the shadow maps again when the window, the render scale, the upscaler or a
    /// resolution changed since the last frame.
    /// </summary>
    private void KeepTargets(in FrameView view, RenderEffects viewOff)
    {
        // FSR draws the picture at the window's size, so starting or stopping it changes the targets.
        bool upscaling = Settings.UpscalerUsed == Upscaler.Fsr && Settings.RenderScale <= 1f && !viewOff.HasFlag(RenderEffects.AntiAliasing);
        bool upscalingChanged = upscaling != _upscaling;
        _upscaling = upscaling;
        if (Settings.VsyncChanged || (uint)view.Width != _swapchain.Extent.Width || (uint)view.Height != _swapchain.Extent.Height)
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

        if (Settings.ShadowResolution != _shadowPass.Resolution)
        {
            // Frames in flight still read the maps being replaced. Changing this is rare.
            _context.WaitIdle();
            _shadowPass.Resize(Settings.ShadowResolution);
            WriteTargetDescriptors();
        }
    }

    /// <summary>
    /// Checks the decals, the static draws and the baked chunks before anything is recorded, so a frame that can't be
    /// drawn throws with the frame untouched.
    /// </summary>
    private void CheckDraws(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        _opaque.PrepareDecals(view);
        BakeIds.CheckStaticDraws(draws);
        BakeFiles.CheckChunks(view.Baked.Chunks);
    }

    // Whether the scene's size, or the resolution of the reflections or the occlusion, is no longer the targets'.
    private bool TargetsChanged()
    {
        var scene = SceneExtent(_swapchain.Extent);
        return Settings.ReflectionResolution != _gBuffer.Reflections || Settings.OcclusionResolution != _gBuffer.Occlusion.Resolution
            || scene.Width != _gBuffer.Extent.Width || scene.Height != _gBuffer.Extent.Height;
    }

    /// <summary>
    /// Takes the effects the frame leaves out, and what follows from them: the wind's reach, the fade rules, the
    /// volumetric fog's targets and depth of field.
    /// </summary>
    private void TakeEffects(in FrameView view, RenderEffects viewOff)
    {
        _switches.Take(view, viewOff, _resources.Materials);
        if (_fog.KeepTargets(view, _gBuffer.Extent, (Settings.VolumetricFogCellSize, Settings.VolumetricFogSlices)))
        {
            WriteTargetDescriptors();
        }

        _post.TakeLens(view, _switches.Off, (int)_gBuffer.Extent.Width);
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
        _post.SetSource(DisplaySource, _gBuffer);
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
        _sky.Follow(view);
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
    private bool AcquireImage(in FrameSlot frame, in FrameView view, out uint imageIndex)
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

        if (Settings.MeasureGpu)
        {
            WaitMilliseconds = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        }

        return true;
    }

    /// <summary>
    /// Reads the GPU timer's last times for the frame slot, when the GPU is measured, and resets its fence. Returns
    /// whether this frame is timed.
    /// </summary>
    private bool ResetFrame(in FrameSlot frame)
    {
        bool timed = Settings.MeasureGpu &&_gpuTimer.IsSupported;
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
        int jitterPhases = _upscaling ? Upscale.JitterPhaseCount((float)_gBuffer.Extent.Width / _gBuffer.Picture.Display.Width) : JitterSteps;
        var jitter = Upscale.Jitter(_frameNumber, jitterPhases);
        var shift = antiAliased ? jitter * 2f / scenePixels : Vector2.Zero;
        // The occlusion keeps a history of its own, which follows the camera and the draws while the picture has none.
        bool lastFrameKnown = _gBuffer.Picture.HistoryReady || _gBuffer.Occlusion.HistoryReady;
        var previous = lastFrameKnown ? _previousViewProjection : viewProjection;
        if (_upscaling)
        {
            float milliseconds = (view.Time - _previousTime) * 1000f;
            _fsrFrame = new FsrFrame(
                jitter,
                _gBuffer.Picture.HistoryReady && milliseconds > 0f ? milliseconds : 1000f / 60f,
                Reset: !_gBuffer.Picture.HistoryReady,
                CameraNear(view.Projection),
                VerticalFov(view.Projection));
        }

        return new FrameCamera(viewProjection, previous, shift, lastFrameKnown);
    }

    /// <summary>
    /// Plans the probes, writes the frame's uniforms, bones and chunks, and picks what each view draws. Returns how
    /// many draws the camera has, at the head of <see cref="_cameraDraws"/>.
    /// </summary>
    private int WriteFrameData(in FrameSlot frame, in FrameView view, ReadOnlySpan<DrawItem> draws, in FrameCamera camera)
    {
        // Before the uniforms, which carry the probes' sun shadow maps and whether surfaces read the probes.
        int probeInstances = PlanProbes(view, draws) + PlanReflectionCapture(view, draws);

        if (_casterBounds.Length < draws.Length)
        {
            _casterBounds = new Vector4[draws.Length * 2];
        }

        int casters = DrawSelection.Bounds(draws, _meshStore.DetailSlots, _casterBounds, _switches.WindReach);
        bool castsShadow = WriteUniforms(frame, view, camera.Previous, camera.Shift, _casterBounds.AsSpan(0, casters));
        _uniformFrame = _ring.Index;
        FrameSlots.WriteBones(frame, view.Bones);
        _motion.Begin(draws, view.Bones, camera.LastFrameKnown,
            new Span<Matrix4x4>((Matrix4x4*)frame.Bones.Mapped + view.Bones.Length, 2 * MaxBones - view.Bones.Length));
        _previousViewProjection = camera.ViewProjection;
        _previousTime = view.Time;
        _frameNumber++;
        _held.Take(view.Picture.Transition);
        var (cameraCount, shadowCount) = SelectDraws(view, camera.ViewProjection, draws, castsShadow);
        int tileDrawCount = _localShadows.Select(draws, Settings.MeshDetailScale);
        _frames.ResetInstances(frame, cameraCount + shadowCount + tileDrawCount + probeInstances);
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
    private CommandBuffer BeginCommands(in FrameSlot frame)
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
    private CommandBuffer RecordCommands(in FrameSlot frame, in FrameView view, uint imageIndex, in FrameDraws draws, bool timed)
    {
        var cmd = BeginCommands(frame);
        // One top-level structure a frame, shared by the trace debug view and the traced probe updates, made only on
        // a frame that traces something.
        TopLevelStructure? traced = null;
        if (_switches.TraceView || LightProbes.TracesBatch)
        {
            // After the uploads, so a mesh made this frame is traced this frame. From here until the submit below,
            // nothing may destroy or change a material: see RayTracing.TopLevel.
            traced = _tracing!.TopLevel(cmd, _traceScene!.Collect(draws.Scene));
        }

        int overlayQuads = _finish.UploadOverlay(view.Extras.Overlay);
        _blend.Upload(view);

        _opaque.UploadDecals();
        if (timed)
        {
            _gpuTimer.Begin(cmd, _ring.Index);
        }

        // A changed sky, redrawn before the frame's passes read it. It waits on the GPU for earlier reads itself, so the
        // bake slice above traces last frame's sky, a frame behind, which the timer leaves out of the "Sky" pass.
        _sky.RecordFrame(cmd);
        _gpuTimer.Mark(cmd);

        // Worked out here, just before the frame is recorded, so a frame that is never drawn doesn't move the clock.
        _post.StepExposure(view, _switches.Off);
        RecordFrame(cmd, frame.Set, imageIndex, new FrameDraws(draws.Camera, draws.Scene, traced), overlayQuads);
        _gpuTimer.End(cmd);
        _api.EndCommandBuffer(cmd).Check();
        return cmd;
    }

    /// <summary>
    /// Submits the frame's commands, once the swapchain image is there, signalling <paramref name="renderFinished"/>
    /// and the frame's fence.
    /// </summary>
    private void Submit(in FrameSlot frame, CommandBuffer cmd, Semaphore renderFinished)
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

    /// <summary>Leaves out what each pass can't see, and swaps in simpler models where nobody will notice.</summary>
    private (int Camera, int Shadows) SelectDraws(
        in FrameView view, in Matrix4x4 viewProjection, ReadOnlySpan<DrawItem> draws, bool castsShadow)
    {
        // A draw near swapping to a finer version is drawn twice, cross-fading, so every view has room for two of each.
        if (_cameraDraws.Length < draws.Length * 2)
        {
            _cameraDraws = new DrawItem[draws.Length * 2];
        }

        var (pixelsAtOneUnit, pixelsPerUnit) = Projection.PixelScale(view.Projection, (int)_gBuffer.Extent.Height);
        var camera = new DetailView(viewProjection, view.CameraPosition, pixelsAtOneUnit, pixelsPerUnit, _switches.DetailError(CameraDetailError, Settings.MeshDetailScale), cull: _switches.Culls);
        _cameraView = camera;
        int cameraCount = SelectCameraDraws(camera, draws);
        int shadowCount = _sunShadows.Select(view, draws, castsShadow, Settings.MeshDetailScale);
        if (Settings.MeasureGpu)
        {
            DrawnTriangles = (DrawSelection.Triangles(_cameraDraws.AsSpan(0, cameraCount)), _sunShadows.Triangles());
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
            return DrawSelection.Select(camera, draws, _meshStore.DetailSlots, _cameraDraws, _switches.WindReach, fades: _switches.FadeRules);
        }

        if (_cameraSources.Length < _cameraDraws.Length)
        {
            _cameraSources = new int[_cameraDraws.Length];
            _cameraChunks = new int[_cameraDraws.Length];
        }

        int cameraCount = DrawSelection.Select(camera, draws, _meshStore.DetailSlots, _cameraDraws, _switches.WindReach, _cameraSources, _switches.FadeRules);
        for (int i = 0; i < cameraCount; i++)
        {
            _cameraChunks[i] = _frameBakes.Relight.DrawChunks[_cameraSources[i]];
        }

        return cameraCount;
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

        _finish.Capture = capture;
        _finish.CaptureRecorded = false;
        try
        {
            Render(view, draws);
        }
        finally
        {
            // A frame that throws, such as for a draw it can't make, must not leave the next frame copying into the
            // buffer the using frees.
            _finish.Capture = null;
        }

        _context.WaitIdle();

        if (!_finish.CaptureRecorded)
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

        _frames.Dispose();
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
        _finish.Dispose();
        _fsrPass.Dispose();
        _post.Dispose();
        _resolve.Dispose();
        _blend.Dispose();
        _lighting.Dispose();
        _occlusion.Dispose();
        _opaque.Dispose();
        _shadowPass.Dispose();
        _localShadows.Dispose();
        DisposeProbesAndSky();
    }

    private void DisposeProbesAndSky()
    {
        _probes.Dispose();
        LightProbes.Dispose();
        _fog.Dispose();
        _reflectionCapture.Dispose();
        _sky.Dispose();
    }

    private void RecordFrame(CommandBuffer cmd, DescriptorSet frameSet, uint imageIndex, in FrameDraws draws, int overlayQuads)
    {
        var targets = _gBuffer;

        // Ldr still holds the last frame, so it's kept before this frame draws over it.
        _held.Record(cmd, targets);
        RecordScene(cmd, frameSet, draws);
        var display = (_readsAntiAliased, _switches.Off);
        _resolve.Record(cmd, frameSet, targets, _upscaling ? (_fsrPass, _fsrFrame, _post.Meter.Set) : null, display);
        _gpuTimer.Mark(cmd);
        _post.Record(cmd, frameSet, targets, display);
        _finish.Record(cmd, frameSet, (_swapchain, imageIndex), (targets, _held.Transition), overlayQuads);
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
        if (!targets.Picture.HistoryReady)
        {
            _api.Transition(cmd, targets.Picture.History.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
        }

        _sunShadows.Record(cmd, frameSet, instances);
        _localShadows.RecordTiles(cmd, frameSet, instances);
        _gpuTimer.Mark(cmd);
        RecordLightSetup(cmd, frameSet, draws, instances);
        _opaque.Record(cmd, targets, frameSet, draws.Camera, new OpaqueInputs(instances, _frameBakes, _sky.Sh, _motion, _cameraChunks, DepthFirstView));
        _gpuTimer.Mark(cmd);

        _occlusion.Record(cmd, targets, frameSet, _switches.Off, _gpuTimer);

        if (_switches.TraceView && draws.Traced is { } scene)
        {
            RecordTraceView(cmd, targets, frameSet, scene);
            return;
        }

        _lighting.RecordReflections(cmd, targets, frameSet, _switches.Off, _gpuTimer);
        _lighting.RecordLighting(cmd, targets, frameSet, _switches.Off, _gpuTimer);
        _blend.Record(cmd, targets, frameSet, draws.Camera, (instances, LightProbes, _lighting.LightingSet));
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

        if (_localShadows.ClustersNeeded)
        {
            _localShadows.RecordClusters(cmd, frameSet);
        }

        _gpuTimer.Mark(cmd);

        // The fog's air needs the shadow maps and the light lists, and the lighting pass needs the fog.
        _fog.Record(cmd, frameSet);

        _gpuTimer.Mark(cmd);
    }

    /// <summary>
    /// The trace debug view writes Hdr in place of lighting, and nothing blends over it. Depth is left read-only as the
    /// transparent pass would leave it, for the passes after. Marks the timer as the passes it stands in for would.
    /// </summary>
    private void RecordTraceView(CommandBuffer cmd, GBuffer targets, DescriptorSet frameSet, TopLevelStructure scene)
    {
        _gpuTimer.Mark(cmd);
        _traceDebugPass!.Record(cmd, _ring.Index, scene, (frameSet, _resources.Set), (targets.Hdr, targets.Extent));
        _gpuTimer.Mark(cmd);
        _gpuTimer.Mark(cmd);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages | SampledStage,
            AccessFlags2.DepthStencilAttachmentReadBit | SampledAccess);
        _gpuTimer.Mark(cmd);
    }

    /// <summary>
    /// Writes the frame's uniforms, with the camera's view shifted by <paramref name="shift"/>, and last frame's.
    /// Returns whether a light casts a shadow this frame.
    /// </summary>
    private bool WriteUniforms(
        in FrameSlot frame, in FrameView view, in Matrix4x4 previousViewProjection, Vector2 shift, ReadOnlySpan<Vector4> casters)
    {
        bool historyValid = _gBuffer.Picture.HistoryReady;
        var uniforms = (FrameUniforms*)frame.Uniforms.Mapped;
        WriteCamera(uniforms, view, previousViewProjection, shift);
        WriteEffects(uniforms, view, shift, historyValid);
        uniforms->CountBlock.ProbeCount = _probes.Prepare(
            view.View, view.Projection, new Span<GpuReflectionProbe>(frame.ReflectionProbes.Mapped, ReflectionProbeOrder.MaxProbes));
        WriteLightProbes(uniforms, view);
        WriteBakedProbes(uniforms);
        WriteSky(uniforms, view);
        _fog.WriteWater(uniforms, view);

        uniforms->CountBlock.Time = view.Time;
        uniforms->CountBlock.PreviousTime = historyValid ? _previousTime : view.Time;
        bool castsShadow = _sunShadows.Write(uniforms, view, casters, Settings.MaxShadowCascades,_frameBakes.MaskSlots.Sun);
        uniforms->CountBlock.Off = unchecked((int)(_switches.Off & ShadedEffects & ~RenderEffects.DepthFirst))
            | (_switches.Off.HasFlag(RenderEffects.Fades) ? FadesOffBit : 0)
            | (_switches.TraceView ? DebugViewBit : 0);
        uniforms->CountBlock.MipBias = _switches.Off.HasFlag(RenderEffects.AntiAliasing) ? 0f
            : _upscaling ? FsrMipBias(_gBuffer.Extent.Width, _gBuffer.Picture.Display.Width)
            : TemporalMipBias;
        _localShadows.Write(frame, uniforms, view, _gBuffer.Extent, (_frameBakes.MaskSlots.Channels, _probes.Order));

        // Probe updates and reflection probe captures light their faces with every point and spot light of the frame,
        // and see the sun's shadow through maps of their own, each zero when there is none this frame.
        uniforms->CountBlock.ProbeLightCount = _localShadows.ProbeLightCount;
        uniforms->ProbeSunMatrix = LightProbes.SunMatrix;
        uniforms->ReflectionSunMatrix = _reflectionCaptureSlot >= 0 ? _reflectionCapture.SunMatrix : default;
        return castsShadow;
    }

    /// <summary>The camera's matrices, this frame's and last frame's, and which frame this is.</summary>
    private void WriteCamera(FrameUniforms* uniforms, in FrameView view, in Matrix4x4 previousViewProjection, Vector2 shift)
    {
        var viewProjection = view.View * view.Projection * Matrix4x4.CreateTranslation(shift.X, shift.Y, 0f);
        Matrix4x4.Invert(viewProjection, out var inverse);
        uniforms->CameraBlock.ViewProjection = viewProjection;
        uniforms->CameraBlock.View = view.View;
        uniforms->CameraBlock.InverseViewProjection = inverse;
        uniforms->CameraBlock.PreviousViewProjection = previousViewProjection;
        uniforms->CountBlock.FrameNumber = _frameNumber;
        uniforms->CountBlock.HistoryValid = _gBuffer.Picture.HistoryReady ? 1 : 0;
        uniforms->CountBlock.ShowEnvironment = view.Environment.ShowAsBackground ? 1 : 0;
    }

    /// <summary>The picture's grade and the view's haze, occlusion, outlines, fog, puddles and wind.</summary>
    private void WriteEffects(FrameUniforms* uniforms, in FrameView view, Vector2 shift, bool historyValid)
    {
        var post = PostGrade.Without(view.Picture.Post, _switches.Off);
        uniforms->GradeBlock.Grade= new Vector4(MathF.Pow(2f, post.Exposure), 1f + post.Contrast, 1f + post.Saturation, post.Vignette);
        uniforms->GradeBlock.Finish = new Vector4(post.Sharpen, post.Grain, shift.X, shift.Y);
        (uniforms->GradeBlock.Lift, uniforms->GradeBlock.Gain) = PostGrade.Tint(post);
        uniforms->ToneBlock.WhiteBalance = PostGrade.WhiteBalance(post.Temperature, post.Tint);
        uniforms->ToneBlock.Curve = PostGrade.Curve(post);
        (uniforms->GradeBlock.HazeOrigin, uniforms->GradeBlock.HazeColor) = Haze.Pack(view.CameraPosition, view.Background.FadeOrigin, view.Background.FadeColor);
        uniforms->Occlusion = Occlusion.Pack(view.ScreenSpace.Occlusion, view.ScreenSpace.Bounce, _switches.Off, historyValid);
        (uniforms->OutlineBlock.Outline, uniforms->OutlineBlock.OutlineFade) = Outlines.Pack(view.ScreenSpace.Outlines, _gBuffer.Extent.Width / (float)Math.Max(view.Width, 1));
        uniforms->SkyBlock.Fog= Fog.Pack(view.Weather.Fog, _switches.Off, underwater: _switches.Underwater(view));
        _fog.Write(uniforms, view, historyValid, (Settings.VolumetricFogCellSize, Settings.VolumetricFogSlices));
        uniforms->WeatherBlock.Puddles = Puddles.Pack(view.Weather.Puddles, _switches.Off);
        uniforms->WeatherBlock.Wind = Wind.Pack(view.Weather.Wind, _switches.Off);
    }

    /// <summary>Where the live light probes are: the fixed grid's box and the cascades round the camera.</summary>
    private void WriteLightProbes(FrameUniforms* uniforms, in FrameView view)
    {
        // The fixed grid, read wherever it has a probe updated.
        bool inUse = LightProbes.InUse;
        if (inUse && view.LightProbes is { } lightProbes)
        {
            var (x, y, z) = ProbeGrid.Counts(lightProbes);
            uniforms->ProbeBlock.LightProbeMin = new Vector4(lightProbes.Min, lightProbes.Spacing);
            uniforms->ProbeBlock.LightProbeCounts = new Vector4(x, y, z, 1f);
        }
        else
        {
            uniforms->ProbeBlock.LightProbeMin = Vector4.Zero;
            uniforms->ProbeBlock.LightProbeCounts = Vector4.Zero;
        }

        // The cascades, read once any of their probes holds an update.
        var cascades = LightProbes.Batch.Cascades;
        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            var origin = cascades.Origin(c);
            uniforms->ProbeBlock.ProbeCascadeOrigins[c] = new Vector4(origin.X, origin.Y, origin.Z, cascades.Layout(c).Spacing);
        }

        bool readCascades = inUse && LightProbes.Volume.Atlases.CascadesWritten;
        var cascadeLayout = cascades.Layout(0);
        uniforms->ProbeBlock.ProbeCascadeCounts = new Vector4(
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
            uniforms->BakedBlock.BakedProbeMin = new Vector4(baked.Settings.Min, baked.Settings.Spacing);
            uniforms->BakedBlock.BakedProbeCounts = new Vector4(x, y, z, BakedProbeBand ? 1f : 2f);
            uniforms->BakedBlock.BakedSpaceMin = new Vector4(baked.Settings.Min, baked.Irradiance);
            uniforms->BakedBlock.BakedSpaceMax = new Vector4(baked.Settings.Max, baked.Depth);
        }
        else
        {
            uniforms->BakedBlock.BakedProbeMin = Vector4.Zero;
            uniforms->BakedBlock.BakedProbeCounts = Vector4.Zero;
            uniforms->BakedBlock.BakedSpaceMin = Vector4.Zero;
            uniforms->BakedBlock.BakedSpaceMax = Vector4.Zero;
        }

        // How far the space's bake has faded in, which weighs its lightmaps and probes against the live light.
        uniforms->BakedBlock.BakedFade = new Vector4(_frameBakes.SpaceFade, 0f, 0f, 0f);
    }

    /// <summary>The camera's place, the environment's light, the sky's sun and night, and the shadows' settings.</summary>
    private void WriteSky(FrameUniforms* uniforms, in FrameView view)
    {
        uniforms->CameraBlock.CameraPosition = new Vector4(view.CameraPosition, 1f);
        uniforms->BackgroundRadiance = new Vector4(view.Background.Radiance, view.Environment.Intensity);
        var flat = _sky.Map.Flat;
        uniforms->BakedBlock.FlatIrradiance = new Vector4(flat.Irradiance, 0f);
        uniforms->BakedBlock.FlatReflection = new Vector4(flat.Reflection, 0f);
        Last.Flat = flat;

        // Probes leave the sun's disc out: their rough levels would scatter it into specks, and
        // the sun's own light already gives surfaces its highlight. The sky is the one BakeSky worked out from the
        // original view, so a frame drawn without its baked sun still shows that sun's disc.
        var sky = _sky.Baked;
        uniforms->SkyBlock.SkySun = new Vector4(sky.Direction, sky.Turbidity);
        var disc = new Vector4(sky.DiscRadiance, sky.DiscCosine);
        uniforms->SkyBlock.SunDisc = disc;
        Last.FromCamera(disc, view.Lights);

        // The physical sky's air between the eye and far surfaces; see SkyModel.AirShows for where it shows.
        (uniforms->AirBlock.AirExtinction, uniforms->AirBlock.AirSun) = Atmosphere.Air(
            sky.Turbidity, sky.Direction, view.Environment.Intensity, SkyModel.AirShows(view, _switches.Off));
        (uniforms->AirBlock.Moon, uniforms->AirBlock.MoonRadiance, uniforms->AirBlock.Stars) = SkyModel.Night(view, sky.Direction, _switches.Off);

        // Moving points two texels of the first cascade off their surface stops them
        // shadowing themselves. The lighting pass works this out per cascade itself.
        float normalOffset = view.Shadows.Radius * 4f / _shadowPass.Resolution;
        // A fade from 0 to 0 is none: the shaders fade only where the end lies past the start.
        bool haze = !_switches.Off.HasFlag(RenderEffects.Haze);
        uniforms->Parameters = new Vector4(
            haze ? view.Background.FadeStart : 0f, haze ? view.Background.FadeEnd : 0f, view.Shadows.LightAngle, normalOffset);
    }



    /// <summary>What tonemapping and bloom read: the anti-aliased image, or the lit one when anti-aliasing is left out.</summary>
    private ImageView DisplaySource => _readsAntiAliased ? _gBuffer.Picture.Resolved.View : _gBuffer.Hdr.View;

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
        _swapchain = new Swapchain(_context, width, height, !Settings.Vsync);
        Settings.VsyncChanged = false;
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
        _gBuffer = new GBuffer(_context, scene, _upscaling ? _swapchain.Extent : scene, Settings.ReflectionResolution, Settings.OcclusionResolution);
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
                Settings.UpscalerUsed = Upscaler.Taa;
                _upscaling = false;
                _gBuffer.Dispose();
                _gBuffer = new GBuffer(_context, scene, reflections: Settings.ReflectionResolution, occlusion: Settings.OcclusionResolution);
            }
        }
        else
        {
            _fsrPass.Release();
        }

        _post.Resize(DisplaySource, _gBuffer);
        _fog.Resize(_gBuffer.Extent, (Settings.VolumetricFogCellSize, Settings.VolumetricFogSlices));

        WriteTargetDescriptors();
    }

    /// <summary>The scene's size for a window this size, at the render scale.</summary>
    private Extent2D SceneExtent(Extent2D window)
    {
        var (width, height) = RenderSettings.SceneSize((int)window.Width, (int)window.Height, Settings.RenderScale, _maxImageSize);
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


    /// <summary>Points the fullscreen passes at the current render targets.</summary>
    private void WriteTargetDescriptors()
    {
        var depth = new DescriptorImageInfo(_kit.NearestSampler, _gBuffer.Depth.View, ImageLayout.ShaderReadOnlyOptimal);
        var normal = new DescriptorImageInfo(_kit.NearestSampler, _gBuffer.Normal.View, ImageLayout.ShaderReadOnlyOptimal);
        _opaque.WriteTargets(_gBuffer, depth, _kit.NearestSampler);

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
            new(_sky.Map.Sampler, _sky.Map.Irradiance, ImageLayout.ShaderReadOnlyOptimal),
            _kit.ShadowSampler);

        _blend.WriteTargets(_gBuffer, _shadowPass.Target.View, _sky.Map);
    }

    // From anti-aliasing to the window: anti-aliasing, tonemapping, the meter and the finish, and the volumetric fog.
    private void WriteDisplaySets()
    {
        _resolve.WriteSet(_gBuffer);
        _post.WriteSets(DisplaySource, _gBuffer);
        _finish.WriteSet(_gBuffer);

        _fog.SetSources(
            new(_kit.ShadowSampler, _shadowPass.Target.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sky.Map.Sampler, _sky.Map.Background, ImageLayout.ShaderReadOnlyOptimal),
            (new(_kit.LinearSampler, LightProbes.Volume.IrradianceView, ImageLayout.General),
            new(_kit.LinearSampler, LightProbes.Volume.DepthView, ImageLayout.General)));
        _frames.WriteVolumetric(_fog.Result);
    }
}
