using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The ambient occlusion's and screen bounce's targets, at <see cref="Extent"/>: as traced, blurred across and then
/// down as well, and averaged over the last frames, with last frame's averages.
/// </summary>
internal sealed class OcclusionTargets : IDisposable
{
    public const Format AoFormat = Format.R8G8B8A8Unorm;
    public const Format BounceFormat = Format.B10G11R11UfloatPack32;
    public const Format AoSteadyFormat = Format.R16G16B16A16Sfloat;

    /// <summary>What an image needs to be drawn to and then copied from.</summary>
    private const ImageUsageFlags DrawnAndCopied = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit;

    public OcclusionTargets(VulkanContext context, Extent2D sceneExtent, OcclusionResolution resolution)
    {
        Resolution = resolution;
        Extent = resolution == OcclusionResolution.Full ? sceneExtent : HalfSize(sceneExtent);
        RenderTarget Color(Format format, ImageUsageFlags usage) => new(context, format, Extent, usage, ImageAspectFlags.ColorBit);
        Ao = Color(AoFormat, ImageUsageFlags.ColorAttachmentBit);
        AoAcross = Color(AoFormat, ImageUsageFlags.ColorAttachmentBit);
        AoBlurred = Color(AoFormat, ImageUsageFlags.ColorAttachmentBit);
        Bounce = Color(BounceFormat, ImageUsageFlags.ColorAttachmentBit);
        BounceAcross = Color(BounceFormat, ImageUsageFlags.ColorAttachmentBit);
        BounceBlurred = Color(BounceFormat, ImageUsageFlags.ColorAttachmentBit);
        AoSteady = Color(AoSteadyFormat, DrawnAndCopied);
        AoHistory = Color(AoSteadyFormat, ImageUsageFlags.TransferDstBit);
        BounceSteady = Color(AoSteadyFormat, DrawnAndCopied);
        BounceHistory = Color(AoSteadyFormat, ImageUsageFlags.TransferDstBit);
    }

    /// <summary>The size of the targets: the scene's, or half of it each way, rounded up.</summary>
    public Extent2D Extent { get; }

    /// <summary>What the targets were sized for.</summary>
    public OcclusionResolution Resolution { get; }

    /// <summary>Ambient occlusion as traced, then blurred across, then down as well, which is what the lighting reads.</summary>
    public RenderTarget Ao { get; }

    public RenderTarget AoAcross { get; }

    public RenderTarget AoBlurred { get; }

    /// <summary>
    /// The light bounced from the surfaces on screen, found with the ambient occlusion and blurred with it: rgb is the
    /// average light arriving from them, weighed by the cosine to the normal, in the lit picture's units.
    /// </summary>
    public RenderTarget Bounce { get; }

    public RenderTarget BounceAcross { get; }

    public RenderTarget BounceBlurred { get; }

    /// <summary>
    /// The occlusion and the bounce averaged over the last frames by ao_temporal.frag, which the blur then reads in
    /// place of <see cref="Ao"/> and <see cref="Bounce"/>, and last frame's copies of them. The occlusion's alpha is
    /// how many frames it averages, and the bounce's the depth it was traced at, which is why neither fits the traced
    /// targets' formats.
    /// </summary>
    public RenderTarget AoSteady { get; }

    public RenderTarget AoHistory { get; }

    public RenderTarget BounceSteady { get; }

    public RenderTarget BounceHistory { get; }

    /// <summary>
    /// Whether <see cref="AoHistory"/> and <see cref="BounceHistory"/> hold last frame's occlusion. Until then the
    /// occlusion is one frame's, blurred wide.
    /// </summary>
    public bool HistoryReady { get; set; }

    /// <summary>
    /// The size of a half-resolution target for a scene this size. Half rounds up, so the last pixel of an odd row or
    /// column still has a pixel of its own there.
    /// </summary>
    public static Extent2D HalfSize(Extent2D extent) => new((extent.Width + 1) / 2, (extent.Height + 1) / 2);

    public void Dispose()
    {
        Ao.Dispose();
        AoAcross.Dispose();
        AoBlurred.Dispose();
        Bounce.Dispose();
        BounceAcross.Dispose();
        BounceBlurred.Dispose();
        AoSteady.Dispose();
        AoHistory.Dispose();
        BounceSteady.Dispose();
        BounceHistory.Dispose();
    }
}
