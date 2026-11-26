using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws <see cref="FrameExtras.Lines"/> over the finished picture, after the grade and before the overlay. Each line
/// is one instance of six vertices, a quad as thick as asked in window pixels. line.frag reads the scene's depth
/// itself, so a hidden part can show faintly in the same draw and the scene's depth can be any size: the render scale
/// and the upscaler leave it smaller than the window. See "Debug lines" in docs/design.md.
/// </summary>
internal sealed unsafe class LinePass : IDisposable
{
    /// <summary>How many lines each frame's buffer holds at first. It doubles whenever a frame draws more.</summary>
    public const int FirstCapacity = 4096;

    private readonly VulkanContext _context;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;
    private readonly GpuBuffer[] _buffers;
    private readonly int[] _capacities;
    private readonly DescriptorSet _depth;
    private (int Frame, int Lines) _drawn;
    private Matrix4x4 _viewProjection;

    public LinePass(VulkanContext context, DescriptorKit kit, Format targetFormat, int framesInFlight)
    {
        _context = context;
        var depthLayout = kit.OneTextureLayout;
        _depth = kit.NewSet(depthLayout);
        _buffers = new GpuBuffer[framesInFlight];
        _capacities = new int[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = NewBuffer(FirstCapacity);
            _capacities[i] = FirstCapacity;
        }

        _layout = context.CreatePipelineLayout(
            [depthLayout], new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(LineConstants)));
        _pipeline = CreatePipeline(context, _layout, targetFormat);
    }

    /// <summary>
    /// Copies this frame's lines to the GPU, growing the frame's buffer first when they don't fit, and keeps the camera
    /// they're seen through, without the anti-aliasing shift, so they hold still. The frame slot's last use has
    /// finished by now, so its buffer can be replaced.
    /// </summary>
    public void Upload(int frameIndex, in FrameView view)
    {
        var lines = view.Extras.Lines;
        _drawn = (frameIndex, lines.Length);
        _viewProjection = view.View * view.Projection;
        if (lines.Length > _capacities[frameIndex])
        {
            int capacity = _capacities[frameIndex];
            while (capacity < lines.Length)
            {
                capacity *= 2;
            }

            _buffers[frameIndex].Dispose();
            _buffers[frameIndex] = NewBuffer(capacity);
            _capacities[frameIndex] = capacity;
        }

        lines.CopyTo(new Span<LineDraw>(_buffers[frameIndex].Mapped, _capacities[frameIndex]));
    }

    /// <summary>Points the lines at the scene's depth, which they read to show faintly behind it.</summary>
    public void WriteSet(Sampler nearest, ImageView depth) =>
        ImageSets.Write(_context, _depth, [new(nearest, depth, ImageLayout.DepthReadOnlyOptimal)]);

    /// <summary>
    /// Draws the lines uploaded last over <paramref name="target"/>, a picture already in the colour attachment
    /// layout, or does nothing when there are none.
    /// </summary>
    public void Record(CommandBuffer cmd, (ImageView View, Extent2D Extent) target)
    {
        if (_drawn.Lines == 0)
        {
            return;
        }

        var api = _context.Api;
        var extent = target.Extent;
        api.BeginOver(cmd, target);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        var set = _depth;
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);

        var constants = new LineConstants
        {
            ViewProjection = _viewProjection,
            Screen = new Vector4(extent.Width, extent.Height, 1f / extent.Width, 1f / extent.Height),
            // An orthographic view keeps M34 at 0. Its depth runs evenly, so the slack is a fixed amount, not a share.
            Perspective = _viewProjection.M34 != 0f || _viewProjection.M14 != 0f || _viewProjection.M24 != 0f ? 1f : 0f,
        };
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(LineConstants), &constants);

        var buffer = _buffers[_drawn.Frame].Handle;
        ulong offset = 0;
        api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        api.CmdDraw(cmd, 6, (uint)_drawn.Lines, 0, 0);
        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }

        _pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }

    // Each line is one instance of the vertex buffer, blended over the picture.
    private static LivePipeline CreatePipeline(VulkanContext context, PipelineLayout layout, Format targetFormat)
    {
        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new(0, (uint)sizeof(LineDraw), VertexInputRate.Instance),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<LineDraw>(nameof(LineDraw.From))),
            new(1, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<LineDraw>(nameof(LineDraw.To))),
            new(2, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<LineDraw>(nameof(LineDraw.Color))),
        ];
        return PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "line.vert",
            FragmentShader = "line.frag",
            Layout = layout,
            ColorFormats = [targetFormat],
            DepthFormat = Format.Undefined,
            VertexBindings = bindings,
            VertexAttributes = attributes,
            Transparent = true,
        });
    }

    private GpuBuffer NewBuffer(int lines) =>
        GpuBuffer.CreateHostVisible(_context, (ulong)(lines * sizeof(LineDraw)), BufferUsageFlags.VertexBufferBit);

    /// <summary>What line.vert and line.frag are pushed. Must match their LineConstants block.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LineConstants
    {
        public Matrix4x4 ViewProjection;

        /// <summary>x, y: the window's size in pixels; z, w: one over them.</summary>
        public Vector4 Screen;

        /// <summary>1 when the camera has perspective, 0 when it's orthographic.</summary>
        public float Perspective;
    }
}
