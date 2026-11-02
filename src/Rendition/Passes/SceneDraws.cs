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

/// <summary>The pipelines of one shader, for one-sided and two-sided materials.</summary>
internal readonly record struct PipelineVariants(Pipeline Culled, Pipeline TwoSided);

/// <summary>Issues the draws for a pass that renders scene geometry.</summary>
internal static unsafe class SceneDraws
{
    public const ShaderStageFlags PushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>Must match the Draw block in Shaders/material.glsl and the vertex shaders.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawConstants
    {
        public Matrix4x4 Model;

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

    /// <summary>The push constants that draw one item.</summary>
    public static DrawConstants Constants(in DrawItem draw, int cascade) => new()
    {
        Model = draw.Transform,
        Tint = new Vector4(draw.Tint ?? Vector3.One, 1f),
        Ghost = draw.Ghost is { } ghost ? new Vector4(ghost.Color, ghost.Opacity) : Vector4.Zero,
        Material = draw.Material.Index,
        Swim = draw.Swim,
        Cascade = cascade,
        FirstBone = draw.Bones.Count > 0 ? draw.Bones.First : -1,
    };

    /// <summary>
    /// Draws the items that belong in <paramref name="pass"/>, in the given order. A pass with
    /// one entry in <paramref name="pipelines"/> draws everything with it. Otherwise each
    /// material picks its surface shader. Materials without cut-out holes use
    /// <paramref name="solid"/> when it's given.
    /// </summary>
    public static void Record(
        Vk api,
        CommandBuffer cmd,
        PipelineLayout layout,
        List<PipelineVariants> pipelines,
        ResourceTable resources,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh?> meshes,
        ScenePass pass,
        PipelineVariants? solid = null,
        int cascade = 0)
    {
        var boundPipeline = default(Pipeline);
        int boundMesh = -1;
        foreach (ref readonly var draw in draws)
        {
            var flags = resources.GetFlags(draw.Material);
            if (!Belongs(draw, flags, pass))
            {
                continue;
            }

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

            var constants = Constants(draw, cascade);
            api.CmdPushConstants(cmd, layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, 1, draw.FirstIndex, 0, 0);
        }
    }
}
