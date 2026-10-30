using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

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

    public static void Record(
        Vk api,
        CommandBuffer cmd,
        PipelineLayout layout,
        Pipeline culledPipeline,
        Pipeline twoSidedPipeline,
        ResourceTable resources,
        ReadOnlySpan<DrawItem> draws,
        List<Mesh> meshes)
    {
        var boundPipeline = default(Pipeline);
        int boundMesh = -1;
        foreach (ref readonly var draw in draws)
        {
            var pipeline = (resources.GetFlags(draw.Material) & MaterialFlags.TwoSided) != 0
                ? twoSidedPipeline
                : culledPipeline;
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
