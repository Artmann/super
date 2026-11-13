using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class DeviceAddressTests(GpuFixture gpu)
{
    [Fact]
    public void A_mesh_built_for_tracing_has_addresses_and_no_validation_errors()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        var (vertices, indices) = TestMeshes.Quad();
        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        var mesh = gpu.Renderer.MeshFor(handle);
        Assert.NotEqual(0ul, mesh.Vertices.Address);
        Assert.NotEqual(0ul, mesh.Indices.Address);
        Assert.Equal(0, gpu.ValidationErrors);
        gpu.Renderer.DestroyMesh(handle);
    }

    [Fact]
    public void A_buffer_bigger_than_a_block_still_has_an_address()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        using var big = new GpuBuffer(gpu.Context, GpuMemory.BlockSize + 1024,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.DeviceLocalBit);
        Assert.NotEqual(0ul, big.Address);
        Assert.Equal(0, gpu.ValidationErrors);
    }
}
