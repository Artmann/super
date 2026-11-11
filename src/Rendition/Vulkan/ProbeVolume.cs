using Rendition.Passes;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>
/// The light probes' data on the GPU. Each probe has a tile in two atlases: the light reaching
/// it from every direction, and the mean and mean square of the distance it sees in every
/// direction, which lets a surface tell whether a probe is behind a wall from it. Each atlas is a
/// 2D array of five layers: the four cascades that follow the camera, finest first, then the fixed
/// grid in <see cref="FixedGridLayer"/>. A capture folds a probe's view into a slot of
/// <see cref="StagingTiles"/>, and <see cref="RecordBlend"/> blends a batch of slots into the atlases.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed unsafe class ProbeVolume : IDisposable
{
    /// <summary>How many texels across each captured face is. The probes keep only soft light, so this is plenty.</summary>
    public const uint FaceSize = 32;

    /// <summary>The most probes of the fixed grid captured in one frame.</summary>
    public const int ProbesPerFrame = 64;

    /// <summary>Texels across a tile, without its border. Must match probes.glsl and probe_fold.glsl.</summary>
    public const int IrradianceTexels = 8;
    public const int DepthTexels = 16;

    /// <summary>The atlas layer of the fixed grid. Layers 0 to 3 are the cascades.</summary>
    public const int FixedGridLayer = ProbeScheduler.FixedGrid;

    /// <summary>The atlases' layers: four cascades and the fixed grid.</summary>
    public const int Layers = FixedGridLayer + 1;

    /// <summary>
    /// One probe's two bordered tiles in the staging buffer: 10 × 10 RGBA floats and 18 × 18 RG
    /// floats. Must match StagedTile in Shaders/probe_fold.glsl.
    /// </summary>
    public const int StagedTileBytes = (IrradianceTexels + 2) * (IrradianceTexels + 2) * 16 + (DepthTexels + 2) * (DepthTexels + 2) * 8;

    private const Format IrradianceFormat = Format.R16G16B16A16Sfloat;
    private const Format DepthFormat = Format.R16G16Sfloat;

    /// <summary>Must match the Fold block in Shaders/probe_fold.comp.</summary>
    private struct FoldConstants
    {
        public int Slot;
        public float MaxDistance;
    }

    /// <summary>Must match the Blend block in Shaders/probe_blend.comp.</summary>
    private struct BlendConstants
    {
        public float Alpha;
        public int Count;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _facesSampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _foldPipeline;
    private readonly Pipeline _blendPipeline;

    private Image _irradiance;
    private GpuAllocation _irradianceMemory;
    private Image _depth;
    private GpuAllocation _depthMemory;
    private GpuBuffer _staging = null!;
    private GpuBuffer _ids = null!;

    public readonly CaptureCube Capture;

    /// <summary>
    /// Whether this GPU can fold captures into the atlases. The depth atlas has two channels,
    /// which compute shaders can only write with shaderStorageImageExtendedFormats. Without it
    /// the atlases are still bound, but nothing is captured and surfaces use the sky.
    /// </summary>
    public readonly bool Supported;

    /// <summary>Which probes of the fixed grid to capture next.</summary>
    public readonly ProbeSchedule Schedule = new();

    public ProbeVolume(VulkanContext context, ProbeQuality quality)
    {
        _context = context;
        Supported = context.StorageImageExtendedFormats;
        var api = context.Api;

        Capture = new CaptureCube(context, FaceSize);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f,
        };
        api.CreateSampler(context.Device, &samplerInfo, null, out _facesSampler).Check();

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 5,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, 1),
            new(DescriptorType.StorageBuffer, 2),
            new(DescriptorType.StorageImage, 2),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 3,
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

        // The fold's and the blend's constants are both 8 bytes.
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

        if (Supported)
        {
            _foldPipeline = PipelineFactory.CreateCompute(context, "probe_fold.comp", _pipelineLayout);
            _blendPipeline = PipelineFactory.CreateCompute(context, "probe_blend.comp", _pipelineLayout);
        }

        CreateStorage(quality);
    }

    public ProbeQuality Quality { get; private set; }

    /// <summary>Tiles across each atlas layer.</summary>
    public int Columns { get; private set; }

    /// <summary>Tile rows in each atlas layer.</summary>
    public int Rows { get; private set; }

    /// <summary>The most probes one <see cref="RecordBlend"/> takes: the traced budget, the larger of the two.</summary>
    public int StagingSlots { get; private set; }

    /// <summary>Folded tiles waiting to be blended into the atlases, <see cref="StagedTileBytes"/> each.</summary>
    public Buffer StagingTiles => _staging.Handle;

    public ImageView IrradianceView { get; private set; }

    public ImageView DepthView { get; private set; }

    /// <summary>
    /// Whether any cascade tile has been written since the atlases were last cleared. Until one is,
    /// surfaces skip the cascades.
    /// </summary>
    public bool CascadesWritten { get; private set; }

    /// <summary>
    /// The bytes the atlases of a quality take: five layers of tiles, each layer with room for a
    /// cascade's probes or the fixed grid's <see cref="ProbeGrid.MaxProbes"/>, whichever is more.
    /// </summary>
    public static long AtlasBytes(ProbeQuality quality)
    {
        var (columns, rows) = Grid(quality);
        long irradiance = (long)columns * (IrradianceTexels + 2) * rows * (IrradianceTexels + 2) * 8;
        long depth = (long)columns * (DepthTexels + 2) * rows * (DepthTexels + 2) * 4;
        return (irradiance + depth) * Layers;
    }

    /// <summary>
    /// Makes the atlases and staging for another quality, starting every probe uncaptured. Waits for
    /// the device, so call it only when the settings change, never on the frame path. The views
    /// change, so the caller must write its descriptors again.
    /// </summary>
    public void Resize(ProbeQuality quality)
    {
        if (quality == Quality)
        {
            return;
        }

        _context.Api.DeviceWaitIdle(_context.Device).Check();
        DestroyStorage();
        CreateStorage(quality);
    }

    /// <summary>
    /// Folds the six faces in <see cref="Capture"/> into one staging slot, after the last face
    /// has been copied in. Follow it with <see cref="RecordBlend"/> to put the tile in an atlas.
    /// </summary>
    public void RecordFold(CommandBuffer cmd, int slot, float spacing)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, StagingSlots);
        var api = _context.Api;

        // The last blend may still be reading the staging slot.
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);

        var set = _set;
        // Further than this, a probe can't light anything, so distances stop there.
        var constants = new FoldConstants { Slot = slot, MaxDistance = spacing * 2f };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _foldPipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FoldConstants), &constants);
        api.CmdDispatch(cmd, 1, 1, 1);
    }

    /// <summary>
    /// Blends staging slots 0 to batch.Count - 1 into the probes the batch names, in the same
    /// order: a probe never captured takes its new tile whole, and one already captured moves
    /// <paramref name="alpha"/> of the way towards it. Surfaces lit from the probes in this same
    /// command buffer, and in later ones, wait for it. Allocates nothing.
    /// </summary>
    public void RecordBlend(CommandBuffer cmd, IReadOnlyList<ProbeId> batch, float alpha)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch.Count, StagingSlots);
        var api = _context.Api;

        // ids is two ints per probe, and StagingSlots keeps it well under CmdUpdateBuffer's 65,536 bytes.
        int count = batch.Count;
        int* ids = stackalloc int[count * 2];
        for (int i = 0; i < count; i++)
        {
            var id = batch[i];
            if (id.Cascade < 0 || id.Cascade >= Layers || id.Slot < 0 || id.Slot >= Columns * Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(batch), id, "There is no such probe in the atlases.");
            }

            ids[i * 2] = id.Cascade;
            ids[i * 2 + 1] = id.Slot;
            if (id.Cascade < FixedGridLayer)
            {
                CascadesWritten = true;
            }
        }

        // The last blend may still be reading the ids.
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit,
            PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit);
        api.CmdUpdateBuffer(cmd, _ids.Handle, 0, (ulong)(count * 8), ids);

        // Wait for the ids, the folds or copies that filled the staging slots, and anything still
        // reading the atlases.
        Barrier(cmd,
            PipelineStageFlags2.AllTransferBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

        var set = _set;
        var constants = new BlendConstants { Alpha = alpha, Count = count };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _blendPipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(BlendConstants), &constants);
        api.CmdDispatch(cmd, (uint)count, 1, 1);

        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
    }

    /// <summary>Clears every layer of both atlases, so every probe is uncaptured again.</summary>
    public void RecordClear(CommandBuffer cmd)
    {
        var api = _context.Api;
        Barrier(cmd,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
            PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
        var black = new ClearColorValue(0f, 0f, 0f, 0f);
        var cleared = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, Layers);
        api.CmdClearColorImage(cmd, _irradiance, ImageLayout.General, &black, 1, &cleared);
        api.CmdClearColorImage(cmd, _depth, ImageLayout.General, &black, 1, &cleared);
        Barrier(cmd, PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        CascadesWritten = false;
    }

    public void Dispose()
    {
        var api = _context.Api;
        DestroyStorage();
        if (Supported)
        {
            api.DestroyPipeline(_context.Device, _foldPipeline, null);
            api.DestroyPipeline(_context.Device, _blendPipeline, null);
        }

        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        api.DestroySampler(_context.Device, _facesSampler, null);
        Capture.Dispose();
    }

    // Enough tiles for a cascade or the fixed grid, laid out near square.
    private static (int Columns, int Rows) Grid(ProbeQuality quality)
    {
        int tiles = Math.Max(ProbeCascades.Layouts(quality)[0].Total, ProbeGrid.MaxProbes);
        int columns = (int)Math.Ceiling(Math.Sqrt(tiles));
        int rows = (tiles + columns - 1) / columns;
        return (columns, rows);
    }

    private void CreateStorage(ProbeQuality quality)
    {
        Quality = quality;
        (Columns, Rows) = Grid(quality);
        StagingSlots = ProbeBudget.For(quality).Traced;
        CascadesWritten = false;

        (_irradiance, _irradianceMemory, var irradianceView) = CreateAtlas(
            (uint)(Columns * (IrradianceTexels + 2)), (uint)(Rows * (IrradianceTexels + 2)), IrradianceFormat);
        (_depth, _depthMemory, var depthView) = CreateAtlas(
            (uint)(Columns * (DepthTexels + 2)), (uint)(Rows * (DepthTexels + 2)), DepthFormat);
        IrradianceView = irradianceView;
        DepthView = depthView;

        _staging = new GpuBuffer(_context, (ulong)StagingSlots * StagedTileBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        _ids = new GpuBuffer(_context, (ulong)StagingSlots * 8,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);

        if (Supported)
        {
            WriteDescriptors();
        }
    }

    private void DestroyStorage()
    {
        var api = _context.Api;
        api.DestroyImageView(_context.Device, IrradianceView, null);
        api.DestroyImageView(_context.Device, DepthView, null);
        api.DestroyImage(_context.Device, _irradiance, null);
        api.DestroyImage(_context.Device, _depth, null);
        _context.Memory.Free(_irradianceMemory);
        _context.Memory.Free(_depthMemory);
        _staging.Dispose();
        _ids.Dispose();
    }

    private void WriteDescriptors()
    {
        var facesInfo = new DescriptorImageInfo(_facesSampler, Capture.ArrayView, ImageLayout.ShaderReadOnlyOptimal);
        var stagingInfo = new DescriptorBufferInfo(_staging.Handle, 0, Vk.WholeSize);
        var idsInfo = new DescriptorBufferInfo(_ids.Handle, 0, Vk.WholeSize);
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
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &stagingInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &idsInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 3,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &irradianceInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 4,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &depthInfo,
            },
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
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

    /// <summary>
    /// A five-layer atlas that stays in the general layout for good, so the blend writes it and lighting samples it.
    /// It starts cleared: black, and every probe uncaptured.
    /// </summary>
    private (Image Image, GpuAllocation Memory, ImageView View) CreateAtlas(uint width, uint height, Format format)
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
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = Layers,
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

        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, Layers);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2DArray,
            Format = format,
            SubresourceRange = range,
        };
        api.CreateImageView(_context.Device, &viewInfo, null, out var view).Check();

        _context.SubmitAndWait(cmd =>
        {
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.None,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.ClearBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range,
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier,
            };
            api.CmdPipelineBarrier2(cmd, &dependency);
            var black = new ClearColorValue(0f, 0f, 0f, 0f);
            var cleared = range;
            api.CmdClearColorImage(cmd, image, ImageLayout.General, &black, 1, &cleared);
            Barrier(cmd, PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        });
        return (image, memory, view);
    }
}
