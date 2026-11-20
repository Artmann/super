using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Which pass a scene draw is recorded for, which decides what it draws.</summary>
internal enum ScenePass
{
    /// <summary>Solid things, into the G-buffer.</summary>
    Opaque,

    /// <summary>Solid things that take decals, into the G-buffer before the decals are laid on it.</summary>
    OpaqueTakingDecals,

    /// <summary>Solid things that don't take decals, into the G-buffer after them, so they cover what the decals laid.</summary>
    OpaqueAfterDecals,

    /// <summary>Glass and see-through ghosts, shaded forward over the lit picture.</summary>
    Transparent,

    /// <summary>Solid things that cast shadows, into a shadow map.</summary>
    Shadow,
}

/// <summary>Draws that go to the GPU as one instanced draw: <see cref="Count"/> entries of an order, from <see cref="First"/>.</summary>
internal readonly record struct DrawBatch(int First, int Count);

/// <summary>The pipelines of one shader, for one-sided and two-sided materials.</summary>
internal readonly record struct PipelineVariants(LivePipeline Culled, LivePipeline TwoSided);

/// <summary>
/// Draws depth ahead of shading, so a pixel is shaded once, by the surface that shows, however many lie behind it.
/// <paramref name="depth"/> holds the one pair of pipelines that write depth alone, and <paramref name="shown"/> each
/// surface shader's pipelines that shade only at the depth already drawn.
/// </summary>
internal sealed class DepthFirst(List<PipelineVariants> depth, List<PipelineVariants> shown)
{
    private DetailView _view;
    private float _viewPixels;

    /// <summary>Sets the view the next draws are for, which has <paramref name="viewPixels"/> pixels in its picture.</summary>
    public void Aim(in DetailView view, float viewPixels)
    {
        _view = view;
        _viewPixels = viewPixels;
    }

    /// <summary>
    /// How many pixels a triangle has to cover, on average over its draw, for drawing the draw twice to pay. The
    /// first time costs by the triangle and saves by the pixel: walls and floors gain from it, and a shoal of small
    /// fish, a few pixels a triangle, only loses.
    /// </summary>
    public const float PixelsPerTriangle = 8f;

    public List<PipelineVariants> Depth { get; } = depth;

    public List<PipelineVariants> Shown { get; } = shown;

    /// <summary>
    /// Whether to draw a draw's depth first: its material has no holes, whose depth only its own shader knows, and
    /// its triangles are large enough on screen.
    /// </summary>
    public bool Pays(in DrawItem draw, ResourceTable resources, List<PartDetail> details)
    {
        if (SceneDraws.HasHoles(draw, resources))
        {
            return false;
        }

        return Pays(DrawSelection.PixelsCovered(_view, draw, details), _viewPixels, draw.IndexCount);
    }

    /// <summary>
    /// Whether a draw of <paramref name="indexCount"/> indices that covers <paramref name="covered"/> pixels, in a
    /// picture of <paramref name="viewPixels"/>, has triangles large enough. It can't cover more than the picture.
    /// </summary>
    public static bool Pays(float covered, float viewPixels, uint indexCount) =>
        MathF.Min(covered, viewPixels) >= PixelsPerTriangle * (indexCount / 3);
}

/// <summary>Issues the draws for a pass that renders scene geometry.</summary>
internal static unsafe class SceneDraws
{
    public const ShaderStageFlags PushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>
    /// Must match the Draw block in Shaders/material.glsl and the vertex shaders. It's the
    /// same for every copy in an instanced draw. Each copy's matrix is in the frame's
    /// <see cref="InstanceBuffer"/>.
    /// </summary>
    // Rounded up to 64 bytes, a multiple of a vec4, so what a pass adds after it, like the probe capture's vec4, starts
    // where the shader's block puts it.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct DrawConstants
    {
        /// <summary>rgb multiplies the material's colour. For glass, w is 1 when it sees the scene through the copy and 0 when it only blends over it. Comes before the scalars to keep std430 alignment.</summary>
        public Vector4 Tint;

        /// <summary>rgb is the ghost colour and w its opacity, or 0 when the draw is no ghost.</summary>
        public Vector4 Ghost;

        public int Material;

        /// <summary>Which sun cascade the shadow pass is drawing, or from <see cref="ShadowPass.MaxCascades"/> on, which tile of the atlas. Other passes ignore it.</summary>
        public int Cascade;

        /// <summary>Where this draw's skin matrices start in the frame's bone buffer, or -1 for no skin.</summary>
        public int FirstBone;

        /// <summary>The lowest height of the draw's part, in its own space, where the wind sways nothing. See wind.glsl.</summary>
        public float WindBase;

        /// <summary>The material's <see cref="Rendition.Material.Wind"/>, or 0 where nothing sways.</summary>
        public float WindSway;
    }

    public static PushConstantRange PushRange => new(PushStages, 0, (uint)sizeof(DrawConstants));

    public static readonly VertexInputBindingDescription[] VertexBindings =
    [
        new(0, (uint)sizeof(Vertex), VertexInputRate.Vertex),
    ];

    public static readonly VertexInputAttributeDescription[] VertexAttributes =
    [
        new(0, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))),
        new(1, 0, Format.R32G32B32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal))),
        new(2, 0, Format.R32G32B32A32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Tangent))),
        new(3, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv))),
        new(4, 0, Format.R8G8B8A8Uint, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Joints))),
        new(5, 0, Format.R8G8B8A8Unorm, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Weights))),
        new(6, 0, Format.R32G32Sfloat, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv2))),
    ];

    /// <summary>
    /// Whether a draw belongs in a pass. Glass goes with the transparent pass and casts a
    /// lighter shadow. A ghost goes where its opacity says, whatever its material, and casts no shadow.
    /// </summary>
    public static bool Belongs(in DrawItem draw, MaterialFlags flags, ScenePass pass)
    {
        bool transparent = draw.Ghost is { } ghost ? ghost.SeeThrough : (flags & MaterialFlags.Transparent) != 0;
        return pass switch
        {
            ScenePass.Opaque => !transparent,
            ScenePass.OpaqueTakingDecals => !transparent && TakesDecals(draw, flags),
            ScenePass.OpaqueAfterDecals => !transparent && !TakesDecals(draw, flags),
            ScenePass.Transparent => transparent,
            _ => draw.Ghost is null,
        };
    }

    /// <summary>Whether any of the draws belongs in the pass.</summary>
    public static bool AnyIn(ReadOnlySpan<DrawItem> draws, ResourceTable resources, ScenePass pass)
    {
        foreach (ref readonly var draw in draws)
        {
            if (Belongs(draw, resources.Materials[draw.Material].Flags, pass))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether decals lay on a draw: not on a skinned one, a character, nor one whose material has <see cref="MaterialFlags.NoDecals"/>.</summary>
    public static bool TakesDecals(in DrawItem draw, MaterialFlags flags) =>
        draw.Bones.Count == 0 && (flags & MaterialFlags.NoDecals) == 0;

    /// <summary>
    /// Groups draws that differ only in what each copy has of its own, where it stands and how it swims, so each
    /// group is one instanced draw. <paramref name="order"/> holds the indices of the draws to make, and unless
    /// <paramref name="keepOrder"/> it's sorted so that copies far apart in the list meet,
    /// as copies of a model with several parts are. Kept order, for glass, groups only
    /// neighbours. A skinned draw is always alone, since its skin is its own.
    /// </summary>
    public static void Batch(ReadOnlySpan<DrawItem> draws, Span<int> order, bool keepOrder, List<DrawBatch> batches)
    {
        if (!keepOrder)
        {
            DrawOrder.Group(draws, order, batches);
            return;
        }

        batches.Clear();
        int first = 0;
        for (int i = 1; i <= order.Length; i++)
        {
            if (i == order.Length || !DrawOrder.SameLook(draws[order[first]], draws[order[i]]))
            {
                batches.Add(new DrawBatch(first, i - first));
                first = i;
            }
        }
    }

    /// <summary>
    /// The push constants that draw one item, or every copy in its batch. <paramref name="windSway"/> is the
    /// material's share of the wind, and <paramref name="windBase"/> the lowest height of the draw's part.
    /// </summary>
    public static DrawConstants Constants(in DrawItem draw, int cascade, float windSway = 0f, float windBase = 0f) => new()
    {
        Tint = new Vector4(draw.Tint ?? Vector3.One, 1f),
        Ghost = draw.Ghost is { } ghost ? new Vector4(ghost.Color, ghost.Opacity) : Vector4.Zero,
        Material = draw.Material.Index,
        Cascade = cascade,
        FirstBone = draw.Bones.Count > 0 ? draw.Bones.First : -1,
        WindBase = windSway > 0f ? windBase : 0f,
        WindSway = windSway,
    };

    /// <summary>
    /// The push constants for a draw, with the wind its material and part give it: its part's lowest height comes
    /// from <paramref name="details"/>, and its material's share of the wind from <paramref name="swayOf"/>.
    /// </summary>
    public static DrawConstants Constants(in DrawItem draw, int cascade, Func<MaterialHandle, float> swayOf, List<PartDetail> details)
    {
        float sway = swayOf(draw.Material);
        float windBase = sway > 0f && draw.Detail.Index > 0 && draw.Detail.Index < details.Count ? details[draw.Detail.Index].MinY : 0f;
        return Constants(draw, cascade, sway, windBase);
    }

    /// <summary>
    /// Draws the items of <paramref name="geometry"/> that belong in the options' pass, with copies that look the
    /// same as one instanced draw. Solid things and shadows are drawn in any order, which
    /// brings copies together. <paramref name="pipelines"/> says which pipeline each material draws with; materials
    /// without cut-out holes, that aren't dithered and aren't glass, use its solid pipelines when it has them. Each copy's
    /// matrix goes into the geometry's instance buffer. Materials that sway in the wind sway from the lowest point of
    /// their part, which the geometry's details hold, unless <paramref name="options"/> keeps everything still, and
    /// with its depth first the batches that pays for are drawn twice.
    /// </summary>
    public static void Record(in SceneTarget target, in ScenePipelines pipelines, in SceneGeometry geometry, in SceneDrawOptions options)
    {
        var draws = geometry.Draws;
        var resources = pipelines.Resources;
        int[] pooled = ArrayPool<int>.Shared.Rent(draws.Length);
        var order = pooled.AsSpan(0, Belonging(draws, resources, options.Pass, pooled));
        var batches = _batches ??= [];
        Batch(draws, order, keepOrder: options.Pass == ScenePass.Transparent, batches);

        // Each batch's copies go into the instance buffer once, however many times the batch is drawn.
        // They go in drawing order, so a batch's copies start as far into them as the batch is into the order. A
        // shadow's shaders read only where a copy stands, so that's all a shadow writes.
        int firstCopy = options.Pass == ScenePass.Shadow
            ? AddPlacings(draws, order, geometry.Instances)
            : AddCopies(draws, order, geometry.Instances);

        var ready = new ReadyBatches
        {
            Draws = draws, Order = order, Batches = batches, FirstCopy = firstCopy, Resources = resources, Details = geometry.Details,
            Cascade = options.Cascade, Still = options.Still, To = new Recorder(target, geometry.Meshes),
        };

        // With depth first there are two rounds: the depth of the batches it pays for, then every batch shaded.
        var depthFirst = options.DepthFirst;
        bool[]? ahead = depthFirst is null ? null : ready.IssueDepth(depthFirst);
        for (int i = 0; i < batches.Count; i++)
        {
            bool shown = ahead is not null && ahead[i];
            ready.Issue(i, PipelineFor(ready.First(i), pipelines.Shaded, resources, shown ? depthFirst!.Shown : pipelines.Solids));
        }

        if (ahead is not null)
        {
            ArrayPool<bool>.Shared.Return(ahead);
        }

        ArrayPool<int>.Shared.Return(pooled);
    }

    // Writes the indices of the draws that belong in a pass into `order`, and returns how many there are.
    private static int Belonging(ReadOnlySpan<DrawItem> draws, ResourceTable resources, ScenePass pass, int[] order)
    {
        int count = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            if (Belongs(draws[i], resources.Materials[draws[i].Material].Flags, pass))
            {
                order[count++] = i;
            }
        }

        return count;
    }

    // A pass's batches with their copies already in the instance buffer, so each can be drawn more than once.
    private ref struct ReadyBatches
    {
        public ReadOnlySpan<DrawItem> Draws;
        public ReadOnlySpan<int> Order;
        public List<DrawBatch> Batches;
        public int FirstCopy;
        public ResourceTable Resources;
        public List<PartDetail> Details;
        public int Cascade;
        public bool Still;
        public Recorder To;

        /// <summary>The first draw of a batch, which every copy in it looks like.</summary>
        public readonly ref readonly DrawItem First(int batch) => ref Draws[Order[Batches[batch].First]];

        public void Issue(int batch, Pipeline pipeline)
        {
            ref readonly var draw = ref First(batch);
            var constants = Still ? Constants(draw, Cascade) : Constants(draw, Cascade, Resources.Materials.SwayOf, Details);
            To.Issue(pipeline, draw, Batches[batch].Count, FirstCopy + Batches[batch].First, constants);
        }

        /// <summary>
        /// Draws the depth of the batches <paramref name="depthFirst"/> pays for, and returns which they were, in an
        /// array from the shared pool.
        /// </summary>
        public bool[] IssueDepth(DepthFirst depthFirst)
        {
            bool[] ahead = ArrayPool<bool>.Shared.Rent(Batches.Count);
            for (int i = 0; i < Batches.Count; i++)
            {
                ref readonly var draw = ref First(i);
                ahead[i] = depthFirst.Pays(draw, Resources, Details);
                if (ahead[i])
                {
                    Issue(i, PipelineFor(draw, depthFirst.Depth, Resources, null));
                }
            }

            return ahead;
        }
    }

    private static Pipeline PipelineFor(in DrawItem draw, List<PipelineVariants> pipelines, ResourceTable resources, List<PipelineVariants>? solids)
    {
        var facts = resources.Materials[draw.Material];
        var flags = facts.Flags;
        var choices = solids is not null && !HasHoles(draw, resources) ? solids : pipelines;
        var variants = choices[choices.Count > 1 ? facts.Shader : 0];
        return (flags & MaterialFlags.TwoSided) != 0 ? variants.TwoSided : variants.Culled;
    }

    // Writes each copy's own matrix and swim into the instance buffer, and returns where the first went.
    private static int AddCopies(ReadOnlySpan<DrawItem> draws, ReadOnlySpan<int> copies, InstanceBuffer instances)
    {
        int first = instances.Count;
        foreach (int index in copies)
        {
            instances.Add(draws[index], index);
        }

        return first;
    }

    // Writes only where each copy stands and how it swims, and returns where the first went.
    private static int AddPlacings(ReadOnlySpan<DrawItem> draws, ReadOnlySpan<int> copies, InstanceBuffer instances)
    {
        int first = instances.Count;
        foreach (int index in copies)
        {
            instances.AddPlacing(draws[index]);
        }

        return first;
    }

    /// <summary>
    /// Whether a material leaves some of its pixels out, by a cutoff, a dithered fade or glass's dithered shadow, so
    /// its fragment shader has to run before a pixel's depth is known. The rest draw with the solid pipelines.
    /// </summary>
    public static bool HasHoles(bool cutouts, MaterialFlags flags) =>
        cutouts || (flags & (MaterialFlags.Transparent | MaterialFlags.Dithered)) != 0;

    /// <summary>
    /// Whether a draw leaves some of its pixels out: its material does, or it's fading, out with distance or into a
    /// finer version of itself, which dithers it.
    /// </summary>
    public static bool HasHoles(in DrawItem draw, ResourceTable resources) =>
        draw.Dither != 0f || HasHoles(resources.Materials[draw.Material].Cutouts, resources.Materials[draw.Material].Flags);

    /// <summary>What a command buffer has bound, so the same pipeline or mesh isn't bound twice in a row.</summary>
    public struct Bound()
    {
        public Pipeline Pipeline;
        public int Mesh = -1;
    }

    /// <summary>
    /// Draws <paramref name="copies"/>, which all look like the first, as one instanced
    /// draw with <paramref name="constants"/>.
    /// </summary>
    public static void Draw(
        in SceneTarget target, in ScenePipelines pipelines, in SceneCopies copies, DrawConstants constants, ref Bound bound)
    {
        ref readonly var draw = ref copies.Draws[copies.Indices[0]];
        var to = new Recorder(target, copies.Meshes) { Bound = bound };
        var pipeline = PipelineFor(draw, pipelines.Shaded, pipelines.Resources, pipelines.Solids);
        to.Issue(pipeline, draw, copies.Indices.Length, AddCopies(copies.Draws, copies.Indices, copies.Instances), constants);
        bound = to.Bound;
    }

    // Issues instanced draws into a command buffer, binding a pipeline or a mesh only when it changes.
    private struct Recorder(SceneTarget target, List<Mesh?> meshes)
    {
        public Bound Bound = new();

        /// <summary>
        /// One instanced draw of <paramref name="copies"/> copies of a draw, whose own data starts at
        /// <paramref name="firstInstance"/> in the instance buffer.
        /// </summary>
        public void Issue(Pipeline pipeline, in DrawItem draw, int copies, int firstInstance, DrawConstants constants)
        {
            var (api, cmd, layout) = target;
            if (pipeline.Handle != Bound.Pipeline.Handle)
            {
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                Bound.Pipeline = pipeline;
            }

            if (draw.Mesh.Index != Bound.Mesh)
            {
                var mesh = meshes[draw.Mesh.Index]!;
                var vertexBuffer = mesh.Vertices.Handle;
                ulong offset = 0;
                api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
                api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
                Bound.Mesh = draw.Mesh.Index;
            }

            api.CmdPushConstants(cmd, layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, (uint)copies, draw.FirstIndex, 0, (uint)firstInstance);
            DrawCallCount.Add();
        }
    }

    [ThreadStatic]
    private static List<DrawBatch>? _batches;
}

/// <summary>
/// Counts the scene's draw calls, which are recorded on several threads at once. A frame's draw calls are the
/// difference in <see cref="Issued"/> across its recording.
/// </summary>
internal static class DrawCallCount
{
    private static long _issued;

    /// <summary>Every draw call issued so far, by every renderer in the process.</summary>
    public static long Issued => Interlocked.Read(ref _issued);

    public static void Add() => Interlocked.Increment(ref _issued);
}
