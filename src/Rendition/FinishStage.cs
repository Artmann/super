using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// Draws the picture into the swapchain image at the window's size, with the transition and the overlay over it, and
/// copies the image out when the frame is a capture.
/// </summary>
internal sealed unsafe class FinishStage
{
    private readonly VulkanContext _context;
    private readonly DescriptorKit _kit;
    private readonly FrameRing _ring;
    private readonly GpuTimer _timer;
    private readonly FullscreenPass _finishPass;
    private readonly DescriptorSet _finishSet;
    private readonly OverlayPass _overlayPass;
    private readonly DescriptorSet _overlaySet;
    private GpuTexture? _overlayAtlas;

    public FinishStage(VulkanContext context, DescriptorKit kit, FrameRing ring, GpuTimer timer, (Format Format, ResourceTable Resources) window)
    {
        _context = context;
        _kit = kit;
        _ring = ring;
        _timer = timer;
        _finishSet = kit.NewSet(kit.TwoTextureLayout);
        _overlaySet = kit.NewSet(kit.OneTextureLayout);
        _finishPass = new FullscreenPass(context, "finish.frag", window.Format, [kit.FrameSetLayout, kit.TwoTextureLayout], TransitionConstants.Size);
        _overlayPass = new OverlayPass(context, window.Format, kit.OneTextureLayout, window.Resources, FrameRing.Count);
    }

    /// <summary>Where the frame being drawn copies its picture for a capture, or null when it isn't one.</summary>
    public GpuBuffer? Capture { get; set; }

    /// <summary>Whether a frame copied its picture into <see cref="Capture"/>.</summary>
    public bool CaptureRecorded { get; set; }

    /// <summary>Uploads the frame's overlay quads, and returns how many it draws: none before an atlas is set.</summary>
    public int UploadOverlay(ReadOnlySpan<OverlayQuad> quads) => _overlayAtlas is null ? 0 : _overlayPass.Upload(_ring.Index, quads);

    /// <summary>
    /// Replaces the image that the overlay's quads read from. Only its alpha is used. Waits for the GPU, so it isn't
    /// for every frame.
    /// </summary>
    public void SetOverlayAtlas(TextureData data)
    {
        _context.WaitIdle();
        _overlayAtlas?.Dispose();
        _overlayAtlas = new GpuTexture(_context, data);
        ImageSets.Write(_context, _overlaySet, [new(_kit.LinearSampler, _overlayAtlas.View, ImageLayout.ShaderReadOnlyOptimal)]);
    }

    /// <summary>Points the finish pass at the targets' finished picture and the held one.</summary>
    public void WriteSet(GBuffer targets)
    {
        ImageSets.Write(_context, _finishSet,
        [
            new(_kit.LinearSampler, targets.Picture.Ldr.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_kit.LinearSampler, targets.Picture.Held.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
    }

    /// <summary>
    /// Draws the picture into the swapchain image at the window's size, with the transition and the overlay over it,
    /// and leaves the image to present, or copies it out for a capture.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, (Swapchain Swapchain, uint Image) window,
        (GBuffer Targets, TransitionConstants Transition) picture, int overlayQuads)
    {
        var api = _context.Api;
        var swapchain = window.Swapchain;
        uint imageIndex = window.Image;
        var extent = picture.Targets.Picture.Display;
        var swapchainImage = swapchain.Images[imageIndex];
        api.Transition(cmd, new ImageTransition(swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal), (ColorStage, AccessFlags2.None), (ColorStage, ColorWrite));

        // From here on the picture is the window's size, which the scene may not be.
        var size = swapchain.Extent;
        ReadOnlySpan<DescriptorSet> finishSets = [frameSet, _finishSet];
        var transition = picture.Transition;
        transition.Style.Z = extent.Width != size.Width || extent.Height != size.Height ? 1f : 0f;
        _finishPass.Record(cmd, swapchain.Views[imageIndex], size, finishSets,
            new ReadOnlySpan<byte>(&transition, (int)TransitionConstants.Size));
        picture.Targets.Picture.LdrReady = true;
        _timer.Mark(cmd);

        if (overlayQuads > 0)
        {
            _overlayPass.Record(cmd, (swapchain.Views[imageIndex], size), _overlaySet, _ring.Index, overlayQuads);
        }

        _timer.Mark(cmd);

        if (Capture is not null)
        {
            RecordCapture(cmd, swapchainImage, size, Capture);
            return;
        }

        api.Transition(cmd, new ImageTransition(swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr), (ColorStage, ColorWrite),
            (PipelineStageFlags2.None, AccessFlags2.None));
    }

    public void Dispose()
    {
        _overlayPass.Dispose();
        _overlayAtlas?.Dispose();
        _finishPass.Dispose();
    }

    /// <summary>Copies the finished frame into a buffer the CPU can read, then readies it for display.</summary>
    private void RecordCapture(CommandBuffer cmd, Image swapchainImage, Extent2D extent, GpuBuffer capture)
    {
        var api = _context.Api;
        api.Transition(cmd, new ImageTransition(swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
        };
        api.CmdCopyImageToBuffer(cmd, swapchainImage, ImageLayout.TransferSrcOptimal, capture.Handle, 1, &region);

        var hostBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &hostBarrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);

        api.Transition(cmd, new ImageTransition(swapchainImage, ImageAspectFlags.ColorBit,
            ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr),
            (PipelineStageFlags2.CopyBit, AccessFlags2.None), (PipelineStageFlags2.None, AccessFlags2.None));
        CaptureRecorded = true;
    }
}
