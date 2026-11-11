using Rendition.Vulkan;

namespace Rendition;

internal sealed class Mesh(GpuBuffer vertices, GpuBuffer indices, uint indexCount) : IDisposable
{
    public GpuBuffer Vertices { get; } = vertices;
    public GpuBuffer Indices { get; } = indices;
    public uint IndexCount { get; } = indexCount;

    /// <summary>The running total of the mesh's triangle areas, found when it was made. See <see cref="LightmapLayout.CumulativeAreas"/>.</summary>
    public float[] CumulativeAreas { get; set; } = [0f];

    /// <summary>The detail entries made for this mesh's parts, freed with it.</summary>
    public int[] Details { get; set; } = [];

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}
