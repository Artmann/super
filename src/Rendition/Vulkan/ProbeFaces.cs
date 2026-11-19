using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The six faces a batch of light probes is drawn into, before the fold turns them into tiles: colour and depth,
/// six layers per slot, each slot a 2D array to draw all six at once with multiview.
/// </summary>
internal sealed unsafe class ProbeFaces : IDisposable
{
    /// <summary>How many texels across each captured face is. The probes keep only soft light, so this is plenty.</summary>
    public const uint FaceSize = 16;

    /// <summary>The faces' colour: the light seen in rgb and the distance in alpha.</summary>
    public const Format ColorFormat = Format.R16G16B16A16Sfloat;

    private readonly VulkanContext _context;
    private readonly (Image Image, GpuAllocation Memory) _color;
    private readonly (Image Image, GpuAllocation Memory) _depth;
    private readonly ImageView[] _colorViews;
    private readonly ImageView[] _depthViews;

    /// <summary>Makes faces for <paramref name="slots"/> probes, ready for the fold to sample. Waits for the GPU.</summary>
    public ProbeFaces(VulkanContext context, int slots)
    {
        _context = context;
        Slots = slots;
        uint layers = (uint)(slots * 6);
        _color = context.CreateArrayImage(
            (FaceSize, FaceSize, layers), ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        _depth = context.CreateArrayImage((FaceSize, FaceSize, layers), GBuffer.DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit);
        ArrayView = context.CreateArrayView(_color.Image, (ColorFormat, ImageAspectFlags.ColorBit), (0, layers));
        _colorViews = new ImageView[slots];
        _depthViews = new ImageView[slots];
        for (int slot = 0; slot < slots; slot++)
        {
            _colorViews[slot] = context.CreateArrayView(_color.Image, (ColorFormat, ImageAspectFlags.ColorBit), ((uint)slot * 6, 6));
            _depthViews[slot] = context.CreateArrayView(_depth.Image, (GBuffer.DepthFormat, ImageAspectFlags.DepthBit), ((uint)slot * 6, 6));
        }

        // The fold samples the faces, so they start readable for the first frame's barriers.
        var api = context.Api;
        var faces = _color.Image;
        context.SubmitAndWait(cmd => api.Transition(cmd, new ImageTransition(faces, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal) { LayerCount = layers }, (PipelineStageFlags2.None, AccessFlags2.None),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit)));
    }

    /// <summary>How many probes' faces fit: the rasterized budget.</summary>
    public int Slots { get; }

    /// <summary>Every probe's six faces, six layers per slot of the batch.</summary>
    public Image Image => _color.Image;

    /// <summary>The depth the faces are drawn with, laid out as <see cref="Image"/>.</summary>
    public Image DepthImage => _depth.Image;

    /// <summary>Every layer of the faces, for the fold to sample.</summary>
    public ImageView ArrayView { get; }

    /// <summary>The six colour layers of one slot, as a 2D array to draw all six faces at once with multiview.</summary>
    public ImageView ColorView(int slot) => _colorViews[slot];

    /// <summary>The six depth layers of one slot.</summary>
    public ImageView DepthView(int slot) => _depthViews[slot];

    public void Dispose()
    {
        var api = _context.Api;
        foreach (var view in _colorViews)
        {
            api.DestroyImageView(_context.Device, view, null);
        }

        foreach (var view in _depthViews)
        {
            api.DestroyImageView(_context.Device, view, null);
        }

        api.DestroyImageView(_context.Device, ArrayView, null);
        _context.DestroyArrayImage(_color);
        _context.DestroyArrayImage(_depth);
    }
}
