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
            ScenePass.Transparent => transparent,
            _ => draw.Ghost is null,
        };
    }

    /// <summary>
    /// Groups draws that differ only in what each copy has of its own, where it stands and how it swims, so each
    /// group is one instanced draw. <paramref name="order"/> holds the indices of the draws to make, and unless
    /// <paramref name="keepOrder"/> it's sorted so that copies far apart in the list meet,
    /// as copies of a model with several parts are. Kept order, for glass, groups only
    /// neighbours. A skinned draw is always alone, since its skin is its own.
    /// </summary>
    public static void Batch(ReadOnlySpan<DrawItem> draws, Span<int> order, bool keepOrder, List<DrawBatch> batches)
    {
        batches.Clear();
        if (!keepOrder)
        {
            // One number a draw, sorted as plain numbers, which is several times faster than comparing the looks
            // field by field. The place in the list comes last in it, so draws that look the same stay in order.
            UInt128[] keys = ArrayPool<UInt128>.Shared.Rent(order.Length);
            for (int i = 0; i < order.Length; i++)
            {
                keys[i] = SortKey(draws[order[i]], i);
            }

            keys.AsSpan(0, order.Length).Sort(order);
            ArrayPool<UInt128>.Shared.Return(keys);
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
        && a.Ghost == b.Ghost;

    /// <summary>
    /// A number that sorts draws that look the same next to each other, in the order they came. The mesh is on top,
    /// so one mesh's parts stay together and it's bound once, then the part, so a mesh's parts are drawn in the
    /// order the mesh holds them, which is the order a model was made to be drawn in and costs the least overdraw.
    /// Then the rest of the look mixed into 32 bits, then the draw's <paramref name="place"/> in the list. Two looks
    /// can mix to the same bits, which only splits their batches where they interleave, since
    /// <see cref="SameLook"/> still decides what shares a draw. Nothing here changes from run to run, so neither
    /// does the order.
    /// </summary>
    private static UInt128 SortKey(in DrawItem draw, int place)
    {
        ulong look = Mix(draw.IndexCount, (uint)draw.Material.Index);
        if (draw.Tint is { } tint)
        {
            look = Mix(look, Bits(tint.X));
            look = Mix(look, Bits(tint.Y));
            look = Mix(look, Bits(tint.Z) + 1);
        }

        if (draw.Ghost is { } ghost)
        {
            look = Mix(look, Bits(ghost.Color.X));
            look = Mix(look, Bits(ghost.Color.Y));
            look = Mix(look, Bits(ghost.Color.Z));
            look = Mix(look, Bits(ghost.Opacity) + 1);
        }

        return new UInt128(
            ((ulong)(uint)draw.Mesh.Index << 32) | draw.FirstIndex,
            (look & 0xFFFFFFFF00000000ul) | (uint)place);
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    // Folds one more value into a running hash, by a multiply that carries every bit towards the top.
    private static ulong Mix(ulong hash, uint value) => (hash ^ value) * 0x9E3779B97F4A7C15ul;

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
    /// Draws the items that belong in <paramref name="pass"/>, with copies that look the
    /// same as one instanced draw. Solid things and shadows are drawn in any order, which
    /// brings copies together. A pass with one entry in <paramref name="pipelines"/> draws
    /// everything with it. Otherwise each material picks its surface shader. Materials
    /// without cut-out holes, that aren't dithered and aren't glass, use <paramref name="solids"/> when it's
    /// given, picked the same way. Each copy's matrix goes into <paramref name="instances"/>. Materials that sway in the wind
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
        List<PipelineVariants>? solids = null,
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
            Draw(api, cmd, layout, pipelines, resources, draws, order.Slice(batch.First, batch.Count), meshes, instances, constants, solids, ref bound);
        }

        ArrayPool<int>.Shared.Return(pooled);
    }

    /// <summary>
    /// Whether a material leaves some of its pixels out, by a cutoff, a dithered fade or glass's dithered shadow, so
    /// its fragment shader has to run before a pixel's depth is known. The rest draw with the solid pipelines.
    /// </summary>
    public static bool HasHoles(bool cutouts, MaterialFlags flags) =>
        cutouts || (flags & (MaterialFlags.Transparent | MaterialFlags.Dithered)) != 0;

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
        List<PipelineVariants>? solids,
        ref Bound bound)
    {
        ref readonly var draw = ref draws[copies[0]];
        var flags = resources.GetFlags(draw.Material);
        var choices = solids is not null && !HasHoles(resources.HasCutouts(draw.Material), flags) ? solids : pipelines;
        var variants = choices[choices.Count > 1 ? resources.GetShader(draw.Material) : 0];
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
