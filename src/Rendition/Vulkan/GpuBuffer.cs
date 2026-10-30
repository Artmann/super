using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>A GPU buffer with its own memory. See docs/design.md on sub-allocation.</summary>
internal sealed unsafe class GpuBuffer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DeviceMemory _memory;

    public readonly Buffer Handle;

    /// <summary>CPU-visible address, or null for device-local buffers.</summary>
    public readonly void* Mapped;

    public GpuBuffer(VulkanContext context, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        _context = context;
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
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = context.FindMemoryType(requirements.MemoryTypeBits, properties),
        };
        api.AllocateMemory(context.Device, &allocInfo, null, out _memory).Check();
        api.BindBufferMemory(context.Device, Handle, _memory, 0).Check();

        if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* mapped;
            api.MapMemory(context.Device, _memory, 0, Vk.WholeSize, 0, &mapped).Check();
            Mapped = mapped;
        }
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
        _context.Api.FreeMemory(_context.Device, _memory, null);
    }
}
