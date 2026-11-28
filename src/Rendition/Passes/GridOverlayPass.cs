using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws <see cref="FrameExtras.GridOverlays"/> over the finished picture, after the grade and before the debug lines,
/// through the GPU layer. Each overlay is one instance of a cube round its cells, whose fragment shader finds the
/// surface at each pixel from the scene's depth and draws the grid's lines, fill and border on it. Seen through the
/// camera without the anti-aliasing shift, so they hold still. See "Grid overlays" in docs/design.md.
/// </summary>
internal sealed unsafe class GridOverlayPass : IDisposable
{
    private readonly VulkanGpu _gpu;
    private readonly PipelineId _pipeline;
    private readonly (FrameBuffers Overlays, FrameBuffers Cells) _buffers;
    private readonly BindGroupId[] _bufferGroups;
    private readonly ImageGroup _depth;
    private readonly GpuGridOverlay[] _packed = new GpuGridOverlay[GridOverlayBatch.MaxOverlays];
    private int _cellWords;
    private (int Frame, int Overlays) _drawn;

    public GridOverlayPass((VulkanContext Context, VulkanGpu Gpu) device, GpuFormat targetFormat, int framesInFlight)
    {
        _gpu = device.Gpu;
        _buffers = (
            new FrameBuffers(device, framesInFlight, GridOverlayBatch.MaxOverlays * sizeof(GpuGridOverlay), BufferUsageFlags.StorageBufferBit),
            new FrameBuffers(device, framesInFlight, GridOverlayBatch.MaxCellWords * sizeof(uint), BufferUsageFlags.StorageBufferBit));
        _depth = new ImageGroup(_gpu, _gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest)));

        // The cube's front faces are left out, so its back faces draw even when the camera is inside it.
        _pipeline = _gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = _gpu.CreateShader("grid_overlay.vert"),
            FragmentShader = _gpu.CreateShader("grid_overlay.frag"),
            Targets = [new ColorTargetState(targetFormat, BlendState.Premultiplied)],
            Cull = CullMode.Front,
        });
        _bufferGroups = new BindGroupId[framesInFlight];
        for (int frame = 0; frame < framesInFlight; frame++)
        {
            _bufferGroups[frame] = _gpu.CreateBindGroup(_pipeline, 1,
                [Bind.Buffer("Overlays", _buffers.Overlays[frame]), Bind.Buffer("Cells", _buffers.Cells[frame])]);
        }
    }

    /// <summary>How many overlays the last <see cref="Prepare"/> packed.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Points the overlays at the scene's depth, which they read to find what they lie on. Makes the bind group again,
    /// so it must only be called while the GPU is idle.
    /// </summary>
    public void SetDepth(ImageView depth) => _depth.Make(_pipeline, 0, [new GroupImage("sceneDepth", depth, Use: GroupImageUse.Depth)]);

    /// <summary>
    /// Checks the frame's overlays and packs them, seen through its camera without the anti-aliasing shift, throwing
    /// before anything is recorded if one can't be drawn.
    /// </summary>
    public void Prepare(in FrameView view)
    {
        Count = 0;
        var overlays = view.Extras.GridOverlays;
        var cells = view.Extras.GridCells;
        GridOverlayBatch.Pack(overlays, cells.Length, view.View * view.Projection, _packed);
        Count = overlays.Length;
        _cellWords = overlays.IsEmpty ? 0 : cells.Length;
    }

    /// <summary>Copies the prepared overlays and their cells to the given frame in flight's buffers, which the GPU is done with.</summary>
    public void Upload(int frameIndex, in FrameView view)
    {
        _drawn = (frameIndex, Count);
        _packed.AsSpan(0, Count).CopyTo(_buffers.Overlays.Items<GpuGridOverlay>(frameIndex, GridOverlayBatch.MaxOverlays));
        view.Extras.GridCells[.._cellWords].CopyTo(_buffers.Cells.Items<uint>(frameIndex, GridOverlayBatch.MaxCellWords));
    }

    /// <summary>Draws the overlays uploaded last over <paramref name="target"/>, keeping what it holds, or does nothing when there are none.</summary>
    public void Record(CommandList commands, ViewId target, Extent2D extent)
    {
        if (_drawn.Overlays == 0)
        {
            return;
        }

        var screen = new Vector4(extent.Width, extent.Height, 1f / extent.Width, 1f / extent.Height);
        commands.BeginRenderPass([new ColorTarget(target, LoadOp.Load, default)], null);
        commands.SetPipeline(_pipeline);
        commands.SetBindGroups([_depth.Id, _bufferGroups[_drawn.Frame]]);
        commands.SetConstants(new ReadOnlySpan<byte>(&screen, sizeof(Vector4)));

        // A cube is 36 corners, made in the vertex shader from the corner's number.
        commands.Draw(36, _drawn.Overlays, 0, 0);
        commands.EndPass();
    }

    public void Dispose()
    {
        foreach (var group in _bufferGroups)
        {
            _gpu.Destroy(group);
        }

        _depth.Dispose();
        _buffers.Overlays.Dispose();
        _buffers.Cells.Dispose();
        _gpu.Destroy(_pipeline);
    }
}
