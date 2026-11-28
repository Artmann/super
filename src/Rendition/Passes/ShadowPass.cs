using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Renders the scene's depth as seen from the shadow-casting light, once per cascade.
/// Each cascade covers three times the radius of the one before, in its own layer, and
/// is fitted to the next slice of what the camera sees.
/// Point and spot lights' shadows go into tiles of one shared atlas; see <see cref="LocalLights"/>.
/// </summary>
internal sealed unsafe class ShadowPass : IDisposable
{
    /// <summary>How many texels across each cascade's map is unless asked otherwise.</summary>
    public const int DefaultResolution = 2048;

    public const int MaxCascades = 4;
    public const float CascadeGrowth = 3f;

    /// <summary>The share of a map, at its edges, over which a cascade hands over to the next.</summary>
    public const float HandOver = 0.1f;

    /// <summary>The share of the furthest reach over which shadows fade out.</summary>
    public const float FadeShare = 0.15f;

    /// <summary>The furthest, in radii, that a caster can stretch a cascade towards the light.</summary>
    public const float MaxReach = 6f;

    private readonly VulkanContext _context;
    private readonly SceneList _list;
    private readonly ScenePipelines _scene;

    /// <summary>The cascades' maps, one layer each. Replaced when <see cref="Resize"/> changes the resolution.</summary>
    public RenderTarget Target { get; private set; }

    /// <summary>How many texels across each cascade's map is: 1024, 2048 or 4096.</summary>
    public int Resolution { get; private set; }

    private Extent2D Extent => new((uint)Resolution, (uint)Resolution);

    /// <summary>Every point and spot light's shadow, each in its own square of it.</summary>
    public readonly RenderTarget Atlas;

    public ShadowPass((VulkanContext Context, VulkanGpu Gpu) device, DescriptorSetLayout frameSetLayout, ResourceTable resources, int resolution)
    {
        var context = device.Context;
        _context = context;
        Resolution = CheckResolution(resolution);
        Target = CreateTarget();
        Atlas = new RenderTarget(
            context, GBuffer.DepthFormat, new Extent2D(LocalLights.AtlasSize, LocalLights.AtlasSize),
            ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);

        _list = new SceneList(device, resources, [frameSetLayout, resources.Layout], SceneDraws.PushRange);
        var (shaded, solid) = CreatePipelines(context, _list);
        _scene = new ScenePipelines([shaded], resources) { Solids = [solid] };
    }

    // The casters' pipelines, culled and two-sided: one that runs shadow.frag for materials with holes, and one with no
    // fragment shader for the rest.
    private static (PipelineVariants Shaded, PipelineVariants Solid) CreatePipelines(
        VulkanContext context, SceneList list)
    {
        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new(0, (uint)sizeof(Vertex), VertexInputRate.Vertex),
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new(0, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))),
            new(1, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv))),
            new(2, 0, Format.R8G8B8A8Uint, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Joints))),
            new(3, 0, Format.R8G8B8A8Unorm, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Weights))),
        ];
        var description = new PipelineDescription
        {
            VertexShader = "shadow.vert",
            FragmentShader = "shadow.frag",
            Layout = list.Layout,
            DepthFormat = GBuffer.DepthFormat,
            VertexBindings = bindings,
            VertexAttributes = attributes,
        };
        var culled = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        var shaded = list.Adopt(culled, PipelineFactory.Create(context, description));

        // Most materials have no holes, so nothing needs to run for their pixels.
        description.FragmentShader = null;
        description.TwoSided = false;
        var solidCulled = PipelineFactory.Create(context, description);
        description.TwoSided = true;
        return (shaded, list.Adopt(solidCulled, PipelineFactory.Create(context, description)));
    }

    /// <summary>The resolution given, when it's one a cascade's map can be: 1024, 2048 or 4096.</summary>
    public static int CheckResolution(int resolution) => resolution is 1024 or 2048 or 4096
        ? resolution
        : throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Shadow maps can be 1024, 2048 or 4096 texels across.");

    /// <summary>
    /// Replaces the cascades' maps with ones <paramref name="resolution"/> texels across. The caller
    /// waits for the GPU first and points every descriptor that reads <see cref="Target"/> at the new one.
    /// </summary>
    public void Resize(int resolution)
    {
        Resolution = CheckResolution(resolution);
        Target.Dispose();
        Target = CreateTarget();
    }

    private RenderTarget CreateTarget() => new(
        _context, GBuffer.DepthFormat, Extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit, MaxCascades);

    /// <summary>Draws one cascade into its layer of the map.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry casters, int cascade) =>
        RecordInto(cmd, frameSet, casters, (Target.LayerViews[cascade], Extent), new SceneDrawOptions(ScenePass.Shadow) { Cascade = cascade });

    /// <summary>
    /// Draws the casters into any depth view, already in the depth attachment layout, through the
    /// matrix the options' cascade picks in shadow.vert: -1 for the light probes' sun map.
    /// </summary>
    public void RecordInto(
        CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry casters, (ImageView View, Extent2D Extent) target, in SceneDrawOptions options)
    {
        var api = _context.Api;
        Begin(cmd, target, AttachmentLoadOp.Clear);
        var sets = stackalloc DescriptorSet[] { frameSet, _scene.Resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _list.Layout, 0, 2, sets, 0, null);
        SceneDraws.Record(_list.Begin(), _scene, casters, options);
        _list.Play(cmd);
        api.CmdEndRendering(cmd);
    }

    /// <summary>
    /// Begins drawing over what a cascade's layer already holds, for the grass's blades, drawn after its casters. The
    /// caller ends the rendering.
    /// </summary>
    public void BeginOver(CommandBuffer cmd, int cascade) => Begin(cmd, (Target.LayerViews[cascade], Extent), AttachmentLoadOp.Load);

    // Begins drawing into a depth view across all of it, cleared to the reversed-Z far plane or keeping what it holds.
    private void Begin(CommandBuffer cmd, (ImageView View, Extent2D Extent) target, AttachmentLoadOp load)
    {
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = load,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)),
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, target.Extent),
            LayerCount = 1,
            PDepthAttachment = &depthAttachment,
        };

        _context.Api.CmdBeginRendering(cmd, &rendering);
        _context.Api.SetFullViewport(cmd, target.Extent);
    }

    /// <summary>Which of a frame's draws belong to one atlas tile, and whether the tile is drawn again this frame.</summary>
    public readonly record struct TileDraws(int First, int Count, bool Redraw);

    /// <summary>Whether any tile is drawn again this frame, so the atlas needs a pass at all.</summary>
    public static bool AnyRedrawn(ReadOnlySpan<TileDraws> ranges)
    {
        foreach (ref readonly var range in ranges)
        {
            if (range.Redraw)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears and draws the tiles of the atlas that <paramref name="ranges"/> marks to redraw, in one pass, each
    /// through its own viewport, and leaves the rest of the atlas as it was. <paramref name="ranges"/> also says which
    /// of the <paramref name="casters"/> belong to each tile.
    /// </summary>
    public void RecordTiles(
        CommandBuffer cmd, DescriptorSet frameSet, ReadOnlySpan<ShadowView> tiles, ReadOnlySpan<TileDraws> ranges,
        in SceneGeometry casters)
    {
        var api = _context.Api;
        var extent = new Extent2D(LocalLights.AtlasSize, LocalLights.AtlasSize);
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = Atlas.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            PDepthAttachment = &depthAttachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        var sets = stackalloc DescriptorSet[] { frameSet, _scene.Resources.Set };
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _list.Layout, 0, 2, sets, 0, null);
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            var (first, count, redraw) = ranges[tile];
            if (!redraw)
            {
                continue;
            }

            ClearTile(cmd, tiles[tile]);
            SceneDraws.Record(_list.Begin(), _scene, casters.Slice(first, count),
                new SceneDrawOptions(ScenePass.Shadow) { Cascade = MaxCascades + tile });
            _list.Play(cmd);
        }

        api.CmdEndRendering(cmd);
    }

    /// <summary>Clears one tile to the far plane and points the viewport and scissor at it.</summary>
    private void ClearTile(CommandBuffer cmd, in ShadowView view)
    {
        var api = _context.Api;
        var clear = new ClearAttachment
        {
            AspectMask = ImageAspectFlags.DepthBit,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)), // Reversed-Z far plane.
        };
        var viewport = new Viewport(view.X, view.Y, view.Size, view.Size, 0, 1);
        var scissor = new Rect2D(new Offset2D(view.X, view.Y), new Extent2D((uint)view.Size, (uint)view.Size));
        var rect = new ClearRect(scissor, 0, 1);
        api.CmdClearAttachments(cmd, 1, &clear, 1, &rect);
        api.CmdSetViewport(cmd, 0, 1, &viewport);
        api.CmdSetScissor(cmd, 0, 1, &scissor);
    }

    public void Dispose()
    {
        _list.Dispose();
        Target.Dispose();
        Atlas.Dispose();
    }
}
