using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The fog volumes the volumetric fog's inject pass reads, one host-visible buffer for each of its two descriptor
/// sets, at binding 5. A set is bound every other frame that draws the fog, and two frames are in flight at most, so
/// the frame that last read a buffer has finished by the time it is written again.
/// </summary>
internal sealed unsafe class FogVolumeBuffers : IDisposable
{
    /// <summary>The binding the buffers take in the fog passes' set.</summary>
    public const uint Binding = 5;

    private readonly GpuBuffer[] _buffers = new GpuBuffer[2];

    public FogVolumeBuffers(VulkanContext context, ReadOnlySpan<DescriptorSet> sets)
    {
        var infos = stackalloc DescriptorBufferInfo[2];
        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            _buffers[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(FogVolumeBatch.MaxVolumes * FogVolumePack.Stride), BufferUsageFlags.StorageBufferBit);
            infos[i] = new DescriptorBufferInfo(_buffers[i].Handle, 0, Vk.WholeSize);
            writes[i] = Descriptors.BufferWrite(sets[i], Binding, &infos[i]);
        }

        context.Api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);
    }

    /// <summary>Writes the volumes into the buffer the set given reads, at most <see cref="FogVolumeBatch.MaxVolumes"/>.</summary>
    public void Write(int set, ReadOnlySpan<FogVolumeDraw> volumes) =>
        FogVolumePack.Pack(volumes, new Span<GpuFogVolume>(_buffers[set].Mapped, FogVolumeBatch.MaxVolumes));

    public void Dispose()
    {
        _buffers[0].Dispose();
        _buffers[1].Dispose();
    }
}
