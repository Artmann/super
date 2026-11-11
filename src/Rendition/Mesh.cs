using Rendition.Vulkan;

namespace Rendition;

internal sealed class Mesh(GpuBuffer vertices, GpuBuffer indices, uint indexCount) : IDisposable
{
    public GpuBuffer Vertices { get; } = vertices;
    public GpuBuffer Indices { get; } = indices;
    public uint IndexCount { get; } = indexCount;

    /// <summary>The mesh's surface area in its own units, found when it was made. See <see cref="LightmapLayout.SurfaceArea"/>.</summary>
    public float SurfaceArea { get; set; }

    /// <summary>The detail entries made for this mesh's parts, freed with it.</summary>
    public int[] Details { get; set; } = [];

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}
