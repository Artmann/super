using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Meters the lit frame for auto exposure, all on the GPU: exposure_histogram.comp counts its
/// pixels by brightness, then exposure_adapt.comp moves the exposure it keeps in a small buffer
/// towards the middle of them, and the tonemap reads that buffer. Nothing comes back to the CPU,
/// which only says how far the exposure may move each frame. See "Auto exposure" in docs/design.md.
/// </summary>
internal sealed unsafe class ExposureMeter : IDisposable
{
    /// <summary>The bins, then the adapted EV and a pad. Must match ExposureState in exposure.glsl.</summary>
    private const ulong StateSize = (Exposure.Bins + 2) * sizeof(uint);

    private const uint GroupSize = 16;

    /// <summary>Must match Adapt in exposure_adapt.comp.</summary>
    private struct AdaptConstants
    {
        public float MaxStep;
        public int Snap;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly GpuBuffer _state;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _histogram;
    private readonly Pipeline _adapt;
    private Extent2D _sourceSize;

    /// <summary>The state buffer at binding 0, for the compute passes and the tonemap, and the frame at binding 1.</summary>
    public readonly DescriptorSetLayout SetLayout;
    /// <summary>The tonemap binds this to read the adapted exposure.</summary>
    public readonly DescriptorSet Set;

    public ExposureMeter(VulkanContext context, Sampler sampler)
    {
        _context = context;
        _sampler = sampler;
        var api = context.Api;

        _state = new GpuBuffer(
            context, StateSize, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out SetLayout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.StorageBuffer, 1),
            new(DescriptorType.CombinedImageSampler, 1),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = SetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out Set).Check();

        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(AdaptConstants));
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &pipelineLayoutInfo, null, out _pipelineLayout).Check();

        _histogram = PipelineFactory.CreateCompute(context, "exposure_histogram.comp", _pipelineLayout);
        _adapt = PipelineFactory.CreateCompute(context, "exposure_adapt.comp", _pipelineLayout);

        var stateInfo = new DescriptorBufferInfo(_state.Handle, 0, StateSize);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &stateInfo,
        };
        api.UpdateDescriptorSets(context.Device, 1, &write, 0, null);

        // Empty bins and an exposure of 0 EV. The first metered frame snaps, so the 0 is never seen.
        var state = _state.Handle;
        _context.SubmitAndWait(cmd =>
        {
            api.CmdFillBuffer(cmd, state, 0, StateSize, 0);
            Barrier(cmd, PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        });
    }

    /// <summary>
    /// Points the histogram at the frame it meters: the lit picture before bloom. Only call it while
    /// no frame in flight uses the set.
    /// </summary>
    public void SetSource(ImageView source, Extent2D size)
    {
        _sourceSize = size;
        var imageInfo = new DescriptorImageInfo(_sampler, source, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <summary>Counts this frame's pixels and moves the exposure by <paramref name="step"/>, before the tonemap reads it.</summary>
    public void Record(CommandBuffer cmd, ExposureStep step)
    {
        var api = _context.Api;

        // The frame was drawn, resolved or copied before this, and last frame's tonemap read the state.
        Barrier(cmd,
            PipelineStageFlags2.ColorAttachmentOutputBit | PipelineStageFlags2.FragmentShaderBit
                | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

        var set = Set;
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _histogram);
        api.CmdDispatch(cmd, (_sourceSize.Width + GroupSize - 1) / GroupSize, (_sourceSize.Height + GroupSize - 1) / GroupSize, 1);

        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

        var constants = new AdaptConstants { MaxStep = step.MaxStep, Snap = step.Snap ? 1 : 0 };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _adapt);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(AdaptConstants), &constants);
        api.CmdDispatch(cmd, 1, 1, 1);

        Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit);
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _histogram, null);
        api.DestroyPipeline(_context.Device, _adapt, null);
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, SetLayout, null);
        _state.Dispose();
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
}
