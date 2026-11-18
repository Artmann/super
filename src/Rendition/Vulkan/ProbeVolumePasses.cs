using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One atlas layer's cells to make uncaptured: the layer's box runs from <paramref name="Origin"/> for
/// <paramref name="Counts"/> cells, and the cells to clear are those in the slab from <paramref name="SlabStart"/> for
/// <paramref name="SlabCount"/> along any axis. A slab with no cells is (0, 0).
/// </summary>
internal readonly record struct ProbeInvalidation(int Layer, Int3 Origin, Int3 Counts, Int3 SlabStart, Int3 SlabCount);

/// <summary>
/// The light probes' three compute passes and the descriptor set they share: probe_fold.comp folds a probe's six faces
/// into a staging slot, probe_blend.comp blends staging slots into the atlases, and probe_invalidate.comp makes a slab
/// of cells uncaptured. Without <c>supported</c> there are no pipelines, and nothing may be recorded.
/// </summary>
internal sealed unsafe class ProbeVolumePasses : IDisposable
{
    // The largest of the three compute shaders' constants.
    private const uint PushBytes = 64;

    /// <summary>Must match the Fold block in Shaders/probe_fold.comp.</summary>
    private struct FoldConstants
    {
        public int Slot;
        public float MaxDistance;
        public float EnvironmentIntensity;
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
    private readonly (Sampler Sampler, ImageView View) _sky;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline? _foldPipeline;
    private readonly LivePipeline? _blendPipeline;
    private readonly LivePipeline? _invalidatePipeline;

    /// <summary>The passes, reading <paramref name="sky"/> where a probe saw nothing.</summary>
    public ProbeVolumePasses(VulkanContext context, (Sampler Sampler, ImageView View) sky, bool supported)
    {
        _context = context;
        _sky = sky;
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
        context.Api.CreateSampler(context.Device, &samplerInfo, null, out _facesSampler).Check();

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
        _pipelineLayout = context.CreatePipelineLayout([_setLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, PushBytes));

        if (supported)
        {
            _foldPipeline = PipelineFactory.CreateCompute(context, "probe_fold.comp", _pipelineLayout);
            _blendPipeline = PipelineFactory.CreateCompute(context, "probe_blend.comp", _pipelineLayout);
            _invalidatePipeline = PipelineFactory.CreateCompute(context, "probe_invalidate.comp", _pipelineLayout);
        }
    }

    /// <summary>Points the set at the faces the fold reads and the atlases, staging slots and ids the passes use.</summary>
    public void PointAt(ProbeVolumeAtlases atlases, ImageView faces)
    {
        var facesInfo = new DescriptorImageInfo(_facesSampler, faces, ImageLayout.ShaderReadOnlyOptimal);
        var stagingInfo = new DescriptorBufferInfo(atlases.Staging.Handle, 0, Vk.WholeSize);
        var idsInfo = new DescriptorBufferInfo(atlases.Ids.Handle, 0, Vk.WholeSize);
        var irradianceInfo = new DescriptorImageInfo(default, atlases.IrradianceView, ImageLayout.General);
        var depthInfo = new DescriptorImageInfo(default, atlases.DepthView, ImageLayout.General);
        var skyInfo = new DescriptorImageInfo(_sky.Sampler, _sky.View, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(_set, 0, DescriptorType.CombinedImageSampler, &facesInfo),
            Descriptors.BufferWrite(_set, 1, &stagingInfo),
            Descriptors.BufferWrite(_set, 2, &idsInfo),
            Descriptors.ImageWrite(_set, 3, DescriptorType.StorageImage, &irradianceInfo),
            Descriptors.ImageWrite(_set, 4, DescriptorType.StorageImage, &depthInfo),
            Descriptors.ImageWrite(_set, 5, DescriptorType.CombinedImageSampler, &skyInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    /// <summary>Folds the six faces of one slot into the same staging slot, with distances stopping at <paramref name="maxDistance"/>.</summary>
    public void RecordFold(CommandBuffer cmd, int slot, float maxDistance, float environmentIntensity)
    {
        var constants = new FoldConstants { Slot = slot, MaxDistance = maxDistance, EnvironmentIntensity = environmentIntensity };
        Dispatch(cmd, _foldPipeline!, &constants, (uint)sizeof(FoldConstants), 1);
    }

    /// <summary>Makes uncaptured the cells of a slab of one layer, <paramref name="total"/> cells in the layer's box.</summary>
    public void RecordInvalidate(CommandBuffer cmd, in ProbeInvalidation cells, int total)
    {
        var constants = new InvalidateConstants
        {
            OriginX = cells.Origin.X, OriginY = cells.Origin.Y, OriginZ = cells.Origin.Z, Layer = cells.Layer,
            CountX = cells.Counts.X, CountY = cells.Counts.Y, CountZ = cells.Counts.Z, Total = total,
            StartX = cells.SlabStart.X, StartY = cells.SlabStart.Y, StartZ = cells.SlabStart.Z,
            SlabX = cells.SlabCount.X, SlabY = cells.SlabCount.Y, SlabZ = cells.SlabCount.Z,
        };
        Dispatch(cmd, _invalidatePipeline!, &constants, (uint)sizeof(InvalidateConstants), (uint)((total + 63) / 64));
    }

    /// <summary>Blends the first <paramref name="count"/> staging slots into the probes the ids name.</summary>
    public void RecordBlend(CommandBuffer cmd, int count, float alpha, float snap)
    {
        var constants = new BlendConstants { Alpha = alpha, Count = count, Snap = snap };
        Dispatch(cmd, _blendPipeline!, &constants, (uint)sizeof(BlendConstants), (uint)count);
    }

    public void Dispose()
    {
        var api = _context.Api;
        _foldPipeline?.Destroy();
        _blendPipeline?.Destroy();
        _invalidatePipeline?.Destroy();
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        api.DestroySampler(_context.Device, _facesSampler, null);
    }

    private void Dispatch(CommandBuffer cmd, LivePipeline pipeline, void* constants, uint size, uint groups)
    {
        var api = _context.Api;
        var set = _set;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, size, constants);
        api.CmdDispatch(cmd, groups, 1, 1);
    }
}
