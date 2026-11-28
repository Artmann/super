using Silk.NET.Vulkan;
using Rendition.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Rendition;

/// <summary>
/// One of the frames that can be in flight at once: its command buffer, its fence, and the buffers its
/// shaders read, with the descriptor set that binds them.
/// </summary>
internal struct FrameSlot
{
    public CommandPool CommandPool;
    public CommandBuffer CommandBuffer;
    public Fence InFlight;
    public GpuBuffer Uniforms;
    public GpuBuffer Bones;
    public InstanceBuffer Instances;
    public GpuBuffer Lights;
    public GpuBuffer ShadowTiles;
    public GpuBuffer Clusters;
    // The reflection probes the view sees, in the order they blend, as light_data.glsl reads them at binding 13.
    public GpuBuffer ReflectionProbes;
    // The frame's suns and placed chunks, as chunks.glsl reads them at binding 11. See GpuChunk.
    public GpuBuffer Chunks;
    public DescriptorSet Set;

    public readonly unsafe void Dispose(Vk api, Device device)
    {
        Uniforms.Dispose();
        Bones.Dispose();
        Instances.Dispose();
        Lights.Dispose();
        ShadowTiles.Dispose();
        Clusters.Dispose();
        ReflectionProbes.Dispose();
        Chunks.Dispose();
        api.DestroyFence(device, InFlight, null);
        api.DestroyCommandPool(device, CommandPool, null);
    }
}
