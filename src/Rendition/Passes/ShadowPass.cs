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
    private readonly ShadowTargets _targets;

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
        _targets = new ShadowTargets(device.Gpu);
    }

    // What the pass draws to, adopted into the GPU layer: each cascade's layer, the maps drawn through RecordInto, and
    // the atlas, with the pipeline that clears the atlas's tiles.
    private sealed class ShadowTargets(VulkanGpu gpu) : IDisposable
    {
        public readonly AdoptedTarget[] Cascades = [new(gpu), new(gpu), new(gpu), new(gpu)];
        public readonly AdoptedTargets Into = new(gpu);
        public readonly AdoptedTarget Atlas = new(gpu);

        private readonly PipelineId _clear = gpu.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = gpu.CreateShader("depth_clear.vert"),
            Targets = [],
            Depth = new DepthState(GpuFormat.Depth32Float, CompareFunction.Always, Write: true),
        });

        /// <summary>
        /// Points the viewport and scissor at one tile and clears it to the far plane, by drawing depth_clear.vert over
        /// it: WebGPU has no way to clear part of a target.
        /// </summary>
        public void ClearTile(CommandList commands, in ShadowView view)
        {
            commands.SetViewport(view.X, view.Y, view.Size, view.Size);
            commands.SetScissor(view.X, view.Y, view.Size, view.Size);
            commands.SetPipeline(_clear);
            commands.Draw(3, 1, 0, 0);
        }

        public void Dispose()
        {
            gpu.Destroy(_clear);
            foreach (var cascade in Cascades)
            {
                cascade.Dispose();
            }

            Into.Dispose();
            Atlas.Dispose();
        }
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

    /// <summary>
    /// Draws one cascade into its layer of the map. <see cref="SunShadows"/> moves the layers to drawing and back, so
    /// the layer is adopted as held.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry casters, int cascade) =>
        RecordInto(cmd, frameSet, casters, Cascade(cascade),
            new SceneDrawOptions(ScenePass.Shadow) { Cascade = cascade });

    /// <summary>
    /// Draws the casters into a depth target of its own, through the matrix the options' cascade picks in shadow.vert:
    /// -1 for the light probes' sun map. The layer moves the target to drawing and leaves it ready to read.
    /// </summary>
    public void RecordInto(
        CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry casters, (RenderTarget Target, Extent2D Extent) target, in SceneDrawOptions options) =>
        RecordInto(cmd, frameSet, casters, _targets.Into.For(target.Target, target.Extent).View, options);

    // Draws the casters into a depth view, cleared first to the reversed-Z far plane.
    private void RecordInto(CommandBuffer cmd, DescriptorSet frameSet, in SceneGeometry casters, ViewId target, in SceneDrawOptions options)
    {
        var into = _list.Begin();
        into.Commands.BeginRenderPass([], new DepthTarget(target, LoadOp.Clear, 0f, ReadOnly: false));
        SceneDraws.Record(into, _scene, casters, options);
        into.Commands.EndPass();
        Play(cmd, frameSet);
    }

    // Plays what was recorded with the frame's set bound by hand; the draws bind the materials themselves.
    private void Play(CommandBuffer cmd, DescriptorSet frameSet)
    {
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _list.Layout, 0, 1, &frameSet, 0, null);
        _list.Play(cmd);
    }

    /// <summary>
    /// The layer view of <paramref name="cascade"/> as the layer knows it, held, for the grass's blades to draw over
    /// what <see cref="Record"/> left in it.
    /// </summary>
    public ViewId Cascade(int cascade) => _targets.Cascades[cascade].ForHeld(Target.LayerViews[cascade], Target.Image, Extent);

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
        var into = _list.Begin();
        var commands = into.Commands;
        var extent = new Extent2D(LocalLights.AtlasSize, LocalLights.AtlasSize);
        // LightShadowTiles moves the atlas to drawing and back, so it is held.
        commands.BeginRenderPass([], new DepthTarget(_targets.Atlas.ForHeld(Atlas.View, Atlas.Image, extent), LoadOp.Load, 0f, ReadOnly: false));
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            var (first, count, redraw) = ranges[tile];
            if (!redraw)
            {
                continue;
            }

            _targets.ClearTile(commands, tiles[tile]);
            SceneDraws.Record(into, _scene, casters.Slice(first, count),
                new SceneDrawOptions(ScenePass.Shadow) { Cascade = MaxCascades + tile });
        }

        commands.EndPass();
        Play(cmd, frameSet);
    }

    public void Dispose()
    {
        _targets.Dispose();
        _list.Dispose();
        Target.Dispose();
        Atlas.Dispose();
    }
}
