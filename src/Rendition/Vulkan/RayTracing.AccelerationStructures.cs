using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

// The structures themselves and the buffers they live in.
internal sealed unsafe partial class RayTracing
{
    /// <summary>Hands a top-level target back to the pool once the work recorded with it is done.</summary>
    private sealed class Recycle(RayTracing owner, TopLevelTarget target) : IDisposable
    {
        public void Dispose() => owner.Return(target);
    }

    /// <summary>
    /// A top-level structure sized for up to <see cref="Capacity"/> instances, the host-visible buffer its instances are
    /// written into, and the host-visible geometry table those instances index.
    /// </summary>
    private sealed class TopLevelTarget : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly KhrAccelerationStructure _api;
        private readonly GpuBuffer _buffer;

        public readonly int Capacity;
        public readonly ulong ScratchSize;
        public readonly GpuBuffer Instances;
        public readonly AccelerationStructureKHR Handle;

        /// <summary>Hands this target back to the pool. Made the first time the target goes out, then reused.</summary>
        public IDisposable? Recycler;

        /// <summary>The geometry table, made by the first <see cref="Table"/> call and grown by later ones.</summary>
        public GpuBuffer? GeometryTable { get; private set; }

        private int _tableCapacity;

        /// <summary>
        /// Room for <paramref name="count"/> entries in the geometry table. A target is only handed out once nothing
        /// reads it, so a table that's too small can go at once.
        /// </summary>
        public Span<GpuTraceGeometry> Table(int count)
        {
            if (GeometryTable is null || _tableCapacity < count)
            {
                GeometryTable?.Dispose();
                _tableCapacity = (int)Math.Max(16u, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));
                GeometryTable = GpuBuffer.CreateHostVisible(
                    _context, (ulong)(_tableCapacity * sizeof(GpuTraceGeometry)), BufferUsageFlags.StorageBufferBit);
            }

            return new Span<GpuTraceGeometry>(GeometryTable.Mapped, count);
        }

        /// <summary>Writes the parts into the geometry table, finding each one's mesh with <paramref name="meshes"/>.</summary>
        public void WriteTable(List<TraceGeometry> parts, Func<MeshHandle, Mesh> meshes)
        {
            var table = Table(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                var (meshHandle, part, tint) = parts[i];
                var mesh = meshes(meshHandle);
                table[i] = new GpuTraceGeometry
                {
                    Vertices = mesh.Vertices.Address,
                    Indices = mesh.Indices.Address,
                    FirstIndex = part.FirstIndex,
                    Material = (uint)part.Material.Index,
                    TintRG = Half2(tint.X, tint.Y),
                    TintB = Half2(tint.Z, 0f),
                };
            }
        }

        public readonly ulong Address;

        public TopLevelTarget(VulkanContext context, KhrAccelerationStructure api, int capacity, ulong size, ulong scratchSize)
        {
            _context = context;
            _api = api;
            Capacity = capacity;
            ScratchSize = scratchSize;
            Instances = GpuBuffer.CreateHostVisible(
                context, (ulong)(capacity * sizeof(AccelerationStructureInstanceKHR)), GpuBuffer.TraceInput(context));
            if (Instances.Address % 16 != 0)
            {
                throw new InvalidOperationException($"Instance data must be 16-byte aligned, but sits at {Instances.Address:X}.");
            }

            _buffer = new GpuBuffer(context, size,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);
            (Handle, Address) = CreateStructure(context, api, _buffer, size, AccelerationStructureTypeKHR.TopLevelKhr);
        }

        public void Dispose()
        {
            _api.DestroyAccelerationStructure(_context.Device, Handle, null);
            _buffer.Dispose();
            Instances.Dispose();
            GeometryTable?.Dispose();
        }
    }

    private static (AccelerationStructureKHR Handle, ulong Address) CreateStructure(
        VulkanContext context, KhrAccelerationStructure api, GpuBuffer buffer, ulong size, AccelerationStructureTypeKHR type)
    {
        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = buffer.Handle,
            Size = size,
            Type = type,
        };
        api.CreateAccelerationStructure(context.Device, &createInfo, null, out var handle).Check();

        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = handle,
        };
        return (handle, api.GetAccelerationStructureDeviceAddress(context.Device, &addressInfo));
    }

    /// <summary>One built bottom-level structure and the buffer it lives in.</summary>
    private sealed class BottomLevelStructure : IDisposable
    {
        private readonly RayTracing _owner;
        private readonly VulkanContext _context;
        private readonly KhrAccelerationStructure _api;
        private readonly GpuBuffer _buffer;

        public readonly TracePart[] Parts;
        public readonly AccelerationStructureKHR Handle;
        public readonly ulong Address;

        public BottomLevelStructure(RayTracing owner, TracePart[] parts, ulong size)
        {
            _owner = owner;
            _context = owner._context;
            _api = owner._api;
            Parts = parts;
            _buffer = new GpuBuffer(_context, size,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);
            (Handle, Address) = CreateStructure(_context, _api, _buffer, size, AccelerationStructureTypeKHR.BottomLevelKhr);
        }

        public void Dispose()
        {
            _api.DestroyAccelerationStructure(_context.Device, Handle, null);
            _buffer.Dispose();
            _owner._bottomLevelsReleased++;
        }
    }
}
