using System.Numerics;
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
    private readonly List<Mesh> _meshes = [];
    private readonly ResourceTable _resources;

    private readonly DescriptorPool _descriptorPool;
    private readonly DescriptorSetLayout _frameSetLayout;
    private readonly DescriptorSetLayout _lightingSetLayout;
    private readonly DescriptorSetLayout _tonemapSetLayout;
    private readonly DescriptorSet _lightingSet;
    private readonly DescriptorSet _tonemapSet;
    private readonly Sampler _sampler;

    private readonly GBufferPass _gBufferPass;
    private readonly FullscreenPass _lightingPass;
    private readonly FullscreenPass _tonemapPass;

    private Swapchain _swapchain;
    private GBuffer _gBuffer;
    private int _frameIndex;

    /// <summary>Vulkan validation messages so far. Always zero when validation is off.</summary>
    public int ValidationErrors => _context.ValidationErrors;

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

        _frameSetLayout = CreateSetLayout(
            DescriptorType.UniformBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
        _lightingSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 3, ShaderStageFlags.FragmentBit);
        _tonemapSetLayout = CreateSetLayout(DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.UniformBuffer, FramesInFlight),
            new(DescriptorType.CombinedImageSampler, 4),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = FramesInFlight + 2,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        _api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool).Check();

        _lightingSet = AllocateSet(_lightingSetLayout);
        _tonemapSet = AllocateSet(_tonemapSetLayout);
        WriteTargetDescriptors();

        for (int i = 0; i < FramesInFlight; i++)
        {
            _frames[i] = CreateFrame();
        }

        _resources = new ResourceTable(_context);
        _gBufferPass = new GBufferPass(_context, _frameSetLayout, _resources);
        _lightingPass = new FullscreenPass(_context, "lighting.frag", GBuffer.HdrFormat, [_frameSetLayout, _lightingSetLayout]);
        _tonemapPass = new FullscreenPass(_context, "tonemap.frag", _swapchain.Format, [_tonemapSetLayout]);
    }

    public MeshHandle CreateMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var vertexBuffer = GpuBuffer.CreateDeviceLocal(_context, vertices, BufferUsageFlags.VertexBufferBit);
        var indexBuffer = GpuBuffer.CreateDeviceLocal(_context, indices, BufferUsageFlags.IndexBufferBit);
        _meshes.Add(new Mesh(vertexBuffer, indexBuffer, (uint)indices.Length));
        return new MeshHandle(_meshes.Count - 1);
    }

    public TextureHandle CreateTexture(TextureData data) => _resources.AddTexture(data);

    public MaterialHandle CreateMaterial(in Material material) => _resources.AddMaterial(material);

    /// <summary>
    /// Uploads a cooked model and returns one draw per opaque part. Transparent parts
    /// are left out until the renderer has a transparency pass.
    /// </summary>
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
                Flags = source.Flags,
            });
        }

        var mesh = CreateMesh(model.Vertices, model.Indices);
        var draws = new List<DrawItem>(model.Parts.Length);
        foreach (ref readonly var part in model.Parts.AsSpan())
        {
            if ((model.Materials[part.Material].Flags & MaterialFlags.Transparent) == 0)
            {
                draws.Add(new DrawItem
                {
                    Mesh = mesh,
                    FirstIndex = part.FirstIndex,
                    IndexCount = part.IndexCount,
                    Material = materials[part.Material],
                    Transform = Matrix4x4.Identity,
                });
            }
        }

        return draws.ToArray();
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

        _api.ResetFences(_context.Device, 1, &fence).Check();

        WriteUniforms(frame.Uniforms, view);

        _api.ResetCommandPool(_context.Device, frame.CommandPool, 0).Check();
        var cmd = frame.CommandBuffer;
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _api.BeginCommandBuffer(cmd, &beginInfo).Check();
        RecordFrame(cmd, frame.Set, imageIndex, draws);
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

    public void Dispose()
    {
        _api.DeviceWaitIdle(_context.Device).Check();

        _tonemapPass.Dispose();
        _lightingPass.Dispose();
        _gBufferPass.Dispose();
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
        _api.DestroyDescriptorSetLayout(_context.Device, _tonemapSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _lightingSetLayout, null);
        _api.DestroyDescriptorSetLayout(_context.Device, _frameSetLayout, null);
        _api.DestroySampler(_context.Device, _sampler, null);
        _gBuffer.Dispose();
        _swapchain.Dispose();
        _context.Dispose();
    }

    private void RecordFrame(CommandBuffer cmd, DescriptorSet frameSet, uint imageIndex, ReadOnlySpan<DrawItem> draws)
    {
        var targets = _gBuffer;
        var extent = targets.Extent;
        var swapchainImage = _swapchain.Images[imageIndex];

        // Targets are shared between frames in flight, so each first use waits for the
        // previous frame's sampling to finish. Contents are discarded via Undefined.
        const PipelineStageFlags2 sampledStage = PipelineStageFlags2.FragmentShaderBit;
        const AccessFlags2 sampledAccess = AccessFlags2.ShaderSampledReadBit;
        const PipelineStageFlags2 colorStage = PipelineStageFlags2.ColorAttachmentOutputBit;
        const AccessFlags2 colorWrite = AccessFlags2.ColorAttachmentWriteBit;
        const PipelineStageFlags2 depthStages =
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        const AccessFlags2 depthWrite = AccessFlags2.DepthStencilAttachmentWriteBit;

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, sampledStage, sampledAccess, colorStage, colorWrite);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, sampledStage, sampledAccess, colorStage, colorWrite);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal, sampledStage, sampledAccess, depthStages,
            depthWrite | AccessFlags2.DepthStencilAttachmentReadBit);

        _gBufferPass.Record(cmd, targets, frameSet, draws, _meshes);

        _api.Transition(cmd, targets.BaseColor.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, colorStage, colorWrite, sampledStage, sampledAccess);
        _api.Transition(cmd, targets.Normal.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, colorStage, colorWrite, sampledStage, sampledAccess);
        _api.Transition(cmd, targets.Depth.Image, ImageAspectFlags.DepthBit,
            ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, depthStages, depthWrite, sampledStage, sampledAccess);
        _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, sampledStage, sampledAccess, colorStage, colorWrite);

        ReadOnlySpan<DescriptorSet> lightingSets = [frameSet, _lightingSet];
        _lightingPass.Record(cmd, targets.Hdr.View, extent, lightingSets);

        _api.Transition(cmd, targets.Hdr.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal, colorStage, colorWrite, sampledStage, sampledAccess);
        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, colorStage, AccessFlags2.None, colorStage, colorWrite);

        ReadOnlySpan<DescriptorSet> tonemapSets = [_tonemapSet];
        _tonemapPass.Record(cmd, _swapchain.Views[imageIndex], extent, tonemapSets);

        _api.Transition(cmd, swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr, colorStage, colorWrite,
            PipelineStageFlags2.None, AccessFlags2.None);
    }

    private static void WriteUniforms(GpuBuffer buffer, in FrameView view)
    {
        var viewProjection = view.View * view.Projection;
        Matrix4x4.Invert(viewProjection, out var inverse);
        *(FrameUniforms*)buffer.Mapped = new FrameUniforms
        {
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = new Vector4(view.CameraPosition, 1f),
            LightDirection = new Vector4(Vector3.Normalize(view.Sun.Direction), 0f),
            LightRadiance = new Vector4(view.Sun.Radiance, 0f),
            AmbientRadiance = new Vector4(view.AmbientRadiance, 0f),
        };
    }

    private void Resize(uint width, uint height)
    {
        _api.DeviceWaitIdle(_context.Device).Check();
        _gBuffer.Dispose();
        _swapchain.Dispose();
        _swapchain = new Swapchain(_context, width, height);
        _gBuffer = new GBuffer(_context, _swapchain.Extent);
        WriteTargetDescriptors();
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
        var images = stackalloc DescriptorImageInfo[]
        {
            new(_sampler, _gBuffer.BaseColor.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Normal.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Depth.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _gBuffer.Hdr.View, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < 4; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = i < 3 ? _lightingSet : _tonemapSet,
                DstBinding = i < 3 ? (uint)i : 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        _api.UpdateDescriptorSets(_context.Device, 4, writes, 0, null);
    }
}
