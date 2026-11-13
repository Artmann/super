using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The bake's filter and dilation on the GPU: an edge-aware à-trous filter over a traced region's irradiance and
/// direction, then rings of dilation into the gutters and invalid texels. Owns its pipelines and set layout; the
/// images a run reads and writes are the run's own (<see cref="FilterRun"/>), and the page's position, normal and
/// validity are the <see cref="Baker"/>'s. See "Filtering and dilation" in docs/design.md.
/// </summary>
internal sealed unsafe class BakeFilter : IDisposable
{
    /// <summary>À-trous passes over the irradiance and direction, texels 1, 2, 4 and 8 apart.</summary>
    internal const int Passes = 4;

    /// <summary>Rings of dilation after the filter, each filling empty texels beside full ones.</summary>
    internal const int DilationRings = 8;

    private const uint GroupSize = 8;

    // The page's position, geometric normal and validity, then a copy's irradiance, direction and mask read and the
    // other copy's three written.
    private const int Bindings = 9;

    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _filterPipeline;
    private readonly Pipeline _dilatePipeline;

    public BakeFilter(VulkanContext context)
    {
        _context = context;
        Span<DescriptorSetLayoutBinding> bindings = stackalloc DescriptorSetLayoutBinding[Bindings];
        for (uint i = 0; i < Bindings; i++)
        {
            bindings[(int)i] = new DescriptorSetLayoutBinding(i, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        }

        _setLayout = context.CreateSetLayout(bindings);
        Pool = context.CreateDescriptorPool(2, new DescriptorPoolSize(DescriptorType.StorageImage, 2 * Bindings));
        _layout = context.CreatePipelineLayout(
            [_setLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants)));
        _filterPipeline = PipelineFactory.CreateCompute(context, "bake_filter.comp", _layout);
        _dilatePipeline = PipelineFactory.CreateCompute(context, "bake_dilate.comp", _layout);
    }

    /// <summary>Room for one run's two sets, for a filter that waits for the GPU; reset it before each run.</summary>
    public DescriptorPool Pool { get; }

    /// <summary>
    /// Gives <paramref name="run"/> its two sets from <paramref name="pool"/>, one each way between its copies, both
    /// reading the page's <paramref name="position"/>, <paramref name="geometricNormal"/> and <paramref name="validity"/>.
    /// </summary>
    public void Bind(DescriptorPool pool, FilterRun run, ImageView position, ImageView geometricNormal, ImageView validity)
    {
        run.Forward = _context.AllocateSet(pool, _setLayout);
        run.Backward = _context.AllocateSet(pool, _setLayout);
        ReadOnlySpan<ImageView> page = [position, geometricNormal, validity];
        WriteSet(run.Forward, page, run.A, run.B);
        WriteSet(run.Backward, page, run.B, run.A);
    }

    /// <summary>
    /// Records a filter run over the region into one command buffer: uploads what <see cref="FilterRun.Fill"/> wrote,
    /// then with <paramref name="prepare"/> the prepare stage, with <paramref name="smooth"/> the à-trous passes, then
    /// the dilation rings, and copies the result back into the run's buffer for <see cref="FilterRun.Read"/>.
    /// </summary>
    public void Record(CommandBuffer cmd, FilterRun run, bool prepare, bool smooth)
    {
        RecordUpload(cmd, run);
        var constants = new FilterConstants { X = run.X, Y = run.Y, Width = run.Width, Height = run.Height };
        if (prepare)
        {
            RecordPass(cmd, _filterPipeline, run.Forward, constants with { Stage = 0 });
        }

        // Each pass reads one copy and writes the other; after the prepare the values are in b.
        bool inB = prepare;
        if (smooth)
        {
            for (int pass = 0; pass < Passes; pass++)
            {
                RecordPass(cmd, _filterPipeline, inB ? run.Backward : run.Forward, constants with { Stage = 1 << pass });
                inB = !inB;
            }
        }

        for (int ring = 0; ring < DilationRings; ring++)
        {
            RecordPass(cmd, _dilatePipeline, inB ? run.Backward : run.Forward, constants);
            inB = !inB;
        }

        // The last pass's barrier lets the copies read what it wrote; the upload's reads of the buffer are done by then.
        var result = inB ? run.B : run.A;
        for (int i = 0; i < 3; i++)
        {
            var region = Region(run, i);
            _context.Api.CmdCopyImageToBuffer(cmd, result[i].Image, ImageLayout.General, run.Staging.Handle, 1, &region);
        }
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _filterPipeline, null);
        api.DestroyPipeline(_context.Device, _dilatePipeline, null);
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorPool(_context.Device, Pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }

    // Uploads the run's buffer into a, and readies b to be written, or left alone with no prepare.
    private void RecordUpload(CommandBuffer cmd, FilterRun run)
    {
        var api = _context.Api;
        for (int i = 0; i < 3; i++)
        {
            var a = run.A[i].Image;
            api.Transition(cmd, a, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit);
            var region = Region(run, i);
            api.CmdCopyBufferToImage(cmd, run.Staging.Handle, a, ImageLayout.General, 1, &region);
            api.Transition(cmd, a, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            api.Transition(cmd, run.B[i].Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }
    }

    // The whole of one of the run's images, at its place in the run's buffer: irradiance, direction then mask.
    private static BufferImageCopy Region(FilterRun run, int image) => new()
    {
        BufferOffset = (ulong)run.Width * (ulong)run.Height * 16 * (ulong)image,
        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        ImageExtent = new Extent3D((uint)run.Width, (uint)run.Height, 1),
    };

    /// <summary>Records one filter or dilation pass over the region, then a barrier so the next pass reads what it wrote.</summary>
    private void RecordPass(CommandBuffer cmd, Pipeline pipeline, DescriptorSet set, FilterConstants constants)
    {
        var api = _context.Api;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants), &constants);
        api.CmdDispatch(cmd, ((uint)constants.Width + GroupSize - 1) / GroupSize, ((uint)constants.Height + GroupSize - 1) / GroupSize, 1);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void WriteSet(DescriptorSet set, ReadOnlySpan<ImageView> page, RenderTarget[] from, RenderTarget[] to)
    {
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, page[0], ImageLayout.General),
            new(default, page[1], ImageLayout.General),
            new(default, page[2], ImageLayout.General),
            new(default, from[0].View, ImageLayout.General),
            new(default, from[1].View, ImageLayout.General),
            new(default, from[2].View, ImageLayout.General),
            new(default, to[0].View, ImageLayout.General),
            new(default, to[1].View, ImageLayout.General),
            new(default, to[2].View, ImageLayout.General),
        };
        var writes = stackalloc WriteDescriptorSet[Bindings];
        for (int i = 0; i < Bindings; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, Bindings, writes, 0, null);
    }

    // Must match the push constants in bake_filter.comp and bake_dilate.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterConstants
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Stage;
    }
}

/// <summary>
/// One filter run over a page region: two copies each of its irradiance, direction and mask, the sets that read one
/// copy and write the other, and the host-visible buffer the run uploads from and reads back into. See
/// <see cref="Baker.CreateFilter(DescriptorPool, int, int, int, int, int)"/> and <see cref="BakeFilter.Record"/>.
/// </summary>
internal sealed class FilterRun : IDisposable
{
    private const Format SumFormat = Format.R32G32B32A32Sfloat;

    private readonly int _count;

    /// <summary>A run over <paramref name="region"/> of <paramref name="page"/>, in texels.</summary>
    public FilterRun(VulkanContext context, int page, (int X, int Y, int Width, int Height) region)
    {
        Page = page;
        (X, Y, Width, Height) = region;
        _count = Width * Height;
        var extent = new Extent2D((uint)Width, (uint)Height);
        const ImageUsageFlags usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        A = new RenderTarget[3];
        B = new RenderTarget[3];
        for (int i = 0; i < 3; i++)
        {
            A[i] = new RenderTarget(context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
            B[i] = new RenderTarget(context, SumFormat, extent, usage, ImageAspectFlags.ColorBit);
        }

        ulong bytes = (ulong)_count * 16;
        Staging = GpuBuffer.CreateHostVisible(
            context, bytes * 3, BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit);
    }

    public int Page { get; }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The irradiance, direction and mask the run uploads into.</summary>
    public RenderTarget[] A { get; }

    /// <summary>The other copy, which each pass writes when it reads <see cref="A"/>.</summary>
    public RenderTarget[] B { get; }

    /// <summary>Irradiance, direction then mask, four floats a texel each: uploaded from, then read back into.</summary>
    public GpuBuffer Staging { get; }

    /// <summary>Reads <see cref="A"/> and writes <see cref="B"/>.</summary>
    public DescriptorSet Forward { get; set; }

    /// <summary>Reads <see cref="B"/> and writes <see cref="A"/>.</summary>
    public DescriptorSet Backward { get; set; }

    /// <summary>Writes what the run uploads, four floats a texel of each.</summary>
    public unsafe void Fill(float[] irradiance, float[] direction, float[] mask)
    {
        int floats = _count * 4;
        var staged = new Span<float>(Staging.Mapped, floats * 3);
        irradiance.AsSpan(0, floats).CopyTo(staged);
        direction.AsSpan(0, floats).CopyTo(staged[floats..]);
        mask.AsSpan(0, floats).CopyTo(staged[(floats * 2)..]);
    }

    /// <summary>What the run read back, once the GPU is done with it.</summary>
    public unsafe (float[] Irradiance, float[] Direction, float[] Mask) Read()
    {
        int floats = _count * 4;
        var staged = new ReadOnlySpan<float>(Staging.Mapped, floats * 3);
        return (staged[..floats].ToArray(), staged.Slice(floats, floats).ToArray(), staged[(floats * 2)..].ToArray());
    }

    public void Dispose()
    {
        foreach (var target in A)
        {
            target.Dispose();
        }

        foreach (var target in B)
        {
            target.Dispose();
        }

        Staging.Dispose();
    }
}
