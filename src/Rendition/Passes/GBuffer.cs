using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>The screen-sized images the passes hand to each other. Layout is in docs/design.md.</summary>
internal sealed class GBuffer : IDisposable
{
    public const Format BaseColorFormat = Format.R8G8B8A8Srgb;
    public const Format NormalFormat = Format.R16G16B16A16Sfloat;
    public const Format EmissiveFormat = Format.R16G16B16A16Sfloat;
    public const Format DepthFormat = Format.D32Sfloat;
    public const Format HdrFormat = Format.R16G16B16A16Sfloat;
    public const Format AoFormat = Format.R8G8B8A8Unorm;
    public const Format LdrFormat = Format.R8G8B8A8Srgb;
    public const Format MotionFormat = Format.R16G16Sfloat;

    /// <summary>Mip levels of <see cref="SceneColor"/>, each half the last, for frosted glass to blur by.</summary>
    public const uint SceneColorMips = 7;

    public readonly Extent2D Extent;
    public readonly RenderTarget BaseColor;
    public readonly RenderTarget Normal;
    /// <summary>Normal as the opaque surfaces left it, for decals to read while they write Normal.</summary>
    public readonly RenderTarget NormalCopy;

    public readonly RenderTarget Emissive;

    /// <summary>
    /// How far each pixel's surface moved on screen since last frame, in pixels, from now back to then. Cleared to
    /// zero where nothing was drawn. See <see cref="Motion"/>.
    /// </summary>
    public readonly RenderTarget Motion;

    public readonly RenderTarget Depth;
    public readonly RenderTarget Hdr;

    /// <summary>The lit image with anti-aliasing applied.</summary>
    public readonly RenderTarget Resolved;

    /// <summary>Last frame's Resolved, for anti-aliasing and reflections.</summary>
    public readonly RenderTarget History;

    /// <summary>The diffuse light of subsurface materials, before and after spreading across the screen.</summary>
    public readonly RenderTarget Scatter;
    public readonly RenderTarget ScatterBlurred;

    public readonly RenderTarget Ao;
    public readonly RenderTarget AoBlurred;
    public readonly RenderTarget Ldr;

    /// <summary>An earlier frame's Ldr, kept for a dissolve or wipe to show over the new one.</summary>
    public readonly RenderTarget Held;

    /// <summary>A copy of Hdr behind the glass, with blurrier mips, for glass to see through and bend.</summary>
    public readonly RenderTarget SceneColor;

    public GBuffer(VulkanContext context, Extent2D extent)
    {
        Extent = extent;
        BaseColor = new RenderTarget(context, BaseColorFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Normal = new RenderTarget(context, NormalFormat, extent, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        NormalCopy = new RenderTarget(context, NormalFormat, extent, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        Emissive = new RenderTarget(context, EmissiveFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Motion = new RenderTarget(context, MotionFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Depth = new RenderTarget(context, DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        Hdr = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        Resolved = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        History = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        Scatter = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        ScatterBlurred = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Ao = new RenderTarget(context, AoFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        AoBlurred = new RenderTarget(context, AoFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Ldr = new RenderTarget(context, LdrFormat, extent, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        Held = new RenderTarget(context, LdrFormat, extent, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        SceneColor = new RenderTarget(
            context, HdrFormat, extent, ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
            mips: MipsFor(extent));
    }

    /// <summary>Up to <see cref="SceneColorMips"/> levels, stopping before a level would be smaller than one pixel.</summary>
    public static uint MipsFor(Extent2D extent) =>
        Math.Min(SceneColorMips, (uint)Math.Floor(Math.Log2(Math.Max(1u, Math.Min(extent.Width, extent.Height)))) + 1);

    public void Dispose()
    {
        BaseColor.Dispose();
        Normal.Dispose();
        NormalCopy.Dispose();
        Emissive.Dispose();
        Motion.Dispose();
        Depth.Dispose();
        Hdr.Dispose();
        Resolved.Dispose();
        History.Dispose();
        Scatter.Dispose();
        ScatterBlurred.Dispose();
        Ao.Dispose();
        AoBlurred.Dispose();
        Ldr.Dispose();
        Held.Dispose();
        SceneColor.Dispose();
    }
}
