using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>The pipelines of one shader, for one-sided and two-sided materials.</summary>
internal readonly record struct PipelineVariants(Pipeline Culled, Pipeline TwoSided);

/// <summary>Issues the draws for a pass that renders scene geometry.</summary>
internal static unsafe class SceneDraws
{
    public const ShaderStageFlags PushStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>Must match the Draw block in Shaders/material.glsl and the vertex shaders.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 Model;
        public int Material;
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
    ];

    /// <summary>
    /// Draws either the opaque or the transparent items, in the given order. A pass with
    /// one entry in <paramref name="pipelines"/> draws everything with it. Otherwise each
    /// material picks its surface shader.
    /// </summary>
    public static void Record(
        Vk api,
        CommandBuffer cmd,
        PipelineLayout layout,
        List<PipelineVariants> pipelines,
        ResourceTable resources,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh> meshes,
        bool transparent = false)
    {
        var boundPipeline = default(Pipeline);
        int boundMesh = -1;
        foreach (ref readonly var draw in draws)
        {
            var flags = resources.GetFlags(draw.Material);
            if ((flags & MaterialFlags.Transparent) != 0 != transparent)
            {
                continue;
            }

            var variants = pipelines[pipelines.Count > 1 ? resources.GetShader(draw.Material) : 0];
            var pipeline = (flags & MaterialFlags.TwoSided) != 0 ? variants.TwoSided : variants.Culled;
            if (pipeline.Handle != boundPipeline.Handle)
            {
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
                boundPipeline = pipeline;
            }

            if (draw.Mesh.Index != boundMesh)
            {
                var mesh = meshes[draw.Mesh.Index];
                var vertexBuffer = mesh.Vertices.Handle;
                ulong offset = 0;
                api.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
                api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint32);
                boundMesh = draw.Mesh.Index;
            }

            var constants = new DrawConstants { Model = draw.Transform, Material = draw.Material.Index };
            api.CmdPushConstants(cmd, layout, PushStages, 0, (uint)sizeof(DrawConstants), &constants);
            api.CmdDrawIndexed(cmd, draw.IndexCount, 1, draw.FirstIndex, 0, 0);
        }
    }
}
