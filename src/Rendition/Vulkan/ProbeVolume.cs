using Rendition.Passes;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>
/// The light probes' data on the GPU. Each probe has a tile in two atlases: the light reaching
/// it from every direction, and the mean and mean square of the distance it sees in every
/// direction, which lets a surface tell whether a probe is behind a wall from it. Each atlas is a
/// 2D array of five layers: the four cascades that follow the camera, finest first, then the fixed
/// grid in <see cref="FixedGridLayer"/>. A frame's probe update draws each probe's six faces into
/// its slot of <see cref="FacesImage"/>, <see cref="RecordFold"/> folds them into a slot of
/// <see cref="StagingTiles"/>, and <see cref="RecordBlend"/> blends a batch of slots into the atlases.
///
/// Nothing real is made until <see cref="EnsureStorage"/>: until then the atlases are a one-tile
/// stand-in, so a renderer with the probes switched off holds almost nothing.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed unsafe class ProbeVolume : IDisposable
{
    /// <summary>How many texels across each captured face is. The probes keep only soft light, so this is plenty.</summary>
    public const uint FaceSize = 16;

    /// <summary>
    /// What a face's alpha holds where nothing was drawn: further than any probe looks. Must match
    /// SKY_MARK in probe_fold.comp.
    /// </summary>
    public const float SkyMark = 1e4f;

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

    /// <summary>The faces' colour: the light seen in rgb and the distance in alpha.</summary>
    public const Format FacesFormat = Format.R16G16B16A16Sfloat;

    private const Format IrradianceFormat = Format.R16G16B16A16Sfloat;
    private const Format DepthFormat = Format.R16G16Sfloat;

    // The largest of the three compute shaders' constants.
    private const uint PushBytes = 64;

    /// <summary>Must match the Fold block in Shaders/probe_fold.comp.</summary>
    private struct FoldConstants
    {
        public int Slot;
        public float MaxDistance;
        public float EnvironmentIntensity;
        public float Unused;
        public System.Numerics.Vector4 SkySun;
    }

    /// <summary>Must match the Blend block in Shaders/probe_blend.comp.</summary>
    private struct BlendConstants
    {
        public float Alpha;
        public int Count;
        public float Snap;
    }

    /// <summary>Must match the Invalidate block in Shaders/probe_invalidate.comp.</summary>
    private struct InvalidateConstants
    {
        public int OriginX, OriginY, OriginZ, Layer;
        public int CountX, CountY, CountZ, Total;
        public int StartX, StartY, StartZ, Unused0;
        public int SlabX, SlabY, SlabZ, Unused1;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _facesSampler;
    private readonly Sampler _skySampler;
    private readonly ImageView _sky;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _foldPipeline;
    private readonly Pipeline _blendPipeline;
    private readonly Pipeline _invalidatePipeline;

    private Image _irradiance;
    private GpuAllocation _irradianceMemory;
    private Image _depth;
    private GpuAllocation _depthMemory;
    private GpuBuffer _staging = null!;
    private GpuBuffer _ids = null!;

    private Image _facesColor;
    private GpuAllocation _facesColorMemory;
    private Image _facesDepth;
    private GpuAllocation _facesDepthMemory;
    private ImageView _facesArray;
    private ImageView[] _slotColorViews = [];
    private ImageView[] _slotDepthViews = [];

    /// <summary>
    /// Whether this GPU can update the probes. The depth atlas has two channels, which compute shaders
    /// can only write with shaderStorageImageExtendedFormats, and a probe's six faces are drawn at once
    /// with multiview. Without both the atlases are still bound, but nothing is captured and surfaces use the sky.
    /// </summary>
    public readonly bool Supported;

    /// <summary>
    /// Makes the stand-in atlases. <paramref name="sky"/> is the sharp sky, which the fold reads where
    /// a probe saw nothing.
    /// </summary>
    public ProbeVolume(VulkanContext context, Sampler skySampler, ImageView sky)
    {
        _context = context;
        _skySampler = skySampler;
        _sky = sky;
        Supported = context.StorageImageExtendedFormats && context.Multiview;
        var api = context.Api;

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

        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(5, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            1,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 2),
            new DescriptorPoolSize(DescriptorType.StorageImage, 2));
        _set = context.AllocateSet(_pool, _setLayout);

        _pipelineLayout = context.CreatePipelineLayout(
            [_setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, PushBytes));

        if (Supported)
        {
            _foldPipeline = PipelineFactory.CreateCompute(context, "probe_fold.comp", _pipelineLayout);
            _blendPipeline = PipelineFactory.CreateCompute(context, "probe_blend.comp", _pipelineLayout);
            _invalidatePipeline = PipelineFactory.CreateCompute(context, "probe_invalidate.comp", _pipelineLayout);
        }

        CreatePlaceholder();
    }

    /// <summary>Whether the real atlases and faces exist, rather than the stand-in.</summary>
    public bool Allocated { get; private set; }

    /// <summary>The quality the storage was made for. Only meaningful while <see cref="Allocated"/>.</summary>
    public ProbeQuality Quality { get; private set; }

    /// <summary>Tiles across each atlas layer.</summary>
    public int Columns { get; private set; }

    /// <summary>Tile rows in each atlas layer.</summary>
    public int Rows { get; private set; }

    /// <summary>The most probes one <see cref="RecordBlend"/> takes: the traced budget, the larger of the two.</summary>
    public int StagingSlots { get; private set; }

    /// <summary>How many probes' faces fit in <see cref="FacesImage"/>: the rasterized budget.</summary>
    public int FaceSlots { get; private set; }

    /// <summary>Folded tiles waiting to be blended into the atlases, <see cref="StagedTileBytes"/> each.</summary>
    public Buffer StagingTiles => _staging.Handle;

    public ImageView IrradianceView { get; private set; }

    public ImageView DepthView { get; private set; }

    /// <summary>Every probe's six faces, six layers per slot of the batch. Only while <see cref="Allocated"/>.</summary>
    public Image FacesImage => _facesColor;

    /// <summary>The depth the faces are drawn with, laid out as <see cref="FacesImage"/>.</summary>
    public Image FacesDepthImage => _facesDepth;

    /// <summary>The six colour layers of one slot, as a 2D array to draw all six faces at once with multiview.</summary>
    public ImageView SlotColorView(int slot) => _slotColorViews[slot];

    /// <summary>The six depth layers of one slot.</summary>
    public ImageView SlotDepthView(int slot) => _slotDepthViews[slot];

    /// <summary>
    /// Whether any cascade tile has been written since the atlases were last cleared. Until one is,
    /// surfaces skip the cascades.
    /// </summary>
    public bool CascadesWritten { get; private set; }

    /// <summary>
    /// Makes the atlases, faces and staging for a quality, if they aren't already, with every probe
    /// uncaptured. Waits for the device when it makes them, so it is for the first frame with the
    /// probes on and for a change of quality, never every frame. Returns true when the views changed,
    /// so the caller must write its descriptors again.
    /// </summary>
    public bool EnsureStorage(ProbeQuality quality)
    {
        if (Allocated && quality == Quality)
        {
            return false;
        }

        _context.Api.DeviceWaitIdle(_context.Device).Check();
        DestroyStorage();
        CreateStorage(quality);
        return true;
    }

    /// <summary>
    /// Frees the atlases and faces and puts the stand-in back, for when the probes are switched off.
    /// Waits for the device. Returns true when there was anything to free, so the caller must write
    /// its descriptors again.
    /// </summary>
    public bool Release()
    {
        if (!Allocated)
        {
            return false;
        }

        _context.Api.DeviceWaitIdle(_context.Device).Check();
        DestroyStorage();
        CreatePlaceholder();
        return true;
    }

    /// <summary>
    /// Readies the faces just drawn for folding: call once after the batch's captures and before its
    /// <see cref="RecordFold"/> calls. The last blend may still be reading the staging slots.
    /// </summary>
    public void BeginFolds(CommandBuffer cmd)
    {
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);
    }

    /// <summary>
    /// Folds the six faces of one slot of <see cref="FacesImage"/> into the same staging slot.
    /// <paramref name="spacing"/> is the probe's cascade's: further than two spacings a probe lights
    /// nothing, so distances stop there. Where it saw nothing it sees the sky: the environment times
    /// <paramref name="environmentIntensity"/>, with the clear sky of <paramref name="skySun"/>, whether or
    /// not the view shows the environment there, so a probe in the open reads as no probe does.
    /// </summary>
    public void RecordFold(
        CommandBuffer cmd, int slot, float spacing, float environmentIntensity, System.Numerics.Vector4 skySun)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Math.Min(StagingSlots, FaceSlots));
        var api = _context.Api;

        var set = _set;
        var constants = new FoldConstants
        {
            Slot = slot,
            MaxDistance = spacing * 2f,
            EnvironmentIntensity = environmentIntensity,
            SkySun = skySun,
        };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _foldPipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FoldConstants), &constants);
        api.CmdDispatch(cmd, 1, 1, 1);
    }

    /// <summary>
    /// Copies the first <paramref name="slots"/> staging slots, as the folds recorded before it wrote them, into
    /// <paramref name="buffer"/>, <see cref="StagedTileBytes"/> each. The baker folds its traced faces this way and
    /// writes the tiles into its own atlases.
    /// </summary>
    public void RecordStagingReadBack(CommandBuffer cmd, GpuBuffer buffer, int slots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots, StagingSlots);
        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        var region = new BufferCopy(0, 0, (ulong)slots * StagedTileBytes);
        _context.Api.CmdCopyBuffer(cmd, _staging.Handle, buffer.Handle, 1, &region);
    }

    /// <summary>
    /// Makes uncaptured every probe of one atlas layer whose cell lies in a slab: cell x in
    /// [start.X, start.X + count.X), or the same along y or z. The cells of the layer's box run from
    /// <paramref name="origin"/> for <paramref name="countX"/> by countY by countZ, and wrap to their
    /// slots as <see cref="ProbeCascades.Slot"/> does. A slab with no cells is (0, 0).
    /// </summary>
    public void RecordInvalidate(
        CommandBuffer cmd, int layer, Int3 origin, int countX, int countY, int countZ, Int3 slabStart, Int3 slabCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, Layers);
        int total = countX * countY * countZ;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(total, Columns * Rows);
        var api = _context.Api;

        // Wait for anything still reading or writing the atlas.
        Barrier(cmd,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);

        var set = _set;
        var constants = new InvalidateConstants
        {
            OriginX = origin.X, OriginY = origin.Y, OriginZ = origin.Z, Layer = layer,
            CountX = countX, CountY = countY, CountZ = countZ, Total = total,
            StartX = slabStart.X, StartY = slabStart.Y, StartZ = slabStart.Z,
            SlabX = slabCount.X, SlabY = slabCount.Y, SlabZ = slabCount.Z,
        };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _invalidatePipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(InvalidateConstants), &constants);
        api.CmdDispatch(cmd, (uint)((total + 63) / 64), 1, 1);

        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
    }

    /// <summary>
    /// Blends staging slots 0 to batch.Count - 1 into the probes the batch names, in the same
    /// order: a probe never captured takes its new tile whole, and one already captured moves
    /// <paramref name="alpha"/> of the way towards it. With <paramref name="snap"/> above 0, a probe whose
    /// mean light changed by more moves further, all the way once the change is <paramref name="snap"/> of its
    /// light. Surfaces lit from the probes in this same command buffer, and in later ones, wait for it.
    /// Allocates nothing.
    /// </summary>
    public void RecordBlend(CommandBuffer cmd, IReadOnlyList<ProbeId> batch, float alpha, float snap = 0f)
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
        var constants = new BlendConstants { Alpha = alpha, Count = count, Snap = snap };
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
            api.DestroyPipeline(_context.Device, _invalidatePipeline, null);
        }

        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        api.DestroySampler(_context.Device, _facesSampler, null);
    }

    // Enough tiles for a cascade or the fixed grid, laid out near square.
    private static (int Columns, int Rows) Grid(ProbeQuality quality)
    {
        int tiles = Math.Max(ProbeCascades.Layouts(quality)[0].Total, ProbeGrid.MaxProbes);
        int columns = (int)Math.Ceiling(Math.Sqrt(tiles));
        int rows = (tiles + columns - 1) / columns;
        return (columns, rows);
    }

    // One tile per layer and one staging slot: enough to bind, and to keep the test hooks working.
    private void CreatePlaceholder()
    {
        Allocated = false;
        Columns = 1;
        Rows = 1;
        StagingSlots = 1;
        FaceSlots = 0;
        CreateAtlases();
    }

    private void CreateStorage(ProbeQuality quality)
    {
        Quality = quality;
        (Columns, Rows) = Grid(quality);
        var budget = ProbeBudget.For(quality);
        StagingSlots = budget.Traced;
        FaceSlots = budget.Raster;
        CreateAtlases();

        uint layers = (uint)(FaceSlots * 6);
        (_facesColor, _facesColorMemory) = CreateFacesImage(
            FacesFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, layers);
        (_facesDepth, _facesDepthMemory) = CreateFacesImage(GBuffer.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, layers);
        _facesArray = CreateFacesView(_facesColor, FacesFormat, ImageAspectFlags.ColorBit, 0, layers);
        _slotColorViews = new ImageView[FaceSlots];
        _slotDepthViews = new ImageView[FaceSlots];
        for (int slot = 0; slot < FaceSlots; slot++)
        {
            _slotColorViews[slot] = CreateFacesView(_facesColor, FacesFormat, ImageAspectFlags.ColorBit, (uint)slot * 6, 6);
            _slotDepthViews[slot] = CreateFacesView(_facesDepth, GBuffer.DepthFormat, ImageAspectFlags.DepthBit, (uint)slot * 6, 6);
        }

        // The fold samples the faces, so they start readable for the first frame's barriers.
        var api = _context.Api;
        var faces = _facesColor;
        _context.SubmitAndWait(cmd => api.Transition(cmd, faces, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.None, AccessFlags2.None,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit, layerCount: layers));

        Allocated = true;
        if (Supported)
        {
            WriteDescriptors();
        }
    }

    private void CreateAtlases()
    {
        CascadesWritten = false;
        (_irradiance, _irradianceMemory, var irradianceView) = CreateAtlas(
            (uint)(Columns * (IrradianceTexels + 2)), (uint)(Rows * (IrradianceTexels + 2)), IrradianceFormat);
        (_depth, _depthMemory, var depthView) = CreateAtlas(
            (uint)(Columns * (DepthTexels + 2)), (uint)(Rows * (DepthTexels + 2)), DepthFormat);
        IrradianceView = irradianceView;
        DepthView = depthView;

        _staging = new GpuBuffer(_context, (ulong)StagingSlots * StagedTileBytes,
            BufferUsageFlags.TransferSrcBit |
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        _ids = new GpuBuffer(_context, (ulong)StagingSlots * 8,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
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

        if (Allocated)
        {
            foreach (var view in _slotColorViews)
            {
                api.DestroyImageView(_context.Device, view, null);
            }

            foreach (var view in _slotDepthViews)
            {
                api.DestroyImageView(_context.Device, view, null);
            }

            api.DestroyImageView(_context.Device, _facesArray, null);
            api.DestroyImage(_context.Device, _facesColor, null);
            api.DestroyImage(_context.Device, _facesDepth, null);
            _context.Memory.Free(_facesColorMemory);
            _context.Memory.Free(_facesDepthMemory);
            _slotColorViews = [];
            _slotDepthViews = [];
            Allocated = false;
        }
    }

    private void WriteDescriptors()
    {
        var facesInfo = new DescriptorImageInfo(_facesSampler, _facesArray, ImageLayout.ShaderReadOnlyOptimal);
        var stagingInfo = new DescriptorBufferInfo(_staging.Handle, 0, Vk.WholeSize);
        var idsInfo = new DescriptorBufferInfo(_ids.Handle, 0, Vk.WholeSize);
        var irradianceInfo = new DescriptorImageInfo(default, IrradianceView, ImageLayout.General);
        var depthInfo = new DescriptorImageInfo(default, DepthView, ImageLayout.General);
        var skyInfo = new DescriptorImageInfo(_skySampler, _sky, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Write(0, DescriptorType.CombinedImageSampler, &facesInfo, null),
            Write(1, DescriptorType.StorageBuffer, null, &stagingInfo),
            Write(2, DescriptorType.StorageBuffer, null, &idsInfo),
            Write(3, DescriptorType.StorageImage, &irradianceInfo, null),
            Write(4, DescriptorType.StorageImage, &depthInfo, null),
            Write(5, DescriptorType.CombinedImageSampler, &skyInfo, null),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    private WriteDescriptorSet Write(uint binding, DescriptorType type, DescriptorImageInfo* image, DescriptorBufferInfo* buffer) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PImageInfo = image,
        PBufferInfo = buffer,
    };

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

    private (Image Image, GpuAllocation Memory) CreateFacesImage(Format format, ImageUsageFlags usage, uint layers)
    {
        var api = _context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(FaceSize, FaceSize, 1),
            MipLevels = 1,
            ArrayLayers = layers,
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
        return (image, memory);
    }

    private ImageView CreateFacesView(Image image, Format format, ImageAspectFlags aspect, uint firstLayer, uint layers)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2DArray,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, firstLayer, layers),
        };
        _context.Api.CreateImageView(_context.Device, &viewInfo, null, out var view).Check();
        return view;
    }

    /// <summary>
    /// A five-layer atlas that stays in the general layout for good, so the blend writes it and lighting samples it.
    /// It starts cleared: black, and every probe uncaptured.
    /// </summary>
    private (Image Image, GpuAllocation Memory, ImageView View) CreateAtlas(uint width, uint height, Format format)
    {
        var api = _context.Api;
        var usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit;
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
