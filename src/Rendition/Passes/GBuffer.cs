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
    public const Format AoFormat = Format.R8Unorm;
    public const Format LdrFormat = Format.R8G8B8A8Srgb;

    public readonly Extent2D Extent;
    public readonly RenderTarget BaseColor;
    public readonly RenderTarget Normal;
    public readonly RenderTarget Emissive;
    public readonly RenderTarget Depth;
    public readonly RenderTarget Hdr;
    public readonly RenderTarget Ao;
    public readonly RenderTarget AoBlurred;
    public readonly RenderTarget Ldr;

    public GBuffer(VulkanContext context, Extent2D extent)
    {
        Extent = extent;
        BaseColor = new RenderTarget(context, BaseColorFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Normal = new RenderTarget(context, NormalFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Emissive = new RenderTarget(context, EmissiveFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Depth = new RenderTarget(context, DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        Hdr = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Ao = new RenderTarget(context, AoFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        AoBlurred = new RenderTarget(context, AoFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Ldr = new RenderTarget(context, LdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
    }

    public void Dispose()
    {
        BaseColor.Dispose();
        Normal.Dispose();
        Emissive.Dispose();
        Depth.Dispose();
        Hdr.Dispose();
        Ao.Dispose();
        AoBlurred.Dispose();
        Ldr.Dispose();
    }
}
