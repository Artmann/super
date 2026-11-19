using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The multisampled tile <see cref="TexelDraw"/> draws a page through without conservative rasterization, and the
/// resolve of each tile into the page, each texel taking a covered sample.
/// </summary>
internal sealed unsafe class TileResolve : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;
    private readonly Sampler _sampler;
    private TileTargets? _tile;

    public TileResolve(VulkanContext context)
    {
        _context = context;

        // The multisampled tile's three targets read and the page's three written.
        const DescriptorType tile = DescriptorType.CombinedImageSampler;
        const DescriptorType page = DescriptorType.StorageImage;
        _setLayout = context.CreateSetLayout(BakeGpu.Bindings(tile, tile, tile, page, page, page));
        _layout = context.CreatePipelineLayout(
            [_setLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ResolveConstants)));
        _pipeline = PipelineFactory.CreateCompute(context, "bake_resolve.comp", _layout);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        context.Api.CreateSampler(context.Device, &samplerInfo, null, out _sampler).Check();
    }

    /// <summary>
    /// Readies a page to be drawn tile by tile: makes the tile the first time, clears the page so tiles nothing is drawn
    /// in stay uncovered, and gives the resolve set into it, from <paramref name="pool"/>.
    /// </summary>
    public DescriptorSet Prepare(CommandBuffer cmd, PageTargets targets, DescriptorPool pool)
    {
        _tile ??= new TileTargets(_context, SampleCount());
        ClearToUncovered(cmd, targets);
        var set = _context.AllocateSet(pool, _setLayout);
        WriteSet(set, _tile, targets);
        return set;
    }

    /// <summary>Readies the tile's targets to be drawn into, once the last tile's resolve has read them, and gives them.</summary>
    public RenderTarget[] BeginTile(CommandBuffer cmd)
    {
        var api = _context.Api;
        var tile = _tile!;
        foreach (var target in tile.Colors)
        {
            api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.None),
                (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit));
        }

        return tile.Colors;
    }

    /// <summary>Resolves the drawn tile at <paramref name="origin"/> into the page through <paramref name="set"/>.</summary>
    public void ResolveTile(CommandBuffer cmd, Offset2D origin, DescriptorSet set)
    {
        var api = _context.Api;
        var tile = _tile!;
        foreach (var target in tile.Colors)
        {
            api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
        }

        var constants = new ResolveConstants { X = origin.X, Y = origin.Y, Samples = (int)tile.SampleCount };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ResolveConstants), &constants);
        const uint groups = BakeGpu.TileSize / BakeGpu.GroupSize;
        api.CmdDispatch(cmd, groups, groups, 1);
    }

    /// <summary>Lets the validity pass read the position and geometric normal the resolves wrote.</summary>
    public void Finish(CommandBuffer cmd, PageTargets targets)
    {
        foreach (var target in targets.Colors)
        {
            _context.Api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit));
        }
    }

    /// <summary>Eight samples, or the most below that the device can draw and sample.</summary>
    public SampleCountFlags SampleCount()
    {
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, out var properties);
        var supported = properties.Limits.FramebufferColorSampleCounts & properties.Limits.SampledImageColorSampleCounts;
        foreach (var count in (ReadOnlySpan<SampleCountFlags>)[SampleCountFlags.Count8Bit, SampleCountFlags.Count4Bit, SampleCountFlags.Count2Bit])
        {
            if ((supported & count) != 0)
            {
                return count;
            }
        }

        throw new InvalidOperationException("Baking without conservative rasterization needs multisampling, which this GPU lacks.");
    }

    /// <summary>Fills <paramref name="tileDraws"/> with the draws whose rectangle reaches into the tile at <paramref name="origin"/>.</summary>
    public static void CollectTouching(
        List<(DrawItem Draw, LightmapRect Rect)> draws, Offset2D origin, List<(DrawItem Draw, LightmapRect Rect)> tileDraws)
    {
        tileDraws.Clear();
        foreach (var entry in draws)
        {
            if (Touches(entry.Rect, origin))
            {
                tileDraws.Add(entry);
            }
        }
    }

    /// <summary>Whether a rectangle, with a texel round it for conservative overestimation, reaches into a tile.</summary>
    private static bool Touches(LightmapRect rect, Offset2D tile)
    {
        const int size = BakeGpu.TileSize;
        float left = rect.Offset.X * LightmapLayout.PageSize - 1;
        float top = rect.Offset.Y * LightmapLayout.PageSize - 1;
        float right = (rect.Offset.X + rect.Scale.X) * LightmapLayout.PageSize + 1;
        float bottom = (rect.Offset.Y + rect.Scale.Y) * LightmapLayout.PageSize + 1;
        return right > tile.X && left < tile.X + size && bottom > tile.Y && top < tile.Y + size;
    }

    // Clears a page's targets for the resolves to write, so tiles nothing is drawn in stay uncovered.
    private void ClearToUncovered(CommandBuffer cmd, PageTargets targets)
    {
        var api = _context.Api;
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal),
                (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.None),
                (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit));
            api.CmdClearColorImage(cmd, target.Image, ImageLayout.TransferDstOptimal, &clear, 1, &range);
            api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferDstOptimal, ImageLayout.General),
                (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));
        }
    }

    private void WriteSet(DescriptorSet set, TileTargets tile, PageTargets targets)
    {
        var images = stackalloc DescriptorImageInfo[6];
        var writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 3; i++)
        {
            images[i] = new DescriptorImageInfo(_sampler, tile.Colors[i].View, ImageLayout.ShaderReadOnlyOptimal);
            images[3 + i] = new DescriptorImageInfo(default, targets.Colors[i].View, ImageLayout.General);
        }

        for (int i = 0; i < 6; i++)
        {
            writes[i] = Descriptors.ImageWrite(
                set, (uint)i, i < 3 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage, &images[i]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    public void Dispose()
    {
        _tile?.Dispose();
        _pipeline.Destroy();
        var api = _context.Api;
        api.DestroyPipelineLayout(_context.Device, _layout, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        api.DestroySampler(_context.Device, _sampler, null);
    }

    /// <summary>The multisampled targets one tile of a page is drawn into.</summary>
    private sealed class TileTargets : IDisposable
    {
        public TileTargets(VulkanContext context, SampleCountFlags samples)
        {
            SampleCount = (uint)samples;
            var extent = new Extent2D(BakeGpu.TileSize, BakeGpu.TileSize);
            const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit;
            Colors =
            [
                new RenderTarget(context, BakeGpu.PositionFormat, extent, usage, ImageAspectFlags.ColorBit, samples: samples),
                new RenderTarget(context, BakeGpu.NormalFormat, extent, usage, ImageAspectFlags.ColorBit, samples: samples),
                new RenderTarget(context, BakeGpu.NormalFormat, extent, usage, ImageAspectFlags.ColorBit, samples: samples),
            ];
        }

        public uint SampleCount { get; }

        public RenderTarget[] Colors { get; }

        public void Dispose()
        {
            foreach (var target in Colors)
            {
                target.Dispose();
            }
        }
    }

    // Must match the push constants in bake_resolve.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct ResolveConstants
    {
        public int X;
        public int Y;
        public int Samples;
    }
}
