using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// A mesh's vertex and index buffers. The GPU layer knows them as <see cref="VertexBuffer"/> and
/// <see cref="IndexBuffer"/>, adopted when the mesh is made and let go of when it is disposed.
/// </summary>
internal sealed class Mesh : IDisposable
{
    private readonly VulkanGpu _gpu;

    public Mesh(VulkanGpu gpu, GpuBuffer vertices, GpuBuffer indices, uint indexCount)
    {
        _gpu = gpu;
        Vertices = vertices;
        Indices = indices;
        IndexCount = indexCount;
        VertexBuffer = gpu.Adopt(vertices.Handle, (long)vertices.Size);
        IndexBuffer = gpu.Adopt(indices.Handle, (long)indices.Size);
    }

    public GpuBuffer Vertices { get; }
    public GpuBuffer Indices { get; }
    public uint IndexCount { get; }

    /// <summary>The vertices as the GPU layer knows them.</summary>
    public BufferId VertexBuffer { get; }

    /// <summary>The indices as the GPU layer knows them.</summary>
    public BufferId IndexBuffer { get; }

    /// <summary>The running total of the mesh's triangle areas, found when it was made. See <see cref="LightmapLayout.CumulativeAreas"/>.</summary>
    public float[] CumulativeAreas { get; set; } = [0f];

    /// <summary>The detail entries made for this mesh's parts, freed with it.</summary>
    public int[] Details { get; set; } = [];

    public void Dispose()
    {
        _gpu.Release(VertexBuffer);
        _gpu.Release(IndexBuffer);
        Vertices.Dispose();
        Indices.Dispose();
    }
}
