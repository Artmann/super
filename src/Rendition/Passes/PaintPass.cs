using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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
/// The oil-paint look. <see cref="PaintFilter"/> filters the picture at half size, and paint_composite.frag lays the
/// result back over the full-size picture in place, as much as each pixel's paint says. Runs after bloom, which reads
/// the picture unpainted, and before the tonemap. See "Paint" in docs/design.md.
/// </summary>
internal sealed unsafe class PaintPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly PaintFilter _filter;
    private readonly FullscreenPass _composite;
    private readonly ImageGroup _compositeGroup;
    private readonly SamplerId _point;
    private readonly AdoptedTarget _target;
    private readonly CommandList _commands = new();

    // The half-size painted picture, made the first time a frame paints, so a game that never paints never spends the
    // memory on it. Null until then, and again after a resize.
    private RenderTarget? _painted;
    private Extent2D _half;
    private (ImageView Source, ImageView Depth) _reads;
    private PaintConstants? _constants;

    public PaintPass(
        (VulkanContext Context, VulkanGpu Gpu) device, (Sampler Nearest, Sampler Linear) samplers, DescriptorSetLayout frameSetLayout, DescriptorSetLayout exposureLayout)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _filter = new PaintFilter(context, samplers, frameSetLayout, exposureLayout);
        _composite = new FullscreenPass(gpu, "paint_composite.frag", [GpuFormat.Rgba16Float], blend: true, borrowed: [frameSetLayout, default]);
        _compositeGroup = new ImageGroup(gpu, gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));
        _point = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest));
        _target = new AdoptedTarget(gpu);
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
    public void Record(CommandBuffer cmd, (DescriptorSet Frame, DescriptorSet Exposure) tables, (RenderTarget Target, Extent2D Extent) target)
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

        _filter.Record(cmd, tables, (_painted.Image, _half), constants);

        // The composite blends over the picture the filter just read, so it loads what's there. The meter, a compute
        // pass, and the tonemap read it after.
        var bytes = new ReadOnlySpan<byte>(&constants, (int)PaintConstants.Size);
        _commands.Reset();
        _composite.Record(_commands, [new ColorTarget(_target.For(target.Target, target.Extent), LoadOp.Load, default)],
            [_gpu.Adopt(tables.Frame), _compositeGroup.Id], bytes);
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        _compositeGroup.Dispose();
        _target.Dispose();
        _painted?.Dispose();
        _composite.Dispose();
        _filter.Dispose();
    }

    private void WriteSets()
    {
        _filter.Write(_reads.Source, _reads.Depth, _painted!.View);
        _compositeGroup.Make(_composite.Pipeline, 1,
            [("paintedTexture", _painted.View), new GroupImage("depthTexture", _reads.Depth, _point, ReadOnlyDepth: true)]);
    }
}
