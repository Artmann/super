using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws <see cref="FrameExtras.Lines"/> over the finished picture, after the grade and before the overlay, through
/// the GPU layer. Each line is one instance of six vertices, a quad as thick as asked in window pixels. line.frag
/// reads the scene's depth itself, so a hidden part can show faintly in the same draw and the scene's depth can be any
/// size: the render scale and the upscaler leave it smaller than the window. See "Debug lines" in docs/design.md.
/// </summary>
internal sealed unsafe class LinePass : IDisposable
{
    /// <summary>How many lines each frame's buffer holds at first. It doubles whenever a frame draws more.</summary>
    public const int FirstCapacity = 4096;

    private readonly VulkanGpu _gpu;
    private readonly PipelineId _pipeline;
    private readonly FrameBuffers _buffers;
    private readonly ImageGroup _depth;
    private (int Frame, int Lines) _drawn;
    private Matrix4x4 _viewProjection;

    public LinePass((VulkanContext Context, VulkanGpu Gpu) device, GpuFormat targetFormat, int framesInFlight)
    {
        _gpu = device.Gpu;
        _buffers = new FrameBuffers(device, framesInFlight, FirstCapacity * sizeof(LineDraw), BufferUsageFlags.VertexBufferBit);
        _depth = new ImageGroup(_gpu, _gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest)));

        // Each line is one instance of the vertex buffer, blended over the picture.
        _pipeline = _gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = _gpu.CreateShader("line.vert"),
            FragmentShader = _gpu.CreateShader("line.frag"),
            Targets = [new ColorTargetState(targetFormat, BlendState.Premultiplied)],
            Vertices =
            [
                new VertexLayout(sizeof(LineDraw), PerInstance: true,
                [
                    new(0, VertexFormat.Float32x4, (int)Marshal.OffsetOf<LineDraw>(nameof(LineDraw.From))),
                    new(1, VertexFormat.Float32x4, (int)Marshal.OffsetOf<LineDraw>(nameof(LineDraw.To))),
                    new(2, VertexFormat.Float32x4, (int)Marshal.OffsetOf<LineDraw>(nameof(LineDraw.Color))),
                ]),
            ],
        });
    }

    /// <summary>
    /// Points the lines at the scene's depth, which they read to find what hides them. Makes the bind group again, so
    /// it must only be called while the GPU is idle.
    /// </summary>
    public void SetDepth(ImageView depth) => _depth.Make(_pipeline, 0, [new GroupImage("sceneDepth", depth, Use: GroupImageUse.Depth)]);

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
        int capacity = (int)(_buffers.Size(frameIndex) / sizeof(LineDraw));
        if (lines.Length > capacity)
        {
            while (capacity < lines.Length)
            {
                capacity *= 2;
            }

            _buffers.Grow(frameIndex, (long)capacity * sizeof(LineDraw));
        }

        lines.CopyTo(_buffers.Items<LineDraw>(frameIndex, capacity));
    }

    /// <summary>Draws the lines uploaded last over <paramref name="target"/>, keeping what it holds, or does nothing when there are none.</summary>
    public void Record(CommandList commands, ViewId target, Extent2D extent)
    {
        if (_drawn.Lines == 0)
        {
            return;
        }

        var constants = new LineConstants
        {
            ViewProjection = _viewProjection,
            Screen = new Vector4(extent.Width, extent.Height, 1f / extent.Width, 1f / extent.Height),
            // An orthographic view keeps M34 at 0. Its depth runs evenly, so the slack is a fixed amount, not a share.
            Perspective = _viewProjection.M34 != 0f || _viewProjection.M14 != 0f || _viewProjection.M24 != 0f ? 1f : 0f,
        };

        commands.BeginRenderPass([new ColorTarget(target, LoadOp.Load, default)], null);
        commands.SetPipeline(_pipeline);
        commands.SetBindGroup(0, _depth.Id);
        commands.SetConstants(new ReadOnlySpan<byte>(&constants, sizeof(LineConstants)));
        commands.SetVertexBuffer(0, _buffers[_drawn.Frame], 0);
        commands.Draw(6, _drawn.Lines, 0, 0);
        commands.EndPass();
    }

    public void Dispose()
    {
        _depth.Dispose();
        _buffers.Dispose();
        _gpu.Destroy(_pipeline);
    }

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
