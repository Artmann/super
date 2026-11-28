using System.Numerics;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The G-buffer's targets adopted into the GPU layer as held, since the opaque stage moves them in and out, for the
/// G-buffer's draws and the grass's blades to begin their passes over.
/// </summary>
internal sealed class GBufferTargets(VulkanGpu gpu) : IDisposable
{
    private readonly AdoptedTarget[] _colors = [new(gpu), new(gpu), new(gpu), new(gpu), new(gpu), new(gpu)];
    private readonly AdoptedTarget _depth = new(gpu);

    /// <summary>
    /// Begins a pass over the G-buffer, every target <paramref name="clear"/>ed or keeping what it holds. Cleared, colour
    /// is nothing (no motion, not lightmapped), the mask is fully lit and depth is the reversed-Z far plane.
    /// </summary>
    public void Begin(CommandList commands, GBuffer targets, bool clear)
    {
        var load = clear ? LoadOp.Clear : LoadOp.Load;
        var extent = targets.Extent;
        ReadOnlySpan<RenderTarget> colors = [targets.BaseColor, targets.Normal, targets.Emissive, targets.Motion, targets.Baked, targets.Mask];
        Span<ColorTarget> into = stackalloc ColorTarget[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            var clearTo = ReferenceEquals(colors[i], targets.Mask) ? Vector4.One : Vector4.Zero;
            into[i] = new ColorTarget(_colors[i].ForHeld(colors[i].View, colors[i].Image, extent), load, clearTo);
        }

        commands.BeginRenderPass(into, new DepthTarget(_depth.ForHeld(targets.Depth.View, targets.Depth.Image, extent), load, 0f, ReadOnly: false));
    }

    public void Dispose()
    {
        foreach (var color in _colors)
        {
            color.Dispose();
        }

        _depth.Dispose();
    }
}
