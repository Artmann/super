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
    public const Format HdrFormat = PictureTargets.HdrFormat;
    public const Format AoFormat = OcclusionTargets.AoFormat;
    public const Format BounceFormat = OcclusionTargets.BounceFormat;
    public const Format AoSteadyFormat = OcclusionTargets.AoSteadyFormat;
    public const Format LdrFormat = PictureTargets.LdrFormat;
    public const Format MotionFormat = Format.R16G16Sfloat;
    public const Format BakedFormat = Format.R16G16B16A16Sfloat;
    public const Format MaskFormat = Format.R8G8B8A8Unorm;

    /// <summary>Mip levels of <see cref="SceneColor"/>, each half the last, for frosted glass to blur by.</summary>
    public const uint SceneColorMips = 7;

    /// <summary>The scene's size: every target but the <see cref="Picture"/>'s display-sized ones.</summary>
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

    /// <summary>The diffuse light of subsurface materials, before and after spreading across the screen.</summary>
    public readonly RenderTarget Scatter;
    public readonly RenderTarget ScatterBlurred;

    /// <summary>The ambient occlusion's and screen bounce's targets.</summary>
    public readonly OcclusionTargets Occlusion;

    /// <summary>
    /// The screen-space reflections, traced before the lighting reads them: the colour each ray found, with alpha how
    /// far to trust it, or -1 where the surface isn't traced. At <see cref="TracedExtent"/>.
    /// </summary>
    public readonly RenderTarget Traced;

    /// <summary>The size of <see cref="Traced"/>: the scene's, or half of it each way, rounded up.</summary>
    public readonly Extent2D TracedExtent;

    /// <summary>What <see cref="Traced"/> was sized for.</summary>
    public readonly ReflectionResolution Reflections;

    /// <summary>
    /// The picture after anti-aliasing, at the display's size: the scene's own size, unless FSR upscales it to the
    /// window's. Each set of targets keeps what its images hold, so drawing a reflection probe into its small targets
    /// leaves the screen's alone.
    /// </summary>
    public readonly PictureTargets Picture;

    /// <summary>A copy of Hdr behind the glass, with blurrier mips, for glass to see through and bend.</summary>
    public readonly RenderTarget SceneColor;

    public GBuffer(
        VulkanContext context, Extent2D extent, Extent2D? display = null, ReflectionResolution reflections = ReflectionResolution.Half,
        OcclusionResolution occlusion = OcclusionResolution.Half)
    {
        Extent = extent;
        Reflections = reflections;
        TracedExtent = reflections == ReflectionResolution.Full ? extent : OcclusionTargets.HalfSize(extent);
        RenderTarget Color(Format format, ImageUsageFlags usage) => new(context, format, extent, usage, ImageAspectFlags.ColorBit);
        BaseColor = Color(BaseColorFormat, ImageUsageFlags.ColorAttachmentBit);
        Normal = Color(NormalFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit);
        NormalCopy = Color(NormalFormat, ImageUsageFlags.TransferDstBit);
        Emissive = Color(EmissiveFormat, ImageUsageFlags.ColorAttachmentBit);
        Motion = Color(MotionFormat, ImageUsageFlags.ColorAttachmentBit);
        Baked = Color(BakedFormat, ImageUsageFlags.ColorAttachmentBit);
        Mask = Color(MaskFormat, ImageUsageFlags.ColorAttachmentBit);
        Depth = new RenderTarget(context, DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        // Where the device can trace, the trace debug view writes it from a compute shader instead of lighting it.
        Hdr = Color(HdrFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | (context.CanTrace ? ImageUsageFlags.StorageBit : 0));
        Picture = new PictureTargets(context, extent, display ?? extent);
        Scatter = Color(HdrFormat, ImageUsageFlags.ColorAttachmentBit);
        ScatterBlurred = Color(HdrFormat, ImageUsageFlags.ColorAttachmentBit);
        Occlusion = new OcclusionTargets(context, extent, occlusion);
        Traced = new RenderTarget(context, HdrFormat, TracedExtent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);

        // Up to SceneColorMips levels, stopping before a level would be smaller than one pixel.
        uint mips = Math.Min(SceneColorMips, (uint)Math.Floor(Math.Log2(Math.Max(1u, Math.Min(extent.Width, extent.Height)))) + 1);
        SceneColor = new RenderTarget(
            context, HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit, mips: mips);
    }

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
        Scatter.Dispose();
        ScatterBlurred.Dispose();
        Occlusion.Dispose();
        Picture.Dispose();
        SceneColor.Dispose();
    }
}
