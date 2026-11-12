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

    /// <summary>Glass and see-through ghosts, shaded forward over the lit picture.</summary>
    Transparent,

    /// <summary>Solid things that cast shadows, into a shadow map.</summary>
    Shadow,
}

/// <summary>Draws that go to the GPU as one instanced draw: <see cref="Count"/> entries of an order, from <see cref="First"/>.</summary>
internal readonly record struct DrawBatch(int First, int Count);

/// <summary>The pipelines of one shader, for one-sided and two-sided materials.</summary>
internal readonly record struct PipelineVariants(Pipeline Culled, Pipeline TwoSided);

/// <summary>Issues the draws for a pass that renders scene geometry.</summary>
internal static unsafe class SceneDraws
{
    public const ShaderStageFlags PushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>
    /// Must match the Draw block in Shaders/material.glsl and the vertex shaders. It's the
    /// same for every copy in an instanced draw. Each copy's matrix is in the frame's
    /// <see cref="InstanceBuffer"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawConstants
    {
        /// <summary>rgb multiplies the material's colour. For glass, w is 1 when it sees the scene through the copy and 0 when it only blends over it. Comes before the scalars to keep std430 alignment.</summary>
        public Vector4 Tint;

        /// <summary>rgb is the ghost colour and w its opacity, or 0 when the draw is no ghost.</summary>
        public Vector4 Ghost;

        public int Material;
        public SwimMotion Swim;

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
            ScenePass.Transparent => transparent,
            _ => draw.Ghost is null,
        };
    }

    /// <summary>
    /// Groups draws that differ only in where they stand, so each group is one instanced
    /// draw. <paramref name="order"/> holds the indices of the draws to make, and unless
    /// <paramref name="keepOrder"/> it's sorted so that copies far apart in the list meet,
    /// as copies of a model with several parts are. Kept order, for glass, groups only
    /// neighbours. A skinned draw is always alone, since its skin is its own.
    /// </summary>
    public static void Batch(ReadOnlySpan<DrawItem> draws, Span<int> order, bool keepOrder, List<DrawBatch> batches)
    {
        batches.Clear();
        if (!keepOrder)
        {
            var looks = ArrayPool<Look>.Shared.Rent(order.Length);
            for (int i = 0; i < order.Length; i++)
            {
                looks[i] = new Look(draws[order[i]], order[i]);
            }

            looks.AsSpan(0, order.Length).Sort();
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = looks[i].Index;
            }

            ArrayPool<Look>.Shared.Return(looks);
        }

        int first = 0;
        for (int i = 1; i <= order.Length; i++)
        {
            if (i == order.Length || !SameLook(draws[order[first]], draws[order[i]]))
            {
                batches.Add(new DrawBatch(first, i - first));
                first = i;
            }
        }
    }

    private static bool SameLook(in DrawItem a, in DrawItem b) =>
        a.Bones.Count == 0 && b.Bones.Count == 0
        && a.Mesh == b.Mesh
        && a.FirstIndex == b.FirstIndex
        && a.IndexCount == b.IndexCount
        && a.Material == b.Material
        && a.Tint == b.Tint
        && a.Ghost == b.Ghost
        && a.Swim == b.Swim;

    /// <summary>What a draw looks like, to sort draws that look the same next to each other, and otherwise keep their order.</summary>
    private readonly struct Look(in DrawItem draw, int index) : IComparable<Look>
    {
        private readonly int _mesh = draw.Mesh.Index;
        private readonly uint _firstIndex = draw.FirstIndex;
        private readonly uint _indexCount = draw.IndexCount;
        private readonly int _material = draw.Material.Index;
        private readonly Vector4 _tint = draw.Tint is { } tint ? new Vector4(tint, 1f) : Vector4.Zero;
        private readonly Vector4 _ghost = draw.Ghost is { } ghost ? new Vector4(ghost.Color, ghost.Opacity) : Vector4.Zero;
        private readonly SwimMotion _swim = draw.Swim;

        public int Index { get; } = index;

        public int CompareTo(Look other)
        {
            int compared = _mesh.CompareTo(other._mesh);
            compared = compared != 0 ? compared : _firstIndex.CompareTo(other._firstIndex);
            compared = compared != 0 ? compared : _indexCount.CompareTo(other._indexCount);
            compared = compared != 0 ? compared : _material.CompareTo(other._material);
            compared = compared != 0 ? compared : Compare(_tint, other._tint);
            compared = compared != 0 ? compared : Compare(_ghost, other._ghost);
            compared = compared != 0 ? compared : _swim.Amplitude.CompareTo(other._swim.Amplitude);
            compared = compared != 0 ? compared : _swim.BodyLength.CompareTo(other._swim.BodyLength);
            compared = compared != 0 ? compared : _swim.Phase.CompareTo(other._swim.Phase);
            return compared != 0 ? compared : Index.CompareTo(other.Index);
        }

        private static int Compare(Vector4 a, Vector4 b)
        {
            int compared = a.X.CompareTo(b.X);
            compared = compared != 0 ? compared : a.Y.CompareTo(b.Y);
            compared = compared != 0 ? compared : a.Z.CompareTo(b.Z);
            return compared != 0 ? compared : a.W.CompareTo(b.W);
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
        Swim = draw.Swim,
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
    /// Draws the items that belong in <paramref name="pass"/>, with copies that look the
    /// same as one instanced draw. Solid things and shadows are drawn in any order, which
    /// brings copies together. A pass with one entry in <paramref name="pipelines"/> draws
    /// everything with it. Otherwise each material picks its surface shader. Materials
    /// without cut-out holes, and that aren't glass, use <paramref name="solid"/> when it's
    /// given. Each copy's matrix goes into <paramref name="instances"/>. Materials that sway in the wind
    /// sway from the lowest point of their part, which <paramref name="details"/> holds.
    /// With <paramref name="still"/> nothing sways, for views that must not follow the wind.
    /// </summary>
    public static void Record(
        Vk api,
        CommandBuffer cmd,
        PipelineLayout layout,
        List<PipelineVariants> pipelines,
        ResourceTable resources,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh?> meshes,
        List<PartDetail> details,
        InstanceBuffer instances,
        ScenePass pass,
        PipelineVariants? solid = null,
        int cascade = 0,
        bool still = false)
    {
        int[] pooled = ArrayPool<int>.Shared.Rent(draws.Length);
        int count = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            if (Belongs(draws[i], resources.GetFlags(draws[i].Material), pass))
            {
                pooled[count++] = i;
            }
        }

        var order = pooled.AsSpan(0, count);
        var batches = _batches ??= [];
        Batch(draws, order, keepOrder: pass == ScenePass.Transparent, batches);

        var bound = new Bound();
        foreach (var batch in batches)
        {
            var constants = still
                ? Constants(draws[order[batch.First]], cascade)
                : Constants(draws[order[batch.First]], cascade, resources.SwayOf, details);
            Draw(api, cmd, layout, pipelines, resources, draws, order.Slice(batch.First, batch.Count), meshes, instances, constants, solid, ref bound);
        }

        ArrayPool<int>.Shared.Return(pooled);
    }

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
        Vk api,
        CommandBuffer cmd,
        PipelineLayout layout,
        List<PipelineVariants> pipelines,
        ResourceTable resources,
        ReadOnlySpan<DrawItem> draws,
        ReadOnlySpan<int> copies,
        List<Mesh?> meshes,
        InstanceBuffer instances,
        DrawConstants constants,
        PipelineVariants? solid,
        ref Bound bound)
    {
        ref readonly var draw = ref draws[copies[0]];
        var flags = resources.GetFlags(draw.Material);
        bool needsFragments = resources.HasCutouts(draw.Material) || (flags & MaterialFlags.Transparent) != 0;
        var variants = solid is { } solidVariants && !needsFragments
            ? solidVariants
            : pipelines[pipelines.Count > 1 ? resources.GetShader(draw.Material) : 0];
        var pipeline = (flags & MaterialFlags.TwoSided) != 0 ? variants.TwoSided : variants.Culled;
        if (pipeline.Handle != bound.Pipeline.Handle)
        {
            api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            bound.Pipeline = pipeline;
        }

        if (draw.Mesh.Index != bound.Mesh)
        {
            var mesh = meshes[draw.Mesh.Index]!;
            var vertexBuffer = mesh.Vertices.Handle;
            ulong offset = 0;
            api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
            api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
            bound.Mesh = draw.Mesh.Index;
        }

        int firstInstance = instances.Count;
        foreach (int index in copies)
        {
            instances.Add(draws[index], index);
        }

        api.CmdPushConstants(cmd, layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
        api.CmdDrawIndexed(cmd, draw.IndexCount, (uint)copies.Length, draw.FirstIndex, 0, (uint)firstInstance);
    }

    [ThreadStatic]
    private static List<DrawBatch>? _batches;
}
