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
        /// <summary>rgb multiplies the material's colour; w is unused. Comes before the scalars to keep std430 alignment.</summary>
        public Vector4 Tint;

        /// <summary>rgb is the ghost colour and w its opacity, or 0 when the draw is no ghost.</summary>
        public Vector4 Ghost;

        public int Material;
        public SwimMotion Swim;

        /// <summary>Which shadow cascade the shadow pass is drawing. Other passes ignore it.</summary>
        public int Cascade;

        /// <summary>Where this draw's skin matrices start in the frame's bone buffer, or -1 for no skin.</summary>
        public int FirstBone;
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
    ];

    /// <summary>
    /// Whether a draw belongs in a pass. Glass goes with the transparent pass and casts no
    /// shadow. A ghost goes where its opacity says, whatever its material, and casts no shadow.
    /// </summary>
    public static bool Belongs(in DrawItem draw, MaterialFlags flags, ScenePass pass)
    {
        bool transparent = draw.Ghost is { } ghost ? ghost.SeeThrough : (flags & MaterialFlags.Transparent) != 0;
        return pass switch
        {
            ScenePass.Opaque => !transparent,
            ScenePass.Transparent => transparent,
            _ => !transparent && draw.Ghost is null,
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

    /// <summary>The push constants that draw one item, or every copy in its batch.</summary>
    public static DrawConstants Constants(in DrawItem draw, int cascade) => new()
    {
        Tint = new Vector4(draw.Tint ?? Vector3.One, 1f),
        Ghost = draw.Ghost is { } ghost ? new Vector4(ghost.Color, ghost.Opacity) : Vector4.Zero,
        Material = draw.Material.Index,
        Swim = draw.Swim,
        Cascade = cascade,
        FirstBone = draw.Bones.Count > 0 ? draw.Bones.First : -1,
    };

    /// <summary>
    /// Draws the items that belong in <paramref name="pass"/>, with copies that look the
    /// same as one instanced draw. Solid things and shadows are drawn in any order, which
    /// brings copies together. Glass keeps the given order. A pass with one entry in
    /// <paramref name="pipelines"/> draws everything with it. Otherwise each material picks
    /// its surface shader. Materials without cut-out holes use <paramref name="solid"/>
    /// when it's given. Each copy's matrix goes into <paramref name="instances"/>.
    /// </summary>
    public static void Record(
        Vk api,
        CommandBuffer cmd,
        PipelineLayout layout,
        List<PipelineVariants> pipelines,
        ResourceTable resources,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh?> meshes,
        InstanceBuffer instances,
        ScenePass pass,
        PipelineVariants? solid = null,
        int cascade = 0)
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

        var boundPipeline = default(Pipeline);
        int boundMesh = -1;
        foreach (var batch in batches)
        {
            ref readonly var draw = ref draws[order[batch.First]];
            var flags = resources.GetFlags(draw.Material);
            var variants = solid is { } solidVariants && !resources.HasCutouts(draw.Material)
                ? solidVariants
                : pipelines[pipelines.Count > 1 ? resources.GetShader(draw.Material) : 0];
            var pipeline = (flags & MaterialFlags.TwoSided) != 0 ? variants.TwoSided : variants.Culled;
            if (pipeline.Handle != boundPipeline.Handle)
            {
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
            }

            if (draw.Mesh.Index != boundMesh)
            {
                var mesh = meshes[draw.Mesh.Index]!;
                var vertexBuffer = mesh.Vertices.Handle;
                ulong offset = 0;
                api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
                api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
                boundMesh = draw.Mesh.Index;
            }

            int firstInstance = instances.Count;
            foreach (int index in order.Slice(batch.First, batch.Count))
            {
                instances.Add(draws[index].Transform);
            }

            var constants = Constants(draw, cascade);
            api.CmdPushConstants(cmd, layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, (uint)batch.Count, draw.FirstIndex, 0, (uint)firstInstance);
        }

        ArrayPool<int>.Shared.Return(pooled);
    }

    [ThreadStatic]
    private static List<DrawBatch>? _batches;
}
