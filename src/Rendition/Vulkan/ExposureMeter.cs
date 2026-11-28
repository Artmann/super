using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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

    private const int GroupSize = 16;

    /// <summary>Must match Metering in exposure.glsl. Both passes push the same.</summary>
    private struct MeteringConstants
    {
        public float Rise;
        public float Fall;
        public float Darkest;
        public float Brightest;
        public float Centre;
        public int Snap;
    }

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly Sampler _sampler;
    private readonly GpuBuffer _state;
    private readonly BufferId _stateId;
    private readonly DescriptorPool _pool;
    private readonly ComputePass _histogram;
    private readonly ComputePass _adapt;
    private readonly CommandList _commands = new();
    private Extent2D _sourceSize;

    /// <summary>The state buffer at binding 0, for the compute passes and the tonemap, and the frame at binding 1.</summary>
    public readonly DescriptorSetLayout SetLayout;
    /// <summary>The tonemap binds this to read the adapted exposure.</summary>
    public readonly DescriptorSet Set;

    /// <summary>
    /// A meter sampling the frame through <paramref name="sampler"/>. Its set is made outside the GPU layer, as the
    /// tonemap and FSR, which haven't moved yet, read it too; the meter's passes borrow its layout.
    /// </summary>
    public ExposureMeter((VulkanContext Context, VulkanGpu Gpu) device, Sampler sampler)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _sampler = sampler;

        _state = new GpuBuffer(
            context, StateSize, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        _stateId = gpu.Adopt(_state.Handle, (long)StateSize);

        (SetLayout, _pool, Set) = context.CreateStateSet(ShaderStageFlags.FragmentBit, new DescriptorBufferInfo(_state.Handle, 0, StateSize));

        _histogram = new ComputePass(gpu, "exposure_histogram.comp", [SetLayout]);
        _adapt = new ComputePass(gpu, "exposure_adapt.comp", [SetLayout]);

        // Empty bins and an exposure of 0 EV. The first metered frame snaps, so the 0 is never seen.
        _commands.ClearBuffer(_stateId, 0, (int)StateSize);
        _context.SubmitAndWait(cmd => _gpu.Play(_commands, cmd));
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

    /// <summary>
    /// Counts this frame's pixels and moves the exposure by <paramref name="step"/>, before the tonemap reads it. Each
    /// dispatch is a compute pass, which sees what was drawn and dispatched before it, so the meter places no barriers.
    /// </summary>
    public void Record(CommandBuffer cmd, ExposureStep step)
    {
        var aim = step.Aim;
        var constants = new MeteringConstants
        {
            Rise = step.Rise,
            Fall = step.Fall,
            Darkest = aim.Darkest,
            Brightest = aim.Brightest,
            Centre = aim.Centre,
            Snap = step.Snap ? 1 : 0,
        };
        var bytes = new ReadOnlySpan<byte>(&constants, sizeof(MeteringConstants));
        var groups = (((int)_sourceSize.Width + GroupSize - 1) / GroupSize, ((int)_sourceSize.Height + GroupSize - 1) / GroupSize, 1);
        BindGroupId set = _gpu.Adopt(Set);

        _commands.Reset();
        _histogram.Record(_commands, [set], bytes, groups);
        _adapt.Record(_commands, [set], bytes, (1, 1, 1));
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        var api = _context.Api;
        _histogram.Dispose();
        _adapt.Dispose();
        _gpu.Release(_stateId);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, SetLayout, null);
        _state.Dispose();
    }
}
