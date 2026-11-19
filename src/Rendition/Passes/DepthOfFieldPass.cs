using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What the depth of field shaders are told for a frame. Must match Lens in Shaders/dof_coc.frag,
/// dof_blur.frag and dof_combine.frag.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DepthOfFieldConstants
{
    /// <summary>The largest circle drawn, as a radius in the scene's pixels. A wider one is drawn this wide.</summary>
    public const float MaxRadius = 16f;

    /// <summary>Where the lens is focused, in metres in front of the camera.</summary>
    public float FocusDistance;

    /// <summary>
    /// How many of the scene's pixels across the circle is for a point at the horizon. A point at depth d
    /// gets this times (d − focus) / d, which is negative in front of the focus.
    /// </summary>
    public float PixelsAtInfinity;

    /// <summary>The largest radius, in the scene's pixels.</summary>
    public float MaxRadiusPixels;

    public float Unused;

    public static unsafe uint Size => (uint)sizeof(DepthOfFieldConstants);

    /// <summary>
    /// The constants for a frame whose scene is <paramref name="sceneWidth"/> pixels wide, or null when no
    /// depth of field is drawn: the switch is off, the aperture is 0, or the lens settings are ones a
    /// view would refuse, which Rendition used alone takes as no depth of field rather than throwing.
    /// </summary>
    public static DepthOfFieldConstants? For(in PostSettings post, RenderEffects off, int sceneWidth)
    {
        if (off.HasFlag(RenderEffects.DepthOfField) || post.Aperture == 0f || sceneWidth <= 0)
        {
            return null;
        }

        float focalLength = ThinLens.FocalLength(post.FocalLength);
        if (!ThinLens.IsValid(post.FocusDistance, post.Aperture, focalLength))
        {
            return null;
        }

        float circle = ThinLens.CircleOfConfusion(float.PositiveInfinity, post.FocusDistance, post.Aperture, focalLength);
        return new DepthOfFieldConstants
        {
            FocusDistance = post.FocusDistance,
            PixelsAtInfinity = ThinLens.InPixels(circle, sceneWidth),
            MaxRadiusPixels = MaxRadius,
        };
    }
}

/// <summary>
/// Blurs what's in front of and behind the focus, as a camera lens does. At half size it works out each
/// pixel's circle from its depth, gathers a near layer and a far layer over a disc of 48 taps, and then
/// blends both over the full-size picture: the far layer where the pixel itself is out of focus, the
/// near layer wherever a blurred near thing spreads, even over sharp things behind it.
/// </summary>
internal sealed unsafe class DepthOfFieldPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DepthOfFieldSets _sets;
    private readonly FullscreenPass _cocPass;
    private readonly FullscreenPass _blurPass;
    private readonly FullscreenPass _combinePass;

    // The half-size layers, made the first time a frame draws depth of field, so a game that never
    // opens a lens never spends the memory on them. Null until then, and again after a resize.
    private DepthOfFieldLayers? _layers;
    private Extent2D _half;
    private ImageView _source;
    private ImageView _depth;

    public DepthOfFieldPass(VulkanContext context, Sampler pointSampler, Sampler linearSampler, DescriptorSetLayout frameSetLayout)
    {
        _context = context;
        _sets = new DepthOfFieldSets(context, (pointSampler, linearSampler));
        ReadOnlySpan<DescriptorSetLayout> layouts = [frameSetLayout, _sets.Layout];
        _cocPass = new FullscreenPass(context, "dof_coc.frag", [GBuffer.HdrFormat], layouts, DepthOfFieldConstants.Size);
        _blurPass = new FullscreenPass(context, "dof_blur.frag", [GBuffer.HdrFormat, GBuffer.HdrFormat], layouts, DepthOfFieldConstants.Size);
        _combinePass = new FullscreenPass(context, "dof_combine.frag", [GBuffer.HdrFormat], layouts, DepthOfFieldConstants.Size, blend: true);
    }

    /// <summary>
    /// Sizes the half-size layers for scene targets <paramref name="extent"/> in size, and points the
    /// passes at <paramref name="source"/>, the picture blurred, and <paramref name="depth"/>, which glass
    /// leaves read-only. The layers are made by the first <see cref="Record"/> after, so frames still in
    /// flight must have finished.
    /// </summary>
    public void SetTargets(ImageView source, ImageView depth, Extent2D extent)
    {
        DestroyLayers();
        _half = new Extent2D(Math.Max((extent.Width + 1) / 2, 1), Math.Max((extent.Height + 1) / 2, 1));
        _source = source;
        _depth = depth;
    }

    /// <summary>Whether the half-size layers exist yet: only once a frame has drawn depth of field.</summary>
    public bool HasTargets => _layers is not null;

    /// <summary>Reads a different picture from the next frame on. Frames still in flight must have finished.</summary>
    public void SetSource(ImageView source, ImageView depth)
    {
        _source = source;
        _depth = depth;
        if (_layers is not null)
        {
            _sets.Write(_source, _depth, _layers);
        }
    }

    /// <summary>
    /// Blurs <paramref name="target"/>, the picture <see cref="SetSource"/> was given, in place. It must be
    /// ready to sample, and is left ready to sample again.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, (Image Image, ImageView View, Extent2D Extent) target, in DepthOfFieldConstants constants)
    {
        if (_layers is null)
        {
            // The first frame to draw depth of field since start or a resize. Nothing has used the
            // sets yet, so they can be written while recording.
            _layers = new DepthOfFieldLayers(_context, _half);
            _sets.Write(_source, _depth, _layers);
        }

        var api = _context.Api;
        var layers = _layers;
        var lens = constants;
        var bytes = new ReadOnlySpan<byte>(&lens, (int)DepthOfFieldConstants.Size);

        api.BeginColorTarget(cmd, layers.Coc.Image);
        _cocPass.Record(cmd, [layers.Coc.View], _half, [frameSet, _sets.Coc], bytes);
        api.EndColorTarget(cmd, layers.Coc.Image);

        api.BeginColorTarget(cmd, layers.Near.Image);
        api.BeginColorTarget(cmd, layers.Far.Image);
        _blurPass.Record(cmd, [layers.Near.View, layers.Far.View], _half, [frameSet, _sets.Blur], bytes);
        api.EndColorTarget(cmd, layers.Near.Image);
        api.EndColorTarget(cmd, layers.Far.Image);

        // The combine blends over the picture, so it keeps what's there. The meter, a compute pass, reads it after.
        api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit));
        _combinePass.Record(cmd, [target.View], target.Extent, [frameSet, _sets.Combine], bytes);
        api.Transition(cmd, new ImageTransition(target.Image, ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            (PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    public void Dispose()
    {
        DestroyLayers();
        _combinePass.Dispose();
        _blurPass.Dispose();
        _cocPass.Dispose();
        _sets.Dispose();
    }

    private void DestroyLayers()
    {
        _layers?.Dispose();
        _layers = null;
    }
}
