using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>What the paint shaders are told for a frame. Must match Paint in Shaders/paint.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PaintConstants
{
    /// <summary>
    /// How far the brush may reach from its pixel, in half-size pixels, along an edge. Must match REACH in
    /// Shaders/paint_filter.comp, whose shared tile is this wide on every side.
    /// </summary>
    public const float MaxReach = 11f;

    /// <summary>The height the brush's size is given at.</summary>
    private const float BrushHeight = 1080f;

    /// <summary>The hardness at sharpness 0, and how many times harder sharpness 1 is.</summary>
    private const float SoftHardness = 200f;
    private const float HardnessRange = 30f;

    public float Amount;
    public float Near;
    public float Far;

    /// <summary>The brush's radius in half-size pixels, before it stretches along an edge.</summary>
    public float Radius;

    /// <summary>What a sector's variance is multiplied by before it is weighed.</summary>
    public float Hardness;

    public float Pad0;
    public float Pad1;
    public float Pad2;

    public static unsafe uint Size => (uint)sizeof(PaintConstants);

    /// <summary>
    /// The constants for a picture <paramref name="outputHeight"/> pixels tall, or null when nothing is painted: the
    /// switch is off, a debug view shows, or the amount is 0. Values a view would refuse are taken as the nearest that
    /// it wouldn't, as Rendition used alone doesn't throw for them.
    /// </summary>
    public static PaintConstants? For(in PaintSettings paint, RenderEffects off, DebugView debugView, int outputHeight)
    {
        if (off.HasFlag(RenderEffects.Paint) || debugView != DebugView.None || !(paint.Amount > 0f) || outputHeight <= 0)
        {
            return null;
        }

        float brush = paint.BrushSize > 0f ? Math.Min(paint.BrushSize, PaintSettings.MaxBrushSize) : PaintSettings.DefaultBrushSize;
        float sharpness = Math.Clamp(paint.Sharpness ?? PaintSettings.DefaultSharpness, 0f, 1f);
        float far = paint.Far > 0f && float.IsFinite(paint.Far) ? paint.Far : 0f;
        float near = far > 0f ? Math.Clamp(paint.Near, 0f, far * 0.999f) : 0f;
        return new PaintConstants
        {
            Amount = Math.Min(paint.Amount, 1f),
            Near = near,
            Far = far,
            Radius = Math.Min(brush * outputHeight / BrushHeight / 2f, MaxReach),
            Hardness = SoftHardness * MathF.Pow(HardnessRange, sharpness),
        };
    }
}

/// <summary>
/// The oil-paint look. paint_filter.comp runs an anisotropic Kuwahara filter over the picture at half size, and
/// paint_composite.frag lays the result back over the full-size picture in place, as much as each pixel's paint says.
/// Runs after bloom, which reads the picture unpainted, and before the tonemap. See "Paint" in docs/design.md.
/// </summary>
internal sealed unsafe class PaintPass : IDisposable
{
    private const uint GroupSize = 16;

    private readonly VulkanContext _context;
    private readonly (Sampler Nearest, Sampler Linear) _samplers;
    private readonly (DescriptorSetLayout Filter, DescriptorSetLayout Composite) _layouts;
    private readonly DescriptorPool _pool;
    private readonly (DescriptorSet Filter, DescriptorSet Composite) _sets;
    private readonly (PipelineLayout Layout, LivePipeline Pipeline) _filter;
    private readonly FullscreenPass _composite;

    // The half-size painted picture, made the first time a frame paints, so a game that never paints never spends the
    // memory on it. Null until then, and again after a resize.
    private RenderTarget? _painted;
    private Extent2D _half;
    private (ImageView Source, ImageView Depth) _reads;
    private PaintConstants? _constants;

    public PaintPass(VulkanContext context, (Sampler Nearest, Sampler Linear) samplers, DescriptorSetLayout frameSetLayout, DescriptorSetLayout exposureLayout)
    {
        _context = context;
        _samplers = samplers;
        _layouts = (
            context.CreateSetLayout(
            [
                new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
                new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
                new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            ]),
            context.CreateSetLayout(
            [
                new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
                new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            ]));
        _pool = context.CreateDescriptorPool(
            2, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 4), new DescriptorPoolSize(DescriptorType.StorageImage, 1));
        _sets = (context.AllocateSet(_pool, _layouts.Filter), context.AllocateSet(_pool, _layouts.Composite));

        var filterLayout = context.CreatePipelineLayout(
            [frameSetLayout, _layouts.Filter, exposureLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, PaintConstants.Size));
        _filter = (filterLayout, PipelineFactory.CreateCompute(context, "paint_filter.comp", filterLayout));
        _composite = new FullscreenPass(
            context, "paint_composite.frag", [GBuffer.HdrFormat], [frameSetLayout, _layouts.Composite], PaintConstants.Size, blend: true);
    }

    /// <summary>Whether this frame paints, once <see cref="Take"/> has run.</summary>
    public bool Painting => _constants is not null;

    /// <summary>Takes the paint the view asks for, before the frame is recorded, for a picture <paramref name="outputHeight"/> tall.</summary>
    public void Take(in FrameView view, RenderEffects off, int outputHeight) =>
        _constants = PaintConstants.For(view.Picture.Paint, off, view.Picture.DebugView, outputHeight);

    /// <summary>
    /// Sizes the half-size picture for a picture <paramref name="display"/> in size, and points the passes at
    /// <paramref name="source"/>, the picture painted, and <paramref name="depth"/>. The half-size picture is made by the
    /// first <see cref="Record"/> after, so frames still in flight must have finished.
    /// </summary>
    public void SetTargets(ImageView source, ImageView depth, Extent2D display)
    {
        _painted?.Dispose();
        _painted = null;
        _half = new Extent2D(Math.Max((display.Width + 1) / 2, 1), Math.Max((display.Height + 1) / 2, 1));
        _reads = (source, depth);
    }

    /// <summary>Reads a different picture from the next frame on. Frames still in flight must have finished.</summary>
    public void SetSource(ImageView source, ImageView depth)
    {
        _reads = (source, depth);
        if (_painted is not null)
        {
            WriteSets();
        }
    }

    /// <summary>
    /// Paints <paramref name="target"/>, the picture <see cref="SetSource"/> was given, in place, when <see cref="Take"/>
    /// said to. It must be ready to sample, and is left ready to sample again.
    /// </summary>
    public void Record(CommandBuffer cmd, (DescriptorSet Frame, DescriptorSet Exposure) tables, (Image Image, ImageView View, Extent2D Extent) target)
    {
        if (_constants is not { } constants)
        {
            return;
        }

        if (_painted is null)
        {
            // The first frame to paint since start or a resize. Nothing has used the sets yet, so they can be written
            // while recording.
            _painted = new RenderTarget(_context, GBuffer.HdrFormat, _half, ImageUsageFlags.StorageBit, ImageAspectFlags.ColorBit);
            WriteSets();
        }

        var api = _context.Api;
        var painted = _painted;

        // The filter reads depth from compute, which no earlier pass may have.
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));

        // Last frame's composite read it; its contents are all replaced.
        api.Transition(cmd, new ImageTransition(painted.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.None),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        var sets = stackalloc DescriptorSet[] { tables.Frame, _sets.Filter, tables.Exposure };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _filter.Pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _filter.Layout, 0, 3, sets, 0, null);
        api.CmdPushConstants(cmd, _filter.Layout, ShaderStageFlags.ComputeBit, 0, PaintConstants.Size, &constants);
        api.CmdDispatch(cmd, (_half.Width + GroupSize - 1) / GroupSize, (_half.Height + GroupSize - 1) / GroupSize, 1);

        api.Transition(cmd, new ImageTransition(painted.Image, ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));

        RecordComposite(cmd, tables.Frame, target, constants);
    }

    // The composite blends over the picture the filter just read, so it keeps what's there. The meter, a compute pass,
    // and the tonemap read it after.
    private void RecordComposite(CommandBuffer cmd, DescriptorSet frame, (Image Image, ImageView View, Extent2D Extent) target, PaintConstants constants)
    {
        _context.Api.BeginBlendOnto(cmd, target.Image);
        var bytes = new ReadOnlySpan<byte>(&constants, (int)PaintConstants.Size);
        _composite.Record(cmd, [target.View], target.Extent, [frame, _sets.Composite], bytes);
        _context.Api.EndBlendOnto(cmd, target.Image);
    }

    public void Dispose()
    {
        _painted?.Dispose();
        _composite.Dispose();
        _filter.Pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _filter.Layout, null);
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _layouts.Filter, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _layouts.Composite, null);
    }

    private void WriteSets()
    {
        var (nearest, linear) = _samplers;
        var source = new DescriptorImageInfo(linear, _reads.Source, ImageLayout.ShaderReadOnlyOptimal);
        var depth = new DescriptorImageInfo(nearest, _reads.Depth, ImageLayout.DepthReadOnlyOptimal);
        var output = new DescriptorImageInfo(default, _painted!.View, ImageLayout.General);
        var painted = new DescriptorImageInfo(linear, _painted.View, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(_sets.Filter, 0, DescriptorType.CombinedImageSampler, &source),
            Descriptors.ImageWrite(_sets.Filter, 1, DescriptorType.CombinedImageSampler, &depth),
            Descriptors.ImageWrite(_sets.Filter, 2, DescriptorType.StorageImage, &output),
            Descriptors.ImageWrite(_sets.Composite, 0, DescriptorType.CombinedImageSampler, &painted),
            Descriptors.ImageWrite(_sets.Composite, 1, DescriptorType.CombinedImageSampler, &depth),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
    }
}
