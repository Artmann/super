using System.Diagnostics;
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

    private const PipelineStageFlags2 SampledStage = PipelineStageFlags2.FragmentShaderBit;
    private const AccessFlags2 SampledAccess = AccessFlags2.ShaderSampledReadBit;
    private const PipelineStageFlags2 ColorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
    private const AccessFlags2 ColorWrite = AccessFlags2.ColorAttachmentWriteBit;
    private const PipelineStageFlags2 DepthStages =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
    private const AccessFlags2 DepthWrite = AccessFlags2.DepthStencilAttachmentWriteBit;

    private static readonly Vector2[] Jitter = CreateJitter();

    private struct Frame
    {
        public CommandPool CommandPool;
        public CommandBuffer CommandBuffer;
        public Fence InFlight;
        public Semaphore ImageAvailable;
        public GpuBuffer Uniforms;
        public DescriptorSet Set;
    }

    private readonly VulkanContext _context;
    private readonly Vk _api;
    private readonly Frame[] _frames = new Frame[FramesInFlight];
    // How far a simpler model may stray. Shadows are soft, so they can afford more.
    private const float CameraDetailError = 1f;
    private const float ShadowDetailError = 2f;

    private readonly List<Mesh> _meshes = [];
    private readonly List<PartDetail> _details = [default];
    private readonly ResourceTable _resources;
    private readonly EnvironmentMap _environment;
    private readonly ReflectionProbes _probes;

    private readonly DescriptorPool _descriptorPool;
    private readonly DescriptorSetLayout _frameSetLayout;
    private readonly DescriptorSetLayout _lightingSetLayout;
    private readonly DescriptorSetLayout _threeTextureSetLayout;
    private readonly DescriptorSetLayout _twoTextureSetLayout;
    private readonly DescriptorSetLayout _oneTextureSetLayout;
    private readonly DescriptorSet _aoSet;
    private readonly DescriptorSet _aoBlurSet;
    private readonly DescriptorSet _lightingSet;
    private readonly DescriptorSet _taaSet;
    private readonly DescriptorSet _tonemapSet;
    private readonly DescriptorSet _finishSet;
    private readonly Sampler _sampler;
    private readonly Sampler _linearSampler;
    private readonly Sampler _shadowSampler;

    private readonly ShadowPass _shadowPass;
    private readonly GBufferPass _gBufferPass;
    private readonly FullscreenPass _aoPass;
    private readonly FullscreenPass _aoBlurPass;
    private readonly FullscreenPass _lightingPass;
    private readonly TransparentPass _transparentPass;
    private readonly FullscreenPass _taaPass;
    private readonly BloomPass _bloomPass;
    private readonly FullscreenPass _tonemapPass;
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

    /// <summary>Vulkan validation messages so far. Always zero when validation is off.</summary>
    public int ValidationErrors => _context.ValidationErrors;

    /// <summary>Times the render passes. Off by default, because reading the times costs a little.</summary>
    public bool MeasureGpu { get; set; }

    /// <summary>Times from the newest finished frame, while <see cref="MeasureGpu"/> is on. Empty if the GPU can't time passes.</summary>
    public ReadOnlySpan<GpuPassTime> GpuTimes => _gpuTimer.Times;

    /// <summary>How long the last frame waited for the GPU to finish earlier work and for the display, while <see cref="MeasureGpu"/> is on.</summary>
    public double WaitMilliseconds { get; private set; }

    /// <summary>Triangles in the last frame's camera and shadow passes, while <see cref="MeasureGpu"/> is on.</summary>
    public (long Camera, long Shadows) DrawnTriangles { get; private set; }

    private DrawItem[] _cameraDraws = [];
    private DrawItem[] _shadowDraws = [];
    private Matrix4x4 _shadowMatrix;

    public Renderer(RendererOptions options)
    {
        _context = new VulkanContext(options);
        _api = _context.Api;

        _swapchain = new Swapchain(_context, (uint)options.Width, (uint)options.Height);
        _gBuffer = new GBuffer(_context, _swapchain.Extent);

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

        // Reversed-Z: a point is lit when it is at least as near the light as the map says.
        var shadowSamplerInfo = samplerInfo with
        {
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            CompareEnable = true,
            CompareOp = CompareOp.GreaterOrEqual,
        };
        _api.CreateSampler(_context.Device, &shadowSamplerInfo, null, out _shadowSampler).Check();

        _frameSetLayout = CreateSetLayout(
            DescriptorType.UniformBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
        _lightingSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 11, ShaderStageFlags.FragmentBit);
        _threeTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 3, ShaderStageFlags.FragmentBit);
        _twoTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 2, ShaderStageFlags.FragmentBit);
        _oneTextureSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.UniformBuffer, FramesInFlight),
            new(DescriptorType.CombinedImageSampler, 23),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = FramesInFlight + 7,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        _api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool).Check();

        for (int i = 0; i < FramesInFlight; i++)
        {
            _frames[i] = CreateFrame();
        }

        _resources = new ResourceTable(_context);
        _shadowPass = new ShadowPass(_context, _frameSetLayout, _resources);

        _environment = new EnvironmentMap(_context);
        SetEnvironment(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.5f),
            Horizon = new Vector3(0.4f),
            Floor = new Vector3(0.2f),
        });
        _probes = new ReflectionProbes(_context, _environment.Sampler);

        _aoSet = AllocateSet(_threeTextureSetLayout);
        _aoBlurSet = AllocateSet(_twoTextureSetLayout);
        _lightingSet = AllocateSet(_lightingSetLayout);
        _taaSet = AllocateSet(_threeTextureSetLayout);
        _tonemapSet = AllocateSet(_twoTextureSetLayout);
        _finishSet = AllocateSet(_oneTextureSetLayout);
        _overlaySet = AllocateSet(_oneTextureSetLayout);
        _bloomPass = new BloomPass(_context, _linearSampler, _gBuffer.Resolved.View, _gBuffer.Extent);
        WriteTargetDescriptors();

        _gBufferPass = new GBufferPass(_context, _frameSetLayout, _resources);
        _aoPass = new FullscreenPass(_context, "ao.frag", GBuffer.AoFormat, [_frameSetLayout, _threeTextureSetLayout]);
        _aoBlurPass = new FullscreenPass(_context, "ao_blur.frag", GBuffer.AoFormat, [_twoTextureSetLayout]);
        _lightingPass = new FullscreenPass(_context, "lighting.frag", GBuffer.HdrFormat, [_frameSetLayout, _lightingSetLayout]);
        _transparentPass = new TransparentPass(_context, _frameSetLayout, _resources, _environment);
        _taaPass = new FullscreenPass(_context, "taa.frag", GBuffer.HdrFormat, [_frameSetLayout, _threeTextureSetLayout]);
        _tonemapPass = new FullscreenPass(_context, "tonemap.frag", GBuffer.LdrFormat, [_frameSetLayout, _twoTextureSetLayout]);
        _finishPass = new FullscreenPass(_context, "finish.frag", _swapchain.Format, [_frameSetLayout, _oneTextureSetLayout]);
        _overlayPass = new OverlayPass(_context, _swapchain.Format, _oneTextureSetLayout, FramesInFlight);
        _gpuTimer = new GpuTimer(_context, FramesInFlight);
    }

    public MeshHandle CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var vertexBuffer = GpuBuffer.CreateDeviceLocal(_context, vertices, BufferUsageFlags.VertexBufferBit);
        var indexBuffer = GpuBuffer.CreateDeviceLocal(_context, indices, BufferUsageFlags.IndexBufferBit);
        _meshes.Add(new Mesh(vertexBuffer, indexBuffer, (uint)indices.Length));
        return new MeshHandle(_meshes.Count - 1);
    }

    /// <summary>
    /// Uploads a mesh as one draw that the renderer can skip when it's out of view, and
    /// simplify when it's small on screen.
    /// </summary>
    public DrawItem CreateDrawItem(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, MaterialHandle material)
    {
        ModelPart[] parts = [new ModelPart { IndexCount = (uint)indices.Length }];
        return CreateDetailedMesh(vertices, indices, parts, [material])[0];
    }

    /// <summary>Replaces what surfaces reflect. Waits for the GPU, so call this while loading.</summary>
    public void SetEnvironment(in EnvironmentDescription environment)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _environment.Bake(environment);
    }

    public TextureHandle CreateTexture(TextureData data) => _resources.AddTexture(data);

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
    /// This takes a moment and waits for the GPU, so call it while loading.
    /// </summary>
    public ReflectionProbeHandle CaptureReflectionProbe(
        in ReflectionProbe probe,
        in FrameView view,
        ReadOnlySpan<DrawItem> visible,
        ReadOnlySpan<DrawItem> shadowCasters)
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

        _api.DeviceWaitIdle(_context.Device).Check();

        // The passes are bound to the current targets, so small ones stand in for a while.
        var screenTargets = _gBuffer;
        _gBuffer = new GBuffer(_context, new Extent2D(ReflectionProbes.FaceSize, ReflectionProbes.FaceSize));
        WriteTargetDescriptors();
        _historyReady = false;

        ref Frame frame = ref _frames[_frameIndex];
        var fence = frame.InFlight;
        var faceView = view;
        faceView.CameraPosition = probe.Position;
        faceView.Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, probeNear);
        faceView.BackgroundFadeStart = 0f;
        faceView.BackgroundFadeEnd = 0f;

        for (int face = 0; face < faces.Length; face++)
        {
            faceView.View = Matrix4x4.CreateLookAt(probe.Position, probe.Position + faces[face].Forward, faces[face].Up);
            bool castsShadow = WriteUniforms(frame.Uniforms, faceView, Matrix4x4.Identity, Vector2.Zero, 0, false, capturingProbe: true);

            _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
            var cmd = frame.CommandBuffer;
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _api.BeginCommandBuffer(cmd, &beginInfo).Check();
            RecordScene(cmd, frame.Set, visible, castsShadow ? shadowCasters : []);
            _probes.RecordFace(cmd, _gBuffer.Hdr.Image, face);
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

        var handle = _probes.Add(probe);

        _gBuffer.Dispose();
        _gBuffer = screenTargets;
        WriteTargetDescriptors();
        _historyReady = false;
        return handle;
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
    }

    /// <summary>Uploads a cooked model and returns one draw per part.</summary>
    public DrawItem[] CreateModel(ModelData model)
    {
        var textures = new TextureHandle[model.Textures.Length];
        for (int i = 0; i < textures.Length; i++)
        {
            textures[i] = CreateTexture(model.Textures[i]);
        }

        var materials = new MaterialHandle[model.Materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            ref readonly var source = ref model.Materials[i];
            materials[i] = CreateMaterial(new Material
            {
                BaseColor = source.BaseColor,
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                AlphaCutoff = source.AlphaCutoff,
                BaseColorTexture = source.BaseColorTexture >= 0 ? textures[source.BaseColorTexture] : null,
                NormalTexture = source.NormalTexture >= 0 ? textures[source.NormalTexture] : null,
                SurfaceTexture = source.SurfaceTexture >= 0 ? textures[source.SurfaceTexture] : null,
                Emissive = source.Emissive,
                EmissiveTexture = source.EmissiveTexture >= 0 ? textures[source.EmissiveTexture] : null,
                Clearcoat = source.Clearcoat,
                Flags = source.Flags,
            });
        }

        return CreateDetailedMesh(model.Vertices, model.Indices, model.Parts, materials);
    }

    private DrawItem[] CreateDetailedMesh(
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<ModelPart> parts, ReadOnlySpan<MaterialHandle> materials)
    {
        var allIndices = new List<uint>(indices.ToArray());
        var draws = new DrawItem[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            ref readonly var part = ref parts[i];
            _details.Add(MeshDetail.Build(vertices, indices.Slice((int)part.FirstIndex, (int)part.IndexCount), allIndices));
            draws[i] = new DrawItem
            {
                FirstIndex = part.FirstIndex,
                IndexCount = part.IndexCount,
                Material = materials[part.Material],
                Transform = Matrix4x4.Identity,
                Detail = new DetailHandle(_details.Count - 1),
            };
        }

        var mesh = CreateMesh(vertices, CollectionsMarshal.AsSpan(allIndices));
        foreach (ref var draw in draws.AsSpan())
        {
            draw.Mesh = mesh;
        }

        return draws;
    }

    public void Render(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        if (view.Width <= 0 || view.Height <= 0)
        {
            return; // Minimized: there is nothing to present to.
        }

        if ((uint)view.Width != _swapchain.Extent.Width || (uint)view.Height != _swapchain.Extent.Height)
        {
            Resize((uint)view.Width, (uint)view.Height);
        }

        ref Frame frame = ref _frames[_frameIndex];
        var fence = frame.InFlight;
        long waitStart = Stopwatch.GetTimestamp();
        _api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue).Check();

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
        var viewProjection = view.View * view.Projection;
        var shift = Jitter[_frameNumber % JitterSteps] * 2f / new Vector2(view.Width, view.Height);
        var previous = _historyReady ? _previousViewProjection : viewProjection;
        bool castsShadow = WriteUniforms(frame.Uniforms, view, previous, shift, _frameNumber, _historyReady, capturingProbe: false);
        _previousViewProjection = viewProjection;
        _frameNumber++;
        var (cameraCount, shadowCount) = SelectDraws(view, viewProjection, draws, castsShadow);

        _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
        var cmd = frame.CommandBuffer;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _api.BeginCommandBuffer(cmd, &beginInfo).Check();
        int overlayQuads = _overlayAtlas is null ? 0 : _overlayPass.Upload(_frameIndex, view.Overlay);
        if (timed)
        {
            _gpuTimer.Begin(cmd, _frameIndex);
        }

        RecordFrame(cmd, frame.Set, imageIndex, _cameraDraws.AsSpan(0, cameraCount), _shadowDraws.AsSpan(0, shadowCount), overlayQuads);
        _gpuTimer.End();
        _api.EndCommandBuffer(cmd).Check();

        var renderFinished = _swapchain.RenderFinished[imageIndex];
        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = frame.ImageAvailable,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = renderFinished,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
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
            _shadowDraws = new DrawItem[draws.Length * 2];
        }

        float focalPixels = MathF.Abs(view.Projection.M22) * view.Height / 2f;
        var camera = new DetailView(viewProjection, view.CameraPosition, focalPixels, 0f, CameraDetailError);
        int cameraCount = DrawSelection.Select(camera, draws, _details, _cameraDraws);

        int shadowCount = 0;
        if (castsShadow)
        {
            float texelsPerUnit = ShadowPass.Resolution / (2f * view.Shadows.Radius);
            var shadow = new DetailView(_shadowMatrix, view.CameraPosition, 0f, texelsPerUnit, ShadowDetailError);
            shadowCount = DrawSelection.Select(shadow, draws, _details, _shadowDraws);
        }

        if (MeasureGpu)
        {
            DrawnTriangles = (CountTriangles(_cameraDraws.AsSpan(0, cameraCount)), CountTriangles(_shadowDraws.AsSpan(0, shadowCount)));
        }

        return (cameraCount, shadowCount);
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

        _gpuTimer.Dispose();
        _overlayPass.Dispose();
        _overlayAtlas?.Dispose();
        _finishPass.Dispose();
        _tonemapPass.Dispose();
        _bloomPass.Dispose();
        _taaPass.Dispose();
        _transparentPass.Dispose();
        _lightingPass.Dispose();
        _aoBlurPass.Dispose();
        _aoPass.Dispose();
        _gBufferPass.Dispose();
        _shadowPass.Dispose();
        _probes.Dispose();
        _environment.Dispose();
        _resources.Dispose();

        foreach (var mesh in _meshes)
        {
            mesh.Dispose();
        }

        foreach (ref var frame in _frames.AsSpan())
        {
            frame.Uniforms.Dispose();
            _api.DestroySemaphore(_context.Device, frame.ImageAvailable, null);
            _api.DestroyFence(_context.Device, frame.InFlight, null);
            _api.DestroyCommandPool(_context.Device, frame.CommandPool, null);
        }

        _api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _oneTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _twoTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _threeTextureSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _lightingSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _frameSetLayout, null);
        _api.DestroySampler(_context.Device, _shadowSampler, null);
        _api.DestroySampler(_context.Device, _linearSampler, null);
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
        ReadOnlySpan<DrawItem> shadowCasters,
        int overlayQuads)
    {
        var targets = _gBuffer;
        var extent = targets.Extent;
        var swapchainImage = _swapchain.Images[imageIndex];

        RecordScene(cmd, frameSet, draws, shadowCasters);

        ReadOnlySpan<DescriptorSet> taaSets = [frameSet, _taaSet];
        _api.BeginColorTarget(cmd, targets.Resolved.Image);
        _taaPass.Record(cmd, targets.Resolved.View, extent, taaSets);
        RecordHistoryCopy(cmd, targets);
        _gpuTimer.Mark(cmd);

        _bloomPass.Record(cmd);
        _gpuTimer.Mark(cmd);

        ReadOnlySpan<DescriptorSet> tonemapSets = [frameSet, _tonemapSet];
        _api.BeginColorTarget(cmd, targets.Ldr.Image);
        _tonemapPass.Record(cmd, targets.Ldr.View, extent, tonemapSets);
        _api.EndColorTarget(cmd, targets.Ldr.Image);
        _gpuTimer.Mark(cmd);

        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, ColorStage, AccessFlags2.None, ColorStage, ColorWrite);

        ReadOnlySpan<DescriptorSet> finishSets = [frameSet, _finishSet];
        _finishPass.Record(cmd, _swapchain.Views[imageIndex], extent, finishSets);
        _gpuTimer.Mark(cmd);

        if (overlayQuads > 0)
        {
            _overlayPass.Record(cmd, _swapchain.Views[imageIndex], extent, _overlaySet, _frameIndex, overlayQuads);
        }

        _gpuTimer.Mark(cmd);

        if (_capture is not null)
        {
            RecordCapture(cmd, swapchainImage, extent, _capture);
            return;
        }

        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr, ColorStage, ColorWrite,
            PipelineStageFlags2.None, AccessFlags2.None);
    }

    /// <summary>Keeps this frame for the next one, and leaves it ready to sample.</summary>
    private void RecordHistoryCopy(CommandBuffer cmd, GBuffer targets)
    {
        _api.Transition(cmd, targets.Resolved.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, ColorStage, ColorWrite,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, SampledStage, SampledAccess,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D(targets.Extent.Width, targets.Extent.Height, 1),
        };
        _api.CmdCopyImage(cmd, targets.Resolved.Image, ImageLayout.TransferSrcOptimal,
            targets.History.Image, ImageLayout.TransferDstOptimal, 1, &region);

        _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Resolved.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit, SampledStage, SampledAccess);
        _historyReady = true;
    }

    /// <summary>Draws and lights the scene into the HDR target, for frames and probe captures alike.</summary>
    private void RecordScene(
        CommandBuffer cmd,
        DescriptorSet frameSet,
        ReadOnlySpan<DrawItem> draws,
        ReadOnlySpan<DrawItem> shadowCasters)
    {
        var targets = _gBuffer;
        var extent = targets.Extent;

        // The lighting pass binds history even before there is any.
        if (!_historyReady)
        {
            _api.Transition(cmd, targets.History.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, SampledStage, SampledAccess, SampledStage, SampledAccess);
        }

        // Targets are shared between frames in flight, so each first use waits for the
        // previous frame's sampling to finish. Contents are discarded via Undefined.
        // Runs without draws when nothing casts a shadow: the lighting pass still binds the map.
        var shadowMap = _shadowPass.Target.Image;
        _api.Transition(cmd, shadowMap, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, SampledStage, SampledAccess, DepthStages,
            DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);
        _shadowPass.Record(cmd, frameSet, shadowCasters, _meshes);
        _api.Transition(cmd, shadowMap, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, SampledStage, SampledAccess);
        _gpuTimer.Mark(cmd);

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Emissive.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, SampledStage, SampledAccess, ColorStage, ColorWrite);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, DepthStages, AccessFlags2.DepthStencilAttachmentReadBit, DepthStages,
            DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit);

        _gBufferPass.Record(cmd, targets, frameSet, draws, _meshes);

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Emissive.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, ColorStage, ColorWrite, SampledStage, SampledAccess);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, DepthStages, DepthWrite, SampledStage, SampledAccess);
        _gpuTimer.Mark(cmd);

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

        _api.BeginColorTarget(cmd, targets.Hdr.Image);

        ReadOnlySpan<DescriptorSet> lightingSets = [frameSet, _lightingSet];
        _lightingPass.Record(cmd, targets.Hdr.View, extent, lightingSets);
        _gpuTimer.Mark(cmd);

        // Glass blends over the lit image and is hidden by whatever stands in front of it.
        _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ColorAttachmentOptimal, ColorStage, ColorWrite, ColorStage,
            ColorWrite | AccessFlags2.ColorAttachmentReadBit);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.DepthReadOnlyOptimal, SampledStage, SampledAccess, DepthStages,
            AccessFlags2.DepthStencilAttachmentReadBit);
        _transparentPass.Record(cmd, targets, frameSet, draws, _meshes);

        _api.EndColorTarget(cmd, targets.Hdr.Image);
        _gpuTimer.Mark(cmd);
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
        GpuBuffer buffer,
        in FrameView view,
        in Matrix4x4 previousViewProjection,
        Vector2 shift,
        int frameNumber,
        bool historyValid,
        bool capturingProbe)
    {
        var viewProjection = view.View * view.Projection * Matrix4x4.CreateTranslation(shift.X, shift.Y, 0f);
        Matrix4x4.Invert(viewProjection, out var inverse);

        var uniforms = (FrameUniforms*)buffer.Mapped;
        uniforms->ViewProjection = viewProjection;
        uniforms->InverseViewProjection = inverse;
        uniforms->PreviousViewProjection = previousViewProjection;
        uniforms->FrameNumber = frameNumber;
        uniforms->HistoryValid = historyValid ? 1 : 0;
        uniforms->ShowEnvironment = capturingProbe ? 1 : 0;
        uniforms->Grade = new Vector4(
            MathF.Pow(2f, view.Post.Exposure), 1f + view.Post.Contrast, 1f + view.Post.Saturation, view.Post.Vignette);
        uniforms->Finish = new Vector4(view.Post.Sharpen, view.Post.Grain, shift.X, shift.Y);
        uniforms->ProbeCount = 0;
        if (!capturingProbe)
        {
            _probes.CopyToUniforms(uniforms);
        }

        uniforms->CameraPosition = new Vector4(view.CameraPosition, 1f);
        uniforms->BackgroundRadiance = new Vector4(view.BackgroundRadiance, view.EnvironmentIntensity);

        // Moving points two shadow map texels off their surface stops them shadowing themselves.
        float normalOffset = view.Shadows.Radius * 4f / ShadowPass.Resolution;
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
        bool castsShadow = false;
        for (int i = 0; i < count; i++)
        {
            ref readonly var light = ref view.Lights[i];
            var direction = Vector3.Normalize(light.Direction);
            bool casts = !castsShadow && light.CastsShadow && view.Shadows.Radius > 0f;
            if (casts)
            {
                _shadowMatrix = ShadowPass.CreateMatrix(direction, view.Shadows.Center, view.Shadows.Radius);
                uniforms->ShadowMatrix = _shadowMatrix;
                castsShadow = true;
            }

            uniforms->Lights[i] = new GpuLight
            {
                Direction = new Vector4(direction, casts ? 1f : 0f),
                Radiance = new Vector4(light.Radiance, 0f),
            };
        }

        return castsShadow;
    }

    private void Resize(uint width, uint height)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _gBuffer.Dispose();
        _swapchain.Dispose();
        _swapchain = new Swapchain(_context, width, height);
        _gBuffer = new GBuffer(_context, _swapchain.Extent);
        _bloomPass.Resize(_gBuffer.Resolved.View, _gBuffer.Extent);
        WriteTargetDescriptors();
        _historyReady = false;
    }

    /// <summary>Halton points spread the shifts evenly over a pixel, in fractions of a pixel.</summary>
    private static Vector2[] CreateJitter()
    {
        static float Halton(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f;
            for (; index > 0; index /= radix)
            {
                fraction /= radix;
                result += fraction * (index % radix);
            }

            return result;
        }

        var shifts = new Vector2[JitterSteps];
        for (int i = 0; i < JitterSteps; i++)
        {
            shifts[i] = new Vector2(Halton(i + 1, 2) - 0.5f, Halton(i + 1, 3) - 0.5f);
        }

        return shifts;
    }

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
        frame.Set = AllocateSet(_frameSetLayout);

        var bufferInfo = new DescriptorBufferInfo(frame.Uniforms.Handle, 0, (ulong)sizeof(FrameUniforms));
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = frame.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &bufferInfo,
        };
        _api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);

        return frame;
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
        ]);

        // Glass has just used depth for testing, and it stays in that layout.
        WriteImages(_taaSet,
        [
            new(_sampler, _gBuffer.Hdr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _gBuffer.History.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Depth.View, ImageLayout.DepthReadOnlyOptimal),
        ]);
        WriteImages(_tonemapSet,
        [
            new(_sampler, _gBuffer.Resolved.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_linearSampler, _bloomPass.Result, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        WriteImages(_finishSet, [new(_linearSampler, _gBuffer.Ldr.View, ImageLayout.ShaderReadOnlyOptimal)]);
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
