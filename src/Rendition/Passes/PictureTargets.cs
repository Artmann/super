using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The picture after anti-aliasing: the resolved image at <see cref="Display"/> and last frame's, then the tonemapped
/// one and a held earlier frame for a transition, with what each holds so far. New targets start empty.
/// </summary>
internal sealed class PictureTargets : IDisposable
{
    /// <summary>The lit picture's format, which <see cref="GBuffer.HdrFormat"/> is too.</summary>
    public const Format HdrFormat = Format.R16G16B16A16Sfloat;

    /// <summary>The tonemapped picture's format.</summary>
    public const Format LdrFormat = Format.R8G8B8A8Srgb;

    public PictureTargets(VulkanContext context, Extent2D extent, Extent2D display)
    {
        Display = display;
        Resolved = new RenderTarget(
            context, HdrFormat, display, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.StorageBit,
            ImageAspectFlags.ColorBit);
        History = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        Ldr = new RenderTarget(
            context, LdrFormat, display, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        Held = new RenderTarget(context, LdrFormat, display, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
    }

    /// <summary>
    /// The size of the picture after anti-aliasing: <see cref="Resolved"/>, <see cref="Ldr"/> and <see cref="Held"/>.
    /// The scene's own size, unless FSR upscales it to the window's.
    /// </summary>
    public Extent2D Display { get; }

    /// <summary>The lit image with anti-aliasing applied, at <see cref="Display"/>. TAA draws it and FSR writes it from compute.</summary>
    public RenderTarget Resolved { get; }

    /// <summary>Last frame's Resolved, for anti-aliasing and reflections, at the scene's size.</summary>
    public RenderTarget History { get; }

    public RenderTarget Ldr { get; }

    /// <summary>An earlier frame's Ldr, kept for a dissolve or wipe to show over the new one.</summary>
    public RenderTarget Held { get; }

    /// <summary>Whether <see cref="History"/> holds the last frame. Until then the lighting binds it empty and reflections trace nothing.</summary>
    public bool HistoryReady { get; set; }

    /// <summary>Whether <see cref="Held"/> holds a picture for the transition to show.</summary>
    public bool HeldReady { get; set; }

    /// <summary>Whether <see cref="Ldr"/> holds a finished frame, which a new hold copies.</summary>
    public bool LdrReady { get; set; }

    public void Dispose()
    {
        Resolved.Dispose();
        History.Dispose();
        Ldr.Dispose();
        Held.Dispose();
    }
}
