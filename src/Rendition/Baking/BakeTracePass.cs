using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// What the trace and probe passes trace against: the scene's top-level structure, the bake's lights, and the sky.
/// </summary>
internal readonly record struct TraceInputs(TopLevelStructure Scene, GpuBuffer Lights, ImageView Sky, Sampler SkySampler);

/// <summary>
/// The <see cref="Baker"/>'s path tracing of a page region's texels, a few passes at a time into its sums: makes and
/// clears the sums, and copies them back with the texels' positions and validity. Builds the scenes a bake traces too.
/// </summary>
internal sealed unsafe class BakeTracePass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly RayTracing _tracing;
    private readonly TraceScene _traceScene;
    private readonly BakeComputePass _pass;

    public BakeTracePass(VulkanContext context, SceneStores stores, RayTracing tracing)
    {
        _context = context;
        _tracing = tracing;
        _traceScene = new TraceScene(stores.TraceFlagsOf);

        // The scene and geometry table, the page's position, geometric normal and validity read, the lights, the three
        // sums read and written, the sky, and a chunk's five transfer sums. Set 1 is the resource table again. Each job
        // writes a set a page from its own pool.
        const DescriptorType image = DescriptorType.StorageImage;
        var bindings = BakeGpu.Bindings(
            DescriptorType.AccelerationStructureKhr, DescriptorType.StorageBuffer, image, image, image,
            DescriptorType.StorageBuffer, image, image, image, DescriptorType.CombinedImageSampler,
            image, image, image, image, image);
        _pass = new BakeComputePass(context, stores, bindings, (uint)sizeof(BakeTraceConstants), "bake_trace.comp");
    }

    /// <summary>Records a build of the frame's top-level structure over <paramref name="draws"/>.</summary>
    public TopLevelStructure BuildScene(CommandBuffer cmd, ReadOnlySpan<DrawItem> draws) =>
        _tracing.TopLevel(cmd, _traceScene.Collect(draws));

    /// <summary>
    /// Records a build of the top-level structure over <paramref name="draws"/> that is held until
    /// <paramref name="release"/> is disposed, so a bake's later steps, in later command buffers, can trace it.
    /// </summary>
    public TopLevelStructure BuildHeldScene(CommandBuffer cmd, ReadOnlySpan<DrawItem> draws, out IDisposable release) =>
        _tracing.TopLevel(cmd, _traceScene.Collect(draws), out release);

    /// <summary>
    /// A region's sums, <see cref="BakeGpu.SumFormat"/> and cleared by <see cref="ClearSums"/>: irradiance, direction
    /// and mask, then the five transfer sums, of the region's size for a chunk and 1 x 1, never written, for a space.
    /// </summary>
    public RenderTarget[] CreateSums(int width, int height, bool transfer)
    {
        var extent = new Extent2D((uint)width, (uint)height);
        var transferExtent = transfer ? extent : new Extent2D(1, 1);
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        var sums = new RenderTarget[3 + BakeGpu.TransferSums];
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i] = new RenderTarget(_context, BakeGpu.SumFormat, i < 3 ? extent : transferExtent, usage, ImageAspectFlags.ColorBit);
        }

        return sums;
    }

    /// <summary>Clears images to zero and readies them for compute shaders to read and write.</summary>
    public void ClearSums(CommandBuffer cmd, RenderTarget[] sums)
    {
        var api = _context.Api;
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        foreach (var sum in sums)
        {
            api.Transition(cmd, sum.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
            api.CmdClearColorImage(cmd, sum.Image, ImageLayout.General, &clear, 1, &range);
            api.Transition(cmd, sum.Image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }
    }

    /// <summary>A trace set from <paramref name="pool"/> for one page, written for the scene, lights, sums and sky.</summary>
    public DescriptorSet CreateSet(DescriptorPool pool, PageTargets page, in TraceInputs inputs, RenderTarget[] sums)
    {
        var set = _pass.AllocateSet(pool);
        WriteSet(set, page, inputs, sums);
        return set;
    }

    /// <summary>
    /// Records <paramref name="count"/> passes from <paramref name="first"/> over the region in
    /// <paramref name="constants"/>, each waiting for the last's sums.
    /// </summary>
    public void RecordPasses(CommandBuffer cmd, DescriptorSet set, BakeTraceConstants constants, int first, int count)
    {
        _tracing.ReadyToTrace(cmd);
        uint groupsX = ((uint)constants.Width + BakeGpu.GroupSize - 1) / BakeGpu.GroupSize;
        uint groupsY = ((uint)constants.Height + BakeGpu.GroupSize - 1) / BakeGpu.GroupSize;
        _pass.RecordPasses(cmd, set, constants, (first, count), (groupsX, groupsY));
    }

    /// <summary>
    /// Records copies of a page region's sums, and its texels' positions and validity from <paramref name="page"/>, into
    /// <paramref name="buffer"/>, of <see cref="SumsReadBack.Bytes"/> at least. Read it with
    /// <see cref="SumsReadBack.Read"/> once the GPU is done.
    /// </summary>
    public void RecordReadBack(CommandBuffer cmd, RenderTarget[] sums, GpuBuffer buffer, in SumsReadBack readBack, PageTargets page)
    {
        var api = _context.Api;
        api.MemoryBarrier(cmd, (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));

        var region = readBack.Region;
        ulong bytes = (ulong)region.Width * (ulong)region.Height * 16;
        for (int i = 0; i < readBack.SumsRead; i++)
        {
            BakeGpu.Copy(api, cmd, sums[i], buffer, BakeGpu.Region(bytes * (ulong)i, (0, 0, region.Width, region.Height)));
        }

        BakeGpu.Copy(api, cmd, page.Position, buffer, BakeGpu.Region(readBack.PositionAt, region));
        BakeGpu.Copy(api, cmd, page.Validity, buffer, BakeGpu.Region(readBack.ValidityAt, region));
    }

    private void WriteSet(DescriptorSet set, PageTargets targets, in TraceInputs inputs, RenderTarget[] sums)
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
        };

        var images = stackalloc DescriptorImageInfo[12];
        FillImages(images, targets, inputs, sums);
        var writes = stackalloc WriteDescriptorSet[15];
        writes[0] = Descriptors.StructureWrite(set, 0, &structureInfo);

        // Bindings 1 and 5 are buffers, 2 to 4 and 6 to 8 storage images, 9 the sky, and 10 to 14 the transfer sums.
        ReadOnlySpan<int> imageOf = [-1, -1, 0, 1, 2, -1, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        for (uint binding = 1; binding < 15; binding++)
        {
            writes[binding] = binding is 1 or 5
                ? Descriptors.BufferWrite(set, binding, &buffers[binding == 1 ? 0 : 1])
                : Descriptors.ImageWrite(
                    set, binding, binding == 9 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
                    &images[imageOf[(int)binding]]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 15, writes, 0, null);
    }

    // The set's twelve images: the page's position, geometric normal and validity, the three sums, the sky, and the
    // five transfer sums.
    private static void FillImages(DescriptorImageInfo* images, PageTargets targets, in TraceInputs inputs, RenderTarget[] sums)
    {
        RenderTarget[] storage = [targets.Position, targets.GeometricNormal, targets.Validity, sums[0], sums[1], sums[2]];
        for (int i = 0; i < storage.Length; i++)
        {
            images[i] = new DescriptorImageInfo(default, storage[i].View, ImageLayout.General);
        }

        images[6] = new DescriptorImageInfo(inputs.SkySampler, inputs.Sky, ImageLayout.ShaderReadOnlyOptimal);
        for (int i = 0; i < BakeGpu.TransferSums; i++)
        {
            images[7 + i] = new DescriptorImageInfo(default, sums[3 + i].View, ImageLayout.General);
        }
    }

    public void Dispose() => _pass.Dispose();
}

/// <summary>
/// Where a page region's sums readback puts what it copies: the sums, the transfer's only for a chunk, then the texels'
/// positions and validity. <see cref="Read"/> turns it into a <see cref="TracedPage"/>.
/// </summary>
internal readonly unsafe struct SumsReadBack
{
    public SumsReadBack(int page, (int X, int Y, int Width, int Height) region, bool transfer)
    {
        Page = page;
        Region = region;
        Transfer = transfer;
        ulong count = (ulong)region.Width * (ulong)region.Height;
        PositionAt = BakeGpu.Align(count * 16 * (ulong)SumsRead);
        ValidityAt = BakeGpu.Align(PositionAt + count * 16);
    }

    public int Page { get; }

    public (int X, int Y, int Width, int Height) Region { get; }

    /// <summary>Whether a chunk's transfer sums are read too.</summary>
    public bool Transfer { get; }

    /// <summary>The sums read: the transfer's only for a chunk.</summary>
    public int SumsRead => Transfer ? 3 + BakeGpu.TransferSums : 3;

    public ulong PositionAt { get; }

    public ulong ValidityAt { get; }

    /// <summary>The bytes the readback takes.</summary>
    public ulong Bytes => ValidityAt + (ulong)Region.Width * (ulong)Region.Height;

    /// <summary>
    /// The region as <see cref="BakeTracePass.RecordReadBack"/> copied it into <paramref name="mapped"/>: the sums
    /// divided by the passes, and which texels are covered and valid.
    /// </summary>
    public TracedPage Read(byte* mapped, int passes)
    {
        int count = Region.Width * Region.Height;
        var floats = new ReadOnlySpan<float>(mapped, count * 4 * SumsRead);
        float scale = 1f / passes;
        var (irradiance, direction, mask) = MeanLight(floats, count, scale);

        // Covered where the position's w names a rectangle, valid where the validity pass wrote 1.
        var positions = (float*)(mapped + PositionAt);
        var validity = mapped + ValidityAt;
        var valid = new bool[count];
        var covered = new bool[count];
        for (int i = 0; i < count; i++)
        {
            covered[i] = positions[i * 4 + 3] > 0.5f;
            valid[i] = validity[i] / 255f > 0.5f;
        }

        var (x, y, width, height) = Region;
        return new TracedPage(Page, x, y, width, height, irradiance, direction, mask, covered, valid, valid)
        {
            Transfer = Transfer ? TransferMeans(floats, count, scale) : null,
        };
    }

    // The irradiance and mask means, and the direction sums, of count texels.
    private static (float[] Irradiance, float[] Direction, float[] Mask) MeanLight(ReadOnlySpan<float> floats, int count, float scale)
    {
        var irradiance = new float[count * 4];
        var direction = new float[count * 4];
        var mask = new float[count * 4];
        for (int i = 0; i < count * 4; i++)
        {
            irradiance[i] = floats[i] * scale;
            direction[i] = floats[count * 4 + i]; // Only its own direction matters, not its scale.
            mask[i] = floats[count * 8 + i] * scale;
        }

        // Irradiance w held the mean squared luminance; it becomes the variance of the mean luminance, which tells the
        // filter how much of the difference between two texels is noise.
        for (int i = 0; i < count; i++)
        {
            float mean = BakeEncoding.Luminance(irradiance[i * 4], irradiance[i * 4 + 1], irradiance[i * 4 + 2]);
            irradiance[i * 4 + 3] = MathF.Max(irradiance[i * 4 + 3] - mean * mean, 0f) * scale;
        }

        return (irradiance, direction, mask);
    }

    // The transfer means, interleaved a texel at a time, each sum's four floats in turn, as TracedPage.Transfer holds
    // them.
    private static float[] TransferMeans(ReadOnlySpan<float> floats, int count, float scale)
    {
        const int stride = BakeGpu.TransferFloats;
        var means = new float[count * stride];
        for (int t = 0; t < count; t++)
        {
            for (int s = 0; s < BakeGpu.TransferSums; s++)
            {
                int from = (3 + s) * count * 4 + t * 4;
                for (int c = 0; c < 4; c++)
                {
                    means[t * stride + s * 4 + c] = floats[from + c] * scale;
                }
            }
        }

        return means;
    }
}

// Must match the push constants in bake_trace.comp.
[StructLayout(LayoutKind.Sequential)]
internal struct BakeTraceConstants : IBakePassConstants
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public uint Pass;
    public uint LightCount;
    public float Lift;
    public float SkyIntensity;
    public uint Chunk;

    uint IBakePassConstants.Pass
    {
        set => Pass = value;
    }
}
