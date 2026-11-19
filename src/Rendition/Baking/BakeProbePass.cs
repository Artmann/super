using System.Runtime.InteropServices;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The <see cref="Baker"/>'s path tracing of a batch of a space's probes, <see cref="BakeGpu.ProbesPerBatch"/> side by
/// side, each a row of its six faces, and the copy of their faces into the probe volume to fold.
/// </summary>
internal sealed unsafe class BakeProbePass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly RayTracing _tracing;
    private readonly BakeComputePass _pass;

    public BakeProbePass(VulkanContext context, SceneStores stores, RayTracing tracing)
    {
        _context = context;
        _tracing = tracing;

        // The scene and geometry table, the lights, the probes' positions, the sum read and written, the faces written,
        // and the sky. Set 1 is the resource table. Each job writes one set from its own pool.
        var bindings = BakeGpu.Bindings(
            DescriptorType.AccelerationStructureKhr, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageImage, DescriptorType.StorageImage,
            DescriptorType.CombinedImageSampler);
        _pass = new BakeComputePass(context, stores, bindings, (uint)sizeof(ProbeTraceConstants), "bake_probes.comp");
    }

    /// <summary>The size of a batch of probes' faces.</summary>
    public static Extent2D Extent => new(6 * ProbeVolume.FaceSize, ProbeVolume.FaceSize * BakeGpu.ProbesPerBatch);

    /// <summary>The probe volume <see cref="EnsureVolume"/> made, or null before it has run.</summary>
    public ProbeVolume? Volume { get; private set; }

    /// <summary>
    /// The baker's probe volume, made the first time at the lowest quality, for its faces, its fold and its staging
    /// slots. Its atlases stay unused, since a bake keeps the fixed layout of <see cref="BakedProbeAtlas"/>, and the
    /// frame's own probes are left alone. Its size is the same for every space, since a bake folds its probes a few at a
    /// time through the volume's slots, so one made once serves every bake. Waits for the GPU the first time only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GPU lacks what the live probes fold with.</exception>
    public ProbeVolume EnsureVolume(ImageView sky, Sampler skySampler)
    {
        if (Volume is { } made)
        {
            return made;
        }

        var volume = new ProbeVolume(_context, skySampler, sky);
        if (!volume.Supported)
        {
            volume.Dispose();
            throw new InvalidOperationException("Baking light probes needs the storage image formats and multiview the live probes fold with.");
        }

        volume.EnsureStorage(ProbeQuality.Low);
        Volume = volume;
        return volume;
    }

    /// <summary>
    /// The probes' sum and their faces, <see cref="BakeGpu.ProbesPerBatch"/> rows of six
    /// <see cref="ProbeVolume.FaceSize"/> faces each.
    /// </summary>
    public (RenderTarget Sum, RenderTarget Faces) CreateTargets()
    {
        var extent = Extent;
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        return (
            new RenderTarget(_context, BakeGpu.SumFormat, extent, usage, ImageAspectFlags.ColorBit),
            new RenderTarget(_context, GBuffer.HdrFormat, extent, usage, ImageAspectFlags.ColorBit));
    }

    /// <summary>The probe set from <paramref name="pool"/>, written for the scene, lights, probes, targets and sky.</summary>
    public DescriptorSet CreateSet(
        DescriptorPool pool, in TraceInputs inputs, GpuBuffer positions, (RenderTarget Sum, RenderTarget Faces) targets)
    {
        var set = _pass.AllocateSet(pool);
        WriteSet(set, inputs, positions, targets);
        return set;
    }

    /// <summary>Records <paramref name="count"/> probe passes from <paramref name="first"/>, each waiting for the last's sum.</summary>
    public void RecordPasses(CommandBuffer cmd, DescriptorSet set, ProbeTraceConstants constants, int first, int count)
    {
        _tracing.ReadyToTrace(cmd);
        var extent = Extent;
        uint groupsX = (extent.Width + BakeGpu.GroupSize - 1) / BakeGpu.GroupSize;
        uint groupsY = (extent.Height + BakeGpu.GroupSize - 1) / BakeGpu.GroupSize;
        _pass.RecordPasses(cmd, set, constants, (first, count), (groupsX, groupsY));
    }

    /// <summary>
    /// Copies the six faces of <paramref name="count"/> of the batch's probe rows from <paramref name="firstRow"/> into
    /// the volume's faces, one slot each, ready for the fold.
    /// </summary>
    public void RecordCopyFaces(CommandBuffer cmd, RenderTarget faces, int firstRow, int count)
    {
        var api = _context.Api;
        var volume = Volume!;
        uint layers = (uint)(count * 6);
        api.Transition(cmd, volume.Faces!.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            layerCount: layers);
        const uint size = ProbeVolume.FaceSize;
        var regions = stackalloc ImageCopy[6];
        for (int slot = 0; slot < count; slot++)
        {
            for (int f = 0; f < 6; f++)
            {
                regions[f] = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffset = new Offset3D(f * (int)size, (firstRow + slot) * (int)size, 0),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)(slot * 6 + f), 1),
                    Extent = new Extent3D(size, size, 1),
                };
            }

            api.CmdCopyImage(cmd, faces.Image, ImageLayout.General, volume.Faces.Image, ImageLayout.TransferDstOptimal, 6, regions);
        }

        api.Transition(cmd, volume.Faces.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            layerCount: layers);
    }

    private void WriteSet(DescriptorSet set, in TraceInputs inputs, GpuBuffer positions, (RenderTarget Sum, RenderTarget Faces) targets)
    {
        var handle = inputs.Scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var buffers = stackalloc DescriptorBufferInfo[]
        {
            new(inputs.Scene.GeometryTable.Handle, 0, Vk.WholeSize),
            new(inputs.Lights.Handle, 0, Vk.WholeSize),
            new(positions.Handle, 0, Vk.WholeSize),
        };
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, targets.Sum.View, ImageLayout.General),
            new(default, targets.Faces.View, ImageLayout.General),
            new(inputs.SkySampler, inputs.Sky, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = Descriptors.StructureWrite(set, 0, &structureInfo);

        // Bindings 1 to 3 are buffers, 4 and 5 storage images, and 6 the sky.
        for (uint binding = 1; binding < 7; binding++)
        {
            writes[binding] = binding <= 3
                ? Descriptors.BufferWrite(set, binding, &buffers[binding - 1])
                : Descriptors.ImageWrite(
                    set, binding, binding == 6 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
                    &images[binding - 4]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 7, writes, 0, null);
    }

    public void Dispose()
    {
        _pass.Dispose();
        Volume?.Dispose();
    }
}

// Must match the push constants in bake_probes.comp.
[StructLayout(LayoutKind.Sequential)]
internal struct ProbeTraceConstants : IBakePassConstants
{
    public uint FirstProbe;
    public uint ProbeCount;
    public uint Pass;
    public uint Passes;
    public uint LightCount;
    public float Lift;
    public float SkyIntensity;
    public float MaxDistance;

    uint IBakePassConstants.Pass
    {
        set => Pass = value;
    }
}
