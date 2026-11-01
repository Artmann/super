using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>A GPU buffer sub-allocated from <see cref="GpuMemory"/>. See docs/design.md.</summary>
internal sealed unsafe class GpuBuffer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocation _allocation;

    public readonly Buffer Handle;
    public readonly ulong Size;

    /// <summary>CPU-visible address, or null for device-local buffers.</summary>
    public readonly void* Mapped;

    public GpuBuffer(VulkanContext context, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        _context = context;
        Size = size;
        var api = context.Api;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        api.CreateBuffer(context.Device, &bufferInfo, null, out Handle).Check();

        api.GetBufferMemoryRequirements(context.Device, Handle, out var requirements);
        _allocation = context.Memory.AllocateBuffer(requirements, properties);
        api.BindBufferMemory(context.Device, Handle, _allocation.Memory, _allocation.Offset).Check();
        Mapped = _allocation.Mapped;
    }

    public static GpuBuffer CreateHostVisible(VulkanContext context, ulong size, BufferUsageFlags usage) =>
        new(context, size, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

    /// <summary>Creates a device-local buffer and fills it through a temporary staging buffer.</summary>
    public static GpuBuffer CreateDeviceLocal<T>(VulkanContext context, ReadOnlySpan<T> data, BufferUsageFlags usage)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        using var staging = CreateHostVisible(context, size, BufferUsageFlags.TransferSrcBit);
        data.CopyTo(new Span<T>(staging.Mapped, data.Length));

        var buffer = new GpuBuffer(
            context, size, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        var source = staging.Handle;
        var destination = buffer.Handle;
        context.SubmitAndWait(cmd =>
        {
            var region = new BufferCopy(0, 0, size);
            context.Api.CmdCopyBuffer(cmd, source, destination, 1, &region);
        });
        return buffer;
    }

    public void Dispose()
    {
        _context.Api.DestroyBuffer(_context.Device, Handle, null);
        _context.Memory.Free(_allocation);
    }
}
