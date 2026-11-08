using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The light probes' data on the GPU. Each probe has a tile in two atlases: the light reaching
/// it from every direction, and the mean and mean square of the distance it sees in every
/// direction, which lets a surface tell whether a probe is behind a wall from it. The renderer
/// draws each probe's faces into <see cref="Capture"/> and this class folds them into its tiles.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed unsafe class ProbeVolume : IDisposable
{
    /// <summary>How many texels across each captured face is. The probes keep only soft light, so this is plenty.</summary>
    public const uint FaceSize = 32;

    /// <summary>The most probes captured in one frame.</summary>
    public const int ProbesPerFrame = 64;

    /// <summary>Texels across a tile, without its border. Must match probes.glsl and probe_fold.comp.</summary>
    public const int IrradianceTexels = 8;
    public const int DepthTexels = 16;

    /// <summary>Tiles across each atlas, so 64 rows of 64 hold <see cref="ProbeGrid.MaxProbes"/>.</summary>
    public const int Columns = 64;

    private const Format IrradianceFormat = Format.R16G16B16A16Sfloat;
    private const Format DepthFormat = Format.R16G16Sfloat;

    /// <summary>Must match the Fold block in Shaders/probe_fold.comp.</summary>
    private struct FoldConstants
    {
        public int Probe;
        public float MaxDistance;
    }

    private readonly VulkanContext _context;
    private readonly Image _irradiance;
    private readonly GpuAllocation _irradianceMemory;
    private readonly Image _depth;
    private readonly GpuAllocation _depthMemory;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;

    public readonly CaptureCube Capture;
    public readonly ImageView IrradianceView;
    public readonly ImageView DepthView;

    /// <summary>Which probes to capture next.</summary>
    public readonly ProbeSchedule Schedule = new();

    /// <summary>
    /// Whether this GPU can fold captures into the atlases. The depth atlas has two channels,
    /// which compute shaders can only write with shaderStorageImageExtendedFormats. Without it
    /// the atlases are still bound, but nothing is captured and surfaces use the sky.
    /// </summary>
    public readonly bool Supported;

    public ProbeVolume(VulkanContext context, Sampler sampler)
    {
        _context = context;
        Supported = context.StorageImageExtendedFormats;
        var api = context.Api;

        Capture = new CaptureCube(context, FaceSize);
        uint irradianceSize = Columns * (IrradianceTexels + 2);
        uint depthSize = Columns * (DepthTexels + 2);
        (_irradiance, _irradianceMemory, IrradianceView) = CreateAtlas(irradianceSize, IrradianceFormat);
        (_depth, _depthMemory, DepthView) = CreateAtlas(depthSize, DepthFormat);

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, 1),
            new(DescriptorType.StorageImage, 2),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _set).Check();

        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FoldConstants));
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &pipelineLayoutInfo, null, out _pipelineLayout).Check();

        if (!Supported)
        {
            return;
        }

        _pipeline = PipelineFactory.CreateCompute(context, "probe_fold.comp", _pipelineLayout);

        var facesInfo = new DescriptorImageInfo(sampler, Capture.ArrayView, ImageLayout.ShaderReadOnlyOptimal);
        var irradianceInfo = new DescriptorImageInfo(default, IrradianceView, ImageLayout.General);
        var depthInfo = new DescriptorImageInfo(default, DepthView, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &facesInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &irradianceInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &depthInfo,
            },
        };
        api.UpdateDescriptorSets(context.Device, 3, writes, 0, null);
    }

    /// <summary>
    /// Folds the six faces in <see cref="Capture"/> into one probe's tiles, after the last face
    /// has been copied in. Surfaces lit from the probes in this same command buffer, and in later
    /// ones, wait for it.
    /// </summary>
    public void RecordFold(CommandBuffer cmd, int probe, float spacing)
    {
        var api = _context.Api;

        // The faces just drawn may have read this probe's old tiles.
        Barrier(cmd, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);

        var set = _set;
        // Further than this, a probe can't light anything, so distances stop there.
        var constants = new FoldConstants { Probe = probe, MaxDistance = spacing * 2f };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FoldConstants), &constants);
        api.CmdDispatch(cmd, 1, 1, 1);

        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit);
    }

    public void Dispose()
    {
        var api = _context.Api;
        if (Supported)
        {
            api.DestroyPipeline(_context.Device, _pipeline, null);
        }

        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        api.DestroyImageView(_context.Device, IrradianceView, null);
        api.DestroyImageView(_context.Device, DepthView, null);
        api.DestroyImage(_context.Device, _irradiance, null);
        api.DestroyImage(_context.Device, _depth, null);
        _context.Memory.Free(_irradianceMemory);
        _context.Memory.Free(_depthMemory);
        Capture.Dispose();
    }

    private void Barrier(
        CommandBuffer cmd, PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess, PipelineStageFlags2 destinationStage, AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>An atlas that stays in the general layout for good, so the fold writes it and lighting samples it. It starts black.</summary>
    private (Image Image, GpuAllocation Memory, ImageView View) CreateAtlas(uint size, Format format)
    {
        var api = _context.Api;
        var usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
        if (Supported)
        {
            usage |= ImageUsageFlags.StorageBit;
        }

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(size, size, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(_context.Device, &imageInfo, null, out var image).Check();
        api.GetImageMemoryRequirements(_context.Device, image, out var requirements);
        var memory = _context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(_context.Device, image, memory.Memory, memory.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        api.CreateImageView(_context.Device, &viewInfo, null, out var view).Check();

        _context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
            var black = new ClearColorValue(0f, 0f, 0f, 0f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            api.CmdClearColorImage(cmd, image, ImageLayout.General, &black, 1, &range);
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit);
        });
        return (image, memory, view);
    }
}
