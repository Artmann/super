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

    /// <summary>
    /// The address a shader or an acceleration structure build reads the buffer by, or 0 when the buffer was made
    /// without <see cref="BufferUsageFlags.ShaderDeviceAddressBit"/> or the device can't trace.
    /// </summary>
    public readonly ulong Address;

    /// <summary>
    /// The usage flags a buffer needs to be read by an acceleration structure build: its own address, and being
    /// allowed as a build input. None where the device can't trace, so those buffers are made exactly as before.
    /// </summary>
    public static BufferUsageFlags TraceInput(VulkanContext context) =>
        context.CanTrace
            ? BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr
            : 0;

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

        if (context.CanTrace && (usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0)
        {
            var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = Handle };
            Address = api.GetBufferDeviceAddress(context.Device, &addressInfo);
        }
    }

    public static GpuBuffer CreateHostVisible(VulkanContext context, ulong size, BufferUsageFlags usage) =>
        new(context, size, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

    /// <summary>
    /// Creates a device-local buffer and a filled staging buffer for it, without copying
    /// yet. Record the copy with <see cref="RecordUpload"/> and dispose the staging buffer
    /// once that work is done.
    /// </summary>
    public static GpuBuffer CreateDeviceLocalStaged<T>(
        VulkanContext context, ReadOnlySpan<T> data, BufferUsageFlags usage, out GpuBuffer staging)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        staging = CreateHostVisible(context, size, BufferUsageFlags.TransferSrcBit);
        data.CopyTo(new Span<T>(staging.Mapped, data.Length));
        return new GpuBuffer(context, size, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
    }

    /// <summary>Records the copy of a staging buffer's whole contents into this one.</summary>
    public void RecordUpload(CommandBuffer cmd, GpuBuffer staging)
    {
        var region = new BufferCopy(0, 0, Size);
        _context.Api.CmdCopyBuffer(cmd, staging.Handle, Handle, 1, &region);
    }

    public void Dispose()
    {
        _context.Api.DestroyBuffer(_context.Device, Handle, null);
        _context.Memory.Free(_allocation);
    }
}
