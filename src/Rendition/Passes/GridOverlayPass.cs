using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws <see cref="FrameExtras.GridOverlays"/> over the finished picture, after the grade and before the debug lines.
/// Each overlay is one instance of a cube round its cells, whose fragment shader finds the surface at each pixel from
/// the scene's depth and draws the grid's lines, fill and border on it. Seen through the camera without the
/// anti-aliasing shift, so they hold still. See "Grid overlays" in docs/design.md.
/// </summary>
internal sealed unsafe class GridOverlayPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DescriptorSet _depth;
    private readonly DescriptorSetLayout _bufferLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _bufferSets;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _pipeline;
    private readonly GpuBuffer[] _overlays;
    private readonly GpuBuffer[] _cells;
    private readonly GpuGridOverlay[] _packed = new GpuGridOverlay[GridOverlayBatch.MaxOverlays];
    private int _cellWords;
    private (int Frame, int Overlays) _drawn;

    /// <summary>Overlays that read the scene's depth through <paramref name="depth"/>, which <see cref="SceneMarks"/> keeps pointed at it.</summary>
    public GridOverlayPass(VulkanContext context, (DescriptorSetLayout Layout, DescriptorSet Set) depth, Format targetFormat, int framesInFlight)
    {
        _context = context;
        _depth = depth.Set;
        _overlays = new GpuBuffer[framesInFlight];
        _cells = new GpuBuffer[framesInFlight];
        _bufferSets = new DescriptorSet[framesInFlight];
        _bufferLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.FragmentBit),
        ]);
        _pool = context.CreateDescriptorPool(
            (uint)framesInFlight, new DescriptorPoolSize(DescriptorType.StorageBuffer, (uint)(2 * framesInFlight)));
        context.AllocateSets(_pool, _bufferLayout, _bufferSets);
        for (int i = 0; i < framesInFlight; i++)
        {
            _overlays[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(GridOverlayBatch.MaxOverlays * sizeof(GpuGridOverlay)), BufferUsageFlags.StorageBufferBit);
            _cells[i] = GpuBuffer.CreateHostVisible(
                context, GridOverlayBatch.MaxCellWords * sizeof(uint), BufferUsageFlags.StorageBufferBit);
            WriteBuffers(_bufferSets[i], _overlays[i], _cells[i]);
        }

        _layout = context.CreatePipelineLayout(
            [depth.Layout, _bufferLayout], new PushConstantRange(ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Vector4)));
        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "grid_overlay.vert",
            FragmentShader = "grid_overlay.frag",
            Layout = _layout,
            ColorFormats = [targetFormat],
            DepthFormat = Format.Undefined,
            Transparent = true,
            CullMode = CullModeFlags.FrontBit,
        });
    }

    /// <summary>How many overlays the last <see cref="Prepare"/> packed.</summary>
    public int Count { get; private set; }

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
        _packed.AsSpan(0, Count).CopyTo(new Span<GpuGridOverlay>(_overlays[frameIndex].Mapped, GridOverlayBatch.MaxOverlays));
        view.Extras.GridCells[.._cellWords].CopyTo(new Span<uint>(_cells[frameIndex].Mapped, GridOverlayBatch.MaxCellWords));
    }

    /// <summary>
    /// Draws the overlays uploaded last over <paramref name="target"/>, a picture already in the colour attachment
    /// layout, or does nothing when there are none.
    /// </summary>
    public void Record(CommandBuffer cmd, (ImageView View, Extent2D Extent) target)
    {
        if (_drawn.Overlays == 0)
        {
            return;
        }

        var api = _context.Api;
        var extent = target.Extent;
        api.BeginOver(cmd, target);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        var sets = stackalloc DescriptorSet[] { _depth, _bufferSets[_drawn.Frame] };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);
        var screen = new Vector4(extent.Width, extent.Height, 1f / extent.Width, 1f / extent.Height);
        api.CmdPushConstants(cmd, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Vector4), &screen);

        // A cube is 36 corners, made in the vertex shader from the corner's number.
        api.CmdDraw(cmd, 36, (uint)_drawn.Overlays, 0, 0);
        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        for (int i = 0; i < _overlays.Length; i++)
        {
            _overlays[i].Dispose();
            _cells[i].Dispose();
        }

        _pipeline.Destroy();
        _context.DestroyLayouts(_layout, _pool, _bufferLayout);
    }

    private void WriteBuffers(DescriptorSet set, GpuBuffer overlays, GpuBuffer cells)
    {
        var infos = stackalloc DescriptorBufferInfo[]
        {
            new(overlays.Handle, 0, Vk.WholeSize),
            new(cells.Handle, 0, Vk.WholeSize),
        };
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(set, 0, &infos[0]),
            Descriptors.BufferWrite(set, 1, &infos[1]),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }
}
