using System.Numerics;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Captured views of the scene, held in <see cref="ProbeCubes"/>, one cube per probe with one mip per
/// roughness step. The renderer draws each face and this class blurs them into the mips. Each probe
/// keeps its cube until it's removed, and a removed probe's cube goes to the next one added. When no
/// cube is free the array grows. Each frame <see cref="Prepare"/> picks and orders the probes the view sees.
/// </summary>
internal sealed unsafe class ReflectionProbes : IDisposable
{
    private const int MaxProbes = ReflectionProbeOrder.MaxProbes;
    private const int Mips = ProbeCubes.Mips;

    /// <summary>Must match the Filter block in Shaders/reflection_probe.comp.</summary>
    private struct FilterConstants
    {
        public float Roughness;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly CaptureCube _source;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _pipeline;
    private readonly ReflectionProbe?[] _slots = new ReflectionProbe?[MaxProbes];

    /// <summary>The array every probe's cube is in.</summary>
    public ProbeCubes Cubes { get; }

    /// <summary>The probes the last <see cref="Prepare"/> found in view, in the order they blend.</summary>
    public ReflectionProbeOrder Order { get; } = new();

    public int Count { get; private set; }

    public ReflectionProbes(VulkanContext context, Sampler sampler)
    {
        _context = context;
        _sampler = sampler;
        Cubes = new ProbeCubes(context);
        _source = new CaptureCube(context, ProbeCubes.FaceSize);

        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            1,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1),
            new DescriptorPoolSize(DescriptorType.StorageImage, 1));
        _set = context.AllocateSet(_pool, _setLayout);

        _pipelineLayout = context.CreatePipelineLayout(
            [_setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants)));
        _pipeline = PipelineFactory.CreateCompute(context, "reflection_probe.comp", _pipelineLayout);
    }

    /// <summary>The cube the renderer copies each finished face into.</summary>
    public CaptureCube Capture => _source;

    /// <summary>
    /// Writes the probes the view sees to <paramref name="gpu"/> in the order they blend, and says how
    /// many. <see cref="Order"/> keeps the depths their boxes span.
    /// </summary>
    public int Prepare(in Matrix4x4 view, in Matrix4x4 projection, Span<GpuReflectionProbe> gpu)
    {
        Order.Prepare(_slots.AsSpan(0, Cubes.Capacity), view, projection, gpu);
        return Order.Count;
    }

    /// <summary>
    /// Blurs the six captured faces into a new probe, in the first free cube. When none is free
    /// the array grows first, which waits for the GPU.
    /// </summary>
    public ReflectionProbeHandle Add(in ReflectionProbe probe)
    {
        if (Count == MaxProbes)
        {
            throw new InvalidOperationException($"Only {MaxProbes} reflection probes are supported.");
        }

        int slot = Array.IndexOf(_slots, null, 0, Cubes.Capacity);
        if (slot < 0)
        {
            slot = Cubes.Capacity;
            Cubes.Grow();
        }

        Store(slot, probe);
        Count++;
        return new ReflectionProbeHandle(slot);
    }

    /// <summary>Blurs the six captured faces into an existing probe, in place of what it held.</summary>
    public void Replace(ReflectionProbeHandle handle, in ReflectionProbe probe)
    {
        CheckHeld(handle);
        Store(handle.Index, probe);
    }

    /// <summary>Forgets one probe, whose cube goes to the next probe added. The rest keep theirs.</summary>
    public void Remove(ReflectionProbeHandle handle)
    {
        CheckHeld(handle);
        _slots[handle.Index] = null;
        Count--;
    }

    /// <summary>Forgets every probe. Surfaces reflect the sky again from the next frame.</summary>
    public void Clear()
    {
        Array.Clear(_slots);
        Count = 0;
    }

    /// <summary>Whether a handle names a probe that was added and not removed since.</summary>
    public bool Holds(ReflectionProbeHandle handle) => (uint)handle.Index < (uint)Cubes.Capacity && _slots[handle.Index] is not null;

    private void CheckHeld(ReflectionProbeHandle handle)
    {
        if (!Holds(handle))
        {
            throw new ArgumentException($"Reflection probe {handle.Index} does not exist.", nameof(handle));
        }
    }

    private void Store(int index, in ReflectionProbe probe)
    {
        for (int mip = 0; mip < Mips; mip++)
        {
            FilterLevel(index, mip);
        }

        _slots[index] = probe;
    }

    public void Dispose()
    {
        var api = _context.Api;
        _pipeline.Destroy();
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        Cubes.Dispose();
        _source.Dispose();
    }

    private void FilterLevel(int probe, int mip)
    {
        var api = _context.Api;

        var sourceInfo = new DescriptorImageInfo(_sampler, _source.CubeView, ImageLayout.ShaderReadOnlyOptimal);
        var targetInfo = new DescriptorImageInfo(default, Cubes.Level(probe, mip), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &sourceInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &targetInfo,
            },
        };
        api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);

        var image = Cubes.Image;
        var set = _set;
        var layout = _pipelineLayout;
        var pipeline = _pipeline;
        uint baseLayer = (uint)probe * 6;
        uint size = ProbeCubes.FaceSize >> mip;
        var constants = new FilterConstants { Roughness = mip / (float)(Mips - 1) };

        // Each level runs separately to keep GPU work short.
        _context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.General,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                baseMip: (uint)mip, baseLayer: baseLayer, layerCount: 6);

            var boundSet = set;
            var push = constants;
            api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &boundSet, 0, null);
            api.CmdPushConstants(cmd, layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants), &push);
            uint groups = (size + 7) / 8;
            api.CmdDispatch(cmd, groups, groups, 6);

            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
                baseMip: (uint)mip, baseLayer: baseLayer, layerCount: 6);
        });
    }

}

/// <summary>
/// The six faces of one capture, copied out of the image the scene was drawn to. Reflection
/// probes blur it into their mips.
/// </summary>
internal sealed unsafe class CaptureCube : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocation _memory;

    public readonly uint FaceSize;
    public readonly Image Image;

    /// <summary>The faces as a cube, for filtered lookups by direction.</summary>
    public readonly ImageView CubeView;

    /// <summary>The faces as six layers, for reading single texels.</summary>
    public readonly ImageView ArrayView;

    public CaptureCube(VulkanContext context, uint faceSize)
    {
        _context = context;
        FaceSize = faceSize;
        var api = context.Api;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = GBuffer.HdrFormat,
            Extent = new Extent3D(faceSize, faceSize, 1),
            MipLevels = 1,
            ArrayLayers = 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(context.Device, &imageInfo, null, out Image).Check();
        api.GetImageMemoryRequirements(context.Device, Image, out var requirements);
        _memory = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, Image, _memory.Memory, _memory.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.TypeCube,
            Format = GBuffer.HdrFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 6),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out CubeView).Check();
        viewInfo.ViewType = ImageViewType.Type2DArray;
        api.CreateImageView(context.Device, &viewInfo, null, out ArrayView).Check();
    }

    /// <summary>
    /// Copies one finished face out of <paramref name="rendered"/>, which must be <see cref="FaceSize"/>
    /// across and ready to sample. It's left ready to copy from, and the face ready for compute to read.
    /// </summary>
    public void RecordFace(CommandBuffer cmd, Image rendered, int face)
    {
        var api = _context.Api;
        api.Transition(cmd, rendered, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        api.Transition(cmd, Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            baseLayer: (uint)face);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)face, 1),
            Extent = new Extent3D(FaceSize, FaceSize, 1),
        };
        api.CmdCopyImage(cmd, rendered, ImageLayout.TransferSrcOptimal, Image, ImageLayout.TransferDstOptimal, 1, &region);

        api.Transition(cmd, Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            baseLayer: (uint)face);
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyImageView(_context.Device, ArrayView, null);
        api.DestroyImageView(_context.Device, CubeView, null);
        api.DestroyImage(_context.Device, Image, null);
        _context.Memory.Free(_memory);
    }
}
