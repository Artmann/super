using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// Draws each static draw into its lightmap rectangle on the <see cref="Baker"/>'s pages: straight in with conservative
/// rasterization, or a tile at a time through <see cref="TileResolve"/> without it. Each rectangle gets a number from
/// 1, drawn into its texels for the filter.
/// </summary>
internal sealed unsafe class TexelDraw : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SceneStores _stores;
    private readonly TexelPages _pages;
    private readonly PipelineLayout _layout;
    private readonly TileResolve _tiles;
    private readonly Dictionary<LightmapRect, int> _charts = [];
    private LivePipeline? _pipeline;
    private bool _pipelineConservative;

    public TexelDraw(VulkanContext context, SceneStores stores, TexelPages pages)
    {
        _context = context;
        _stores = stores;
        _pages = pages;
        _layout = context.CreatePipelineLayout(
            [], new PushConstantRange(ShaderStageFlags.VertexBit, 0, (uint)sizeof(TexelConstants)));
        _tiles = new TileResolve(context);
    }

    /// <summary>
    /// Draws every page's static draws into it, with conservative rasterization when <paramref name="conservative"/>,
    /// and through multisampled tiles with their sets from <paramref name="pool"/> otherwise.
    /// </summary>
    public void DrawPages(CommandBuffer cmd, LightmapLayout layout, ReadOnlySpan<DrawItem> draws, DescriptorPool pool, bool conservative)
    {
        EnsurePipeline(conservative);
        var onPage = ByPage(layout, draws);
        for (int page = 0; page < layout.Pages; page++)
        {
            if (conservative)
            {
                DrawConservative(cmd, _pages[page], onPage[page]);
            }
            else
            {
                DrawMultisampled(cmd, _pages[page], onPage[page], pool);
            }
        }
    }

    // Each page's static draws and their rectangles. Each rectangle gets a number from 1, drawn into its texels for the
    // filter. Draws that share a rectangle share it.
    private List<(DrawItem Draw, LightmapRect Rect)>[] ByPage(LightmapLayout layout, ReadOnlySpan<DrawItem> draws)
    {
        var onPage = new List<(DrawItem Draw, LightmapRect Rect)>[layout.Pages];
        for (int i = 0; i < onPage.Length; i++)
        {
            onPage[i] = [];
        }

        _charts.Clear();
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0 && draw.IndexCount != 0 && layout.TryGet(draw.BakeId, out var rect))
            {
                onPage[rect.Page].Add((draw, rect));
                _charts.TryAdd(rect, _charts.Count + 1);
            }
        }

        return onPage;
    }

    /// <summary>Draws a page's draws straight into it: every texel a triangle touches is covered.</summary>
    private void DrawConservative(CommandBuffer cmd, PageTargets targets, List<(DrawItem Draw, LightmapRect Rect)> draws)
    {
        var api = _context.Api;
        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.None,
                PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);
        }

        var extent = new Extent2D(LightmapLayout.PageSize, LightmapLayout.PageSize);
        DrawInto(cmd, targets.Colors, extent, default, draws);

        foreach (var target in targets.Colors)
        {
            api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.General,
                PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
        }
    }

    /// <summary>
    /// Draws a page tile by tile into multisampled targets, then resolves each tile into the page, each texel taking a
    /// covered sample, so a triangle that misses a texel's centre but covers any sample still covers it.
    /// </summary>
    private void DrawMultisampled(
        CommandBuffer cmd, PageTargets targets, List<(DrawItem Draw, LightmapRect Rect)> draws, DescriptorPool pool)
    {
        var set = _tiles.Prepare(cmd, targets, pool);
        const int tiles = LightmapLayout.PageSize / BakeGpu.TileSize;
        var extent = new Extent2D(BakeGpu.TileSize, BakeGpu.TileSize);
        var tileDraws = new List<(DrawItem Draw, LightmapRect Rect)>();
        for (int tileY = 0; tileY < tiles; tileY++)
        {
            for (int tileX = 0; tileX < tiles; tileX++)
            {
                var origin = new Offset2D(tileX * BakeGpu.TileSize, tileY * BakeGpu.TileSize);
                TileResolve.CollectTouching(draws, origin, tileDraws);
                if (tileDraws.Count > 0)
                {
                    var colors = _tiles.BeginTile(cmd);
                    DrawInto(cmd, colors, extent, origin, tileDraws);
                    _tiles.ResolveTile(cmd, origin, set);
                }
            }
        }

        _tiles.Finish(cmd, targets);
    }

    /// <summary>
    /// Draws into the three colour targets given, cleared first. They cover <paramref name="extent"/> of the page from
    /// <paramref name="origin"/>; the viewport still spans the whole page, so a rectangle lands on the same texels
    /// whichever tile draws it.
    /// </summary>
    private void DrawInto(
        CommandBuffer cmd, RenderTarget[] targets, Extent2D extent, Offset2D origin, List<(DrawItem Draw, LightmapRect Rect)> draws)
    {
        var api = _context.Api;
        BeginRendering(cmd, targets, extent);

        var viewport = new Viewport(-origin.X, -origin.Y, LightmapLayout.PageSize, LightmapLayout.PageSize, 0, 1);
        var scissor = new Rect2D(default, extent);
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        api.CmdSetScissor(cmd, 0, 1, &scissor);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline!);

        foreach (var (draw, rect) in draws)
        {
            var mesh = _stores.MeshFor(draw.Mesh);
            var vertexBuffer = mesh.Vertices.Handle;
            ulong offset = 0;
            api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
            api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
            var constants = new TexelConstants
            {
                Model = draw.Transform,
                Rect = new Vector4(rect.Scale, rect.Offset.X, rect.Offset.Y),
                Chart = _charts[rect],
            };
            api.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(TexelConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, 1, draw.FirstIndex, 0, 0);
        }

        api.CmdEndRendering(cmd);
    }

    // Begins rendering into the three colour targets, cleared first, over the extent.
    private void BeginRendering(CommandBuffer cmd, RenderTarget[] targets, Extent2D extent)
    {
        var attachments = stackalloc RenderingAttachmentInfo[3];
        for (int i = 0; i < 3; i++)
        {
            attachments[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = targets[i].View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
            };
        }

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = 3,
            PColorAttachments = attachments,
        };
        _context.Api.CmdBeginRendering(cmd, &rendering);
    }

    private void EnsurePipeline(bool conservative)
    {
        if (_pipeline is not null && _pipelineConservative == conservative)
        {
            return;
        }

        _pipeline?.Destroy();

        // Only position, normal and the second UVs: attributes 0, 1 and 6 of the scene's vertex layout.
        var all = SceneDraws.VertexAttributes;
        VertexInputAttributeDescription[] attributes = [all[0], all[1], all[6]];
        Format[] formats = [BakeGpu.PositionFormat, BakeGpu.NormalFormat, BakeGpu.NormalFormat];
        _pipeline = PipelineFactory.Create(_context, new PipelineDescription
        {
            VertexShader = "bake_texels.vert",
            FragmentShader = "bake_texels.frag",
            Layout = _layout,
            ColorFormats = formats,
            VertexBindings = SceneDraws.VertexBindings,
            VertexAttributes = attributes,
            CullMode = CullModeFlags.None,
            Samples = conservative ? SampleCountFlags.Count1Bit : _tiles.SampleCount(),
            Conservative = conservative,
        });
        _pipelineConservative = conservative;
    }

    public void Dispose()
    {
        _tiles.Dispose();
        _pipeline?.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }

    // Must match the push constants in bake_texels.vert.
    [StructLayout(LayoutKind.Sequential)]
    private struct TexelConstants
    {
        public Matrix4x4 Model;
        public Vector4 Rect;
        public float Chart;
    }
}
