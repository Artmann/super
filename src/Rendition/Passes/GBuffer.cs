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
    public const Format BakedFormat = Format.R16G16B16A16Sfloat;
    public const Format MaskFormat = Format.R8G8B8A8Unorm;

    /// <summary>Mip levels of <see cref="SceneColor"/>, each half the last, for frosted glass to blur by.</summary>
    public const uint SceneColorMips = 7;

    /// <summary>The scene's size: every target but <see cref="Resolved"/>, <see cref="Ldr"/> and <see cref="Held"/>.</summary>
    public readonly Extent2D Extent;

    /// <summary>
    /// The size of the picture after anti-aliasing: <see cref="Resolved"/>, <see cref="Ldr"/> and
    /// <see cref="Held"/>. The scene's own size, unless FSR upscales it to the window's.
    /// </summary>
    public readonly Extent2D Display;

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

    /// <summary>
    /// The soft diffuse light a lightmap gives each pixel, already turned by its normal, in the irradiance map's units,
    /// with alpha how much the lightmap counts: 1 where the pixel is lightmapped, less in a chunk's edge band, where the
    /// lighting blends it with the live light. Cleared to zero, which the lighting reads as not lightmapped.
    /// </summary>
    public readonly RenderTarget Baked;

    /// <summary>Each pixel's baked shadow mask, one channel a Mixed light. Cleared to 1, fully lit.</summary>
    public readonly RenderTarget Mask;

    public readonly RenderTarget Depth;
    public readonly RenderTarget Hdr;

    /// <summary>The lit image with anti-aliasing applied, at <see cref="Display"/>. TAA draws it and FSR writes it from compute.</summary>
    public readonly RenderTarget Resolved;

    /// <summary>Last frame's Resolved, for anti-aliasing and reflections.</summary>
    public readonly RenderTarget History;

    /// <summary>The diffuse light of subsurface materials, before and after spreading across the screen.</summary>
    public readonly RenderTarget Scatter;
    public readonly RenderTarget ScatterBlurred;

    /// <summary>
    /// Ambient occlusion as traced, then blurred across, then down as well, which is what the lighting reads. At
    /// <see cref="AoExtent"/>.
    /// </summary>
    public readonly RenderTarget Ao;
    public readonly RenderTarget AoAcross;
    public readonly RenderTarget AoBlurred;

    /// <summary>The size of the occlusion targets: the scene's, or half of it each way, rounded up.</summary>
    public readonly Extent2D AoExtent;

    /// <summary>What the occlusion targets were sized for.</summary>
    public readonly OcclusionResolution Occlusion;

    /// <summary>
    /// The screen-space reflections, traced before the lighting reads them: the colour each ray found, with alpha how
    /// far to trust it, or -1 where the surface isn't traced. At <see cref="TracedExtent"/>.
    /// </summary>
    public readonly RenderTarget Traced;

    /// <summary>The size of <see cref="Traced"/>: the scene's, or half of it each way, rounded up.</summary>
    public readonly Extent2D TracedExtent;

    /// <summary>What <see cref="Traced"/> was sized for.</summary>
    public readonly ReflectionResolution Reflections;

    public readonly RenderTarget Ldr;

    /// <summary>An earlier frame's Ldr, kept for a dissolve or wipe to show over the new one.</summary>
    public readonly RenderTarget Held;

    /// <summary>A copy of Hdr behind the glass, with blurrier mips, for glass to see through and bend.</summary>
    public readonly RenderTarget SceneColor;

    public GBuffer(
        VulkanContext context, Extent2D extent, Extent2D? display = null, ReflectionResolution reflections = ReflectionResolution.Half,
        OcclusionResolution occlusion = OcclusionResolution.Half)
    {
        Extent = extent;
        Display = display ?? extent;
        Reflections = reflections;
        TracedExtent = reflections == ReflectionResolution.Full ? extent : HalfSize(extent);
        Occlusion = occlusion;
        AoExtent = occlusion == OcclusionResolution.Full ? extent : HalfSize(extent);
        BaseColor = new RenderTarget(context, BaseColorFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Normal = new RenderTarget(context, NormalFormat, extent, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        NormalCopy = new RenderTarget(context, NormalFormat, extent, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        Emissive = new RenderTarget(context, EmissiveFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Motion = new RenderTarget(context, MotionFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Baked = new RenderTarget(context, BakedFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Mask = new RenderTarget(context, MaskFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Depth = new RenderTarget(context, DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        // Where the device can trace, the trace debug view writes it from a compute shader instead of lighting it.
        Hdr = new RenderTarget(context, HdrFormat, extent,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | (context.CanTrace ? ImageUsageFlags.StorageBit : 0),
            ImageAspectFlags.ColorBit);
        Resolved = new RenderTarget(
            context, HdrFormat, Display, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.StorageBit,
            ImageAspectFlags.ColorBit);
        History = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        Scatter = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        ScatterBlurred = new RenderTarget(context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Ao = new RenderTarget(context, AoFormat, AoExtent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        AoAcross = new RenderTarget(context, AoFormat, AoExtent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        AoBlurred = new RenderTarget(context, AoFormat, AoExtent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Traced = new RenderTarget(context, HdrFormat, TracedExtent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Ldr = new RenderTarget(context, LdrFormat, Display, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        Held = new RenderTarget(context, LdrFormat, Display, ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        SceneColor = new RenderTarget(
            context, HdrFormat, extent, ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
            mips: MipsFor(extent));
    }

    /// <summary>Up to <see cref="SceneColorMips"/> levels, stopping before a level would be smaller than one pixel.</summary>
    public static uint MipsFor(Extent2D extent) =>
        Math.Min(SceneColorMips, (uint)Math.Floor(Math.Log2(Math.Max(1u, Math.Min(extent.Width, extent.Height)))) + 1);

    /// <summary>
    /// The size of a half-resolution target for a scene this size. Half rounds up, so the last pixel of an odd row or
    /// column still has a pixel of its own there.
    /// </summary>
    private static Extent2D HalfSize(Extent2D extent) => new((extent.Width + 1) / 2, (extent.Height + 1) / 2);

    public void Dispose()
    {
        Traced.Dispose();
        BaseColor.Dispose();
        Normal.Dispose();
        NormalCopy.Dispose();
        Emissive.Dispose();
        Motion.Dispose();
        Baked.Dispose();
        Mask.Dispose();
        Depth.Dispose();
        Hdr.Dispose();
        Resolved.Dispose();
        History.Dispose();
        Scatter.Dispose();
        ScatterBlurred.Dispose();
        Ao.Dispose();
        AoAcross.Dispose();
        AoBlurred.Dispose();
        Ldr.Dispose();
        Held.Dispose();
        SceneColor.Dispose();
    }
}
