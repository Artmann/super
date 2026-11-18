using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>
/// What every acceleration structure build shares: the extension's calls, one scratch buffer that grows when a build
/// needs more, and the barriers between builds and before tracing.
/// </summary>
internal sealed unsafe class StructureBuilder : IDisposable
{
    private readonly Action<IDisposable> _releaseAfterRecorded;
    private readonly ulong _scratchAlignment;

    // Shared by every build. Each build waits for the one before it, so one buffer serves them all; it grows when a
    // build needs more.
    private GpuBuffer? _scratch;

    /// <summary>
    /// The builder for the context's device. <paramref name="releaseAfterRecorded"/> frees an outgrown scratch buffer
    /// once the work recorded so far is done with it.
    /// </summary>
    public StructureBuilder(VulkanContext context, Action<IDisposable> releaseAfterRecorded)
    {
        Context = context;
        _releaseAfterRecorded = releaseAfterRecorded;
        if (!context.Api.TryGetDeviceExtension(context.Instance, context.Device, out KhrAccelerationStructure api))
        {
            throw new InvalidOperationException($"{KhrAccelerationStructure.ExtensionName} is not available.");
        }

        Api = api;
        var structureProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
        };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &structureProperties };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        _scratchAlignment = Math.Max(1u, structureProperties.MinAccelerationStructureScratchOffsetAlignment);
    }

    public VulkanContext Context { get; }

    public KhrAccelerationStructure Api { get; }

    /// <summary>Makes a structure of a type in <paramref name="buffer"/>, and finds its device address.</summary>
    public (AccelerationStructureKHR Handle, ulong Address) CreateStructure(GpuBuffer buffer, ulong size, AccelerationStructureTypeKHR type)
    {
        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = buffer.Handle,
            Size = size,
            Type = type,
        };
        Api.CreateAccelerationStructure(Context.Device, &createInfo, null, out var handle).Check();

        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = handle,
        };
        return (handle, Api.GetAccelerationStructureDeviceAddress(Context.Device, &addressInfo));
    }

    /// <summary>A scratch address with room for <paramref name="size"/> bytes, aligned as the device asks.</summary>
    public ulong Scratch(ulong size)
    {
        // The buffer's own address only promises the memory's alignment, so it's made big enough to round up.
        ulong needed = size + _scratchAlignment;
        if (_scratch is null || _scratch.Size < needed)
        {
            if (_scratch is not null)
            {
                _releaseAfterRecorded(_scratch);
            }

            _scratch = new GpuBuffer(Context, needed,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.DeviceLocalBit);
        }

        return TraceEncoding.AlignUp(_scratch.Address, _scratchAlignment);
    }

    /// <summary>
    /// Makes later work wait for the build: anything that reads the structure, and the next build, which reuses the
    /// scratch buffer.
    /// </summary>
    public void WaitForBuild(CommandBuffer cmd) => Context.Api.MemoryBarrier(cmd,
        (PipelineStageFlags2.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr),
        (PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr));

    /// <summary>Makes compute shaders recorded after this wait for every structure built before it, so they can trace them.</summary>
    public void ReadyToTrace(CommandBuffer cmd) => Context.Api.MemoryBarrier(cmd,
        (PipelineStageFlags2.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr),
        (TraceEncoding.TraceStages, AccessFlags2.AccelerationStructureReadBitKhr));

    public void Dispose()
    {
        _scratch?.Dispose();
        _scratch = null;
    }
}
