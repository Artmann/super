using Rendition.Vulkan;

namespace Rendition;

internal sealed class Mesh(GpuBuffer vertices, GpuBuffer indices, uint indexCount) : IDisposable
{
    public GpuBuffer Vertices { get; } = vertices;
    public GpuBuffer Indices { get; } = indices;
    public uint IndexCount { get; } = indexCount;

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}
