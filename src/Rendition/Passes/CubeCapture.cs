using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// What drawing the scene round one spot into the six faces of a cube at once takes, shared by the light probes'
/// <see cref="ProbeCapturePass"/> and <see cref="ReflectionCapturePass"/>: picking the draws a spot sees, starting the
/// multiview pass, and drawing them at rest in batches with the pass's own pipelines, through the GPU layer.
/// </summary>
internal sealed unsafe class CubeCapture(
    VulkanContext context, SceneList list, ResourceTable resources, List<PipelineVariants> pipelines, Extent2D extent)
{
    private readonly List<DrawBatch> _batches = [];

    /// <summary>
    /// Adds the index of each draw a capture from <paramref name="position"/> sees to <paramref name="kept"/>: those that
    /// <see cref="ProbeCapturePass.Captures"/> and come within <paramref name="reach"/> of it. Returns how many triangles
    /// they hold.
    /// </summary>
    public long Keep(ReadOnlySpan<DrawItem> draws, List<PartDetail> details, Vector3 position, float reach, List<int> kept)
    {
        long triangles = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var draw = ref draws[i];
            if (!ProbeCapturePass.Captures(draw, resources.Materials[draw.Material].Flags))
            {
                continue;
            }

            if (DrawSelection.TryBounds(draw, details, out var center, out float radius)
                && Vector3.Distance(center, position) - radius > reach)
            {
                continue;
            }

            kept.Add(i);
            triangles += draw.IndexCount / 3;
        }

        return triangles;
    }

    /// <summary>
    /// Starts drawing the six faces at once, into six-layer views, clearing the colour to <paramref name="clear"/> and the
    /// depth to the far plane.
    /// </summary>
    public void Begin(CommandBuffer cmd, ImageView color, ImageView depth, Vector4 clear)
    {
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = color,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(clear.X, clear.Y, clear.Z, clear.W)),
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depth,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)), // Reversed-Z far plane.
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ViewMask = 0x3F,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };

        context.Api.CmdBeginRendering(cmd, &rendering);
        context.Api.SetFullViewport(cmd, extent);
    }

    /// <summary>
    /// Draws <paramref name="draws"/> at the indices in <paramref name="kept"/>, which it reorders, at rest and in
    /// batches, with whichever pipeline each material needs.
    /// </summary>
    public void Draw(CommandBuffer cmd, ReadOnlySpan<DrawItem> draws, Span<int> kept, List<Mesh?> meshes, InstanceBuffer instances)
    {
        var scene = new ScenePipelines(pipelines, resources);
        SceneDraws.Batch(draws, kept, keepOrder: false, _batches);
        var bound = new SceneDraws.Bound();
        var target = list.Begin();
        foreach (var batch in _batches)
        {
            // No wind: a probe sees things at rest.
            var constants = SceneDraws.Constants(draws[kept[batch.First]], 0);
            var copies = new SceneCopies(draws, kept.Slice(batch.First, batch.Count), meshes, instances);
            SceneDraws.Draw(target, scene, copies, constants, ref bound);
        }

        // Played into the multiview rendering begun by hand, which the layer's render passes can't begin yet.
        list.Play(cmd);
    }
}
