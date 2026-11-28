using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The planar reflection's picture: two layers of one HDR array, the lit colour and how high each surface stands over
/// the plane, with up to <see cref="MaxMips"/> mips, and a depth buffer. See <see cref="PlanarStage"/>.
/// </summary>
internal sealed unsafe class MirrorTargets : IDisposable
{
    /// <summary>How many mips at most the picture is halved into: enough blur for a rough puddle.</summary>
    public const uint MaxMips = 7;

    private const uint Layers = 2;

    private readonly VulkanContext _context;
    private readonly RenderTarget _color;
    private readonly RenderTarget _depth;

    // Whether the picture has been laid out for sampling since it was made.
    private bool _laidOut;

    /// <summary>Makes both layers with their mips, and the depth, at this extent, not laid out yet.</summary>
    public MirrorTargets(VulkanContext context, Extent2D extent)
    {
        _context = context;
        Extent = extent;
        uint mips = Math.Min(MaxMips, (uint)Math.Log2(Math.Max(extent.Width, extent.Height)) + 1);
        _color = new RenderTarget(context, GBuffer.HdrFormat, extent,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
            layers: Layers, mips: mips);
        _depth = new RenderTarget(context, GBuffer.DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
    }

    public Extent2D Extent { get; }

    /// <summary>Both layers with their mips, as the lighting pass samples them.</summary>
    public ImageView View => _color.View;

    /// <summary>The picture's mip count.</summary>
    public uint Mips => _color.Mips;

    /// <summary>Lays the picture out for sampling, once, for a frame that draws nothing into it.</summary>
    public void LayOutUnused(CommandBuffer cmd)
    {
        if (_laidOut)
        {
            return;
        }

        _context.Api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined,
            ImageLayout.ShaderReadOnlyOptimal) { MipCount = _color.Mips, LayerCount = Layers },
            (SampledStage, SampledAccess), (SampledStage, SampledAccess));
        _laidOut = true;
    }

    /// <summary>The picture, whose two layers <see cref="MirrorPipeline"/> draws into through their own views.</summary>
    public RenderTarget Color => _color;

    /// <summary>The depth the mirror tests against while it draws.</summary>
    public RenderTarget Depth => _depth;

    /// <summary>
    /// Moves both layers' first mip and the depth to drawing. They stay with this class rather than the GPU layer,
    /// because two views of one image are drawn at once and the picture goes on to be halved into its mips.
    /// </summary>
    public void Ready(CommandBuffer cmd)
    {
        var api = _context.Api;
        api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal) { LayerCount = Layers }, (SampledStage, SampledAccess), (ColorStage, ColorWrite));
        api.Transition(cmd, new ImageTransition(_depth.Image, ImageAspectFlags.DepthBit, ImageLayout.Undefined,
            ImageLayout.DepthAttachmentOptimal), (DepthStages, DepthWrite), (DepthStages, DepthWrite | AccessFlags2.DepthStencilAttachmentReadBit));
    }

    /// <summary>Halves both layers down their mips once drawing has ended, leaving the picture ready to sample.</summary>
    public void Finish(CommandBuffer cmd)
    {
        var api = _context.Api;
        api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit, ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferDstOptimal) { LayerCount = Layers }, (ColorStage, ColorWrite), (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit));
        if (_color.Mips > 1)
        {
            api.Transition(cmd, new ImageTransition(_color.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal) { BaseMip = 1, MipCount = _color.Mips - 1, LayerCount = Layers },
                (SampledStage, SampledAccess), (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit));
        }

        MipBlits.Fill(api, cmd, _color, Extent, Layers);
        _laidOut = true;
    }

    public void Dispose()
    {
        _color.Dispose();
        _depth.Dispose();
    }
}
