using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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
        if (off.HasFlag(RenderEffects.DepthOfField) || post.Lens.Aperture == 0f || sceneWidth <= 0)
        {
            return null;
        }

        float focalLength = ThinLens.FocalLength(post.Lens.FocalLength);
        if (!ThinLens.IsValid(post.Lens.FocusDistance, post.Lens.Aperture, focalLength))
        {
            return null;
        }

        float circle = ThinLens.CircleOfConfusion(float.PositiveInfinity, post.Lens.FocusDistance, post.Lens.Aperture, focalLength);
        return new DepthOfFieldConstants
        {
            FocusDistance = post.Lens.FocusDistance,
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
    private readonly VulkanGpu _gpu;
    private readonly DepthOfFieldSets _sets;
    private readonly FullscreenPass _cocPass;
    private readonly FullscreenPass _blurPass;
    private readonly FullscreenPass _combinePass;
    private readonly CommandList _commands = new();
    private readonly AdoptedTarget[] _targets;

    // The half-size layers, made the first time a frame draws depth of field, so a game that never
    // opens a lens never spends the memory on them. Null until then, and again after a resize.
    private DepthOfFieldLayers? _layers;
    private Extent2D _half;
    private (ImageView Source, ImageView Depth) _reads;

    public DepthOfFieldPass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _sets = new DepthOfFieldSets(gpu);
        _targets = [new(gpu), new(gpu), new(gpu), new(gpu)];
        // Group 0 is the frame's set, lent by the pass that fills it; group 1 is laid out from each shader.
        ReadOnlySpan<DescriptorSetLayout> borrowed = [frameSetLayout, default];
        ReadOnlySpan<GpuFormat> hdr = [GpuFormat.Rgba16Float];
        _cocPass = new FullscreenPass(gpu, "dof_coc.frag", hdr, borrowed: borrowed);
        _blurPass = new FullscreenPass(gpu, "dof_blur.frag", [GpuFormat.Rgba16Float, GpuFormat.Rgba16Float], borrowed: borrowed);
        _combinePass = new FullscreenPass(gpu, "dof_combine.frag", hdr, blend: true, borrowed: borrowed);
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
        _reads = (source, depth);
    }

    /// <summary>Whether the half-size layers exist yet: only once a frame has drawn depth of field.</summary>
    public bool HasTargets => _layers is not null;

    /// <summary>Reads a different picture from the next frame on. Frames still in flight must have finished.</summary>
    public void SetSource(ImageView source, ImageView depth)
    {
        _reads = (source, depth);
        if (_layers is not null)
        {
            WriteSets(_layers);
        }
    }

    /// <summary>
    /// Blurs <paramref name="target"/>, the picture <see cref="SetSource"/> was given, in place. It must be
    /// ready to sample, and is left ready to sample again.
    /// </summary>
    public void Record(
        CommandBuffer cmd, DescriptorSet frameSet, (RenderTarget Target, Extent2D Extent) target, in DepthOfFieldConstants constants)
    {
        if (_layers is null)
        {
            // The first frame to draw depth of field since start or a resize. Nothing has used the
            // groups yet, so they can be made while recording.
            _layers = new DepthOfFieldLayers(_context, _half);
            WriteSets(_layers);
        }

        var layers = _layers;
        var lens = constants;
        var bytes = new ReadOnlySpan<byte>(&lens, (int)DepthOfFieldConstants.Size);
        var frame = _gpu.Adopt(frameSet);

        _commands.Reset();
        _cocPass.Record(_commands, [Draw(0, layers.Coc, _half, LoadOp.DontCare)], [frame, _sets.Coc.Id], bytes);
        _blurPass.Record(_commands, [Draw(1, layers.Near, _half, LoadOp.DontCare), Draw(2, layers.Far, _half, LoadOp.DontCare)],
            [frame, _sets.Blur.Id], bytes);
        // The combine blends over the picture, so it keeps what's there.
        _combinePass.Record(_commands, [Draw(3, target.Target, target.Extent, LoadOp.Load)], [frame, _sets.Combine.Id], bytes);
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        DestroyLayers();
        foreach (var target in _targets)
        {
            target.Dispose();
        }

        _combinePass.Dispose();
        _blurPass.Dispose();
        _cocPass.Dispose();
        _sets.Dispose();
    }

    private ColorTarget Draw(int slot, RenderTarget target, Extent2D extent, LoadOp load) =>
        new(_targets[slot].For(target, extent), load, default);

    private void WriteSets(DepthOfFieldLayers layers) =>
        _sets.Write(_reads.Source, _reads.Depth, layers, (_cocPass.Pipeline, _blurPass.Pipeline, _combinePass.Pipeline));

    private void DestroyLayers()
    {
        _layers?.Dispose();
        _layers = null;
    }
}
