using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>A range of device memory handed out by <see cref="GpuMemory"/>.</summary>
internal readonly unsafe struct GpuAllocation
{
    public DeviceMemory Memory { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }

    /// <summary>CPU address of the range, or null when the memory isn't host visible.</summary>
    public void* Mapped { get; init; }

    /// <summary>Which pool and block the range came from, or -1 for a dedicated allocation.</summary>
    internal int Pool { get; init; }
    internal int Block { get; init; }
}

/// <summary>
/// Sub-allocates all device memory from 64 MB blocks, one set of pools per memory type and
/// kept apart for buffers and images. Anything larger than a block gets its own allocation.
/// Host-visible blocks are mapped once for their whole life. See docs/design.md. With canTrace, every allocation
/// allows a device address.
/// </summary>
internal sealed unsafe class GpuMemory(Vk api, PhysicalDevice physicalDevice, Device device, bool canTrace) : IDisposable
{
    public const ulong BlockSize = 64 * 1024 * 1024;

    private sealed class Block(DeviceMemory memory, ulong size, byte* mapped)
    {
        public readonly DeviceMemory Memory = memory;
        public readonly MemoryArena Arena = new(size);
        public readonly byte* Mapped = mapped;
    }

    private sealed class Pool(uint memoryType, bool images, bool hostVisible)
    {
        public readonly uint MemoryType = memoryType;
        public readonly bool Images = images;
        public readonly bool HostVisible = hostVisible;
        public readonly List<Block> Blocks = [];
    }

    private readonly List<Pool> _pools = [];

    // Bytes in allocations too large for a block, which each have their own memory.
    private ulong _dedicated;

    /// <summary>Bytes handed out and still in use, in the blocks and in allocations of their own.</summary>
    public ulong UsedBytes
    {
        get
        {
            ulong used = _dedicated;
            foreach (var pool in _pools)
            {
                foreach (var block in pool.Blocks)
                {
                    used += block.Arena.Used;
                }
            }

            return used;
        }
    }

    /// <summary>Bytes taken from the device: every block whole, and the allocations of their own.</summary>
    public ulong ReservedBytes
    {
        get
        {
            ulong reserved = _dedicated;
            foreach (var pool in _pools)
            {
                reserved += (ulong)pool.Blocks.Count * BlockSize;
            }

            return reserved;
        }
    }

    public GpuAllocation AllocateBuffer(in MemoryRequirements requirements, MemoryPropertyFlags properties) =>
        Allocate(requirements, properties, images: false);

    public GpuAllocation AllocateImage(in MemoryRequirements requirements, MemoryPropertyFlags properties) =>
        Allocate(requirements, properties, images: true);

    public void Free(in GpuAllocation allocation)
    {
        if (allocation.Pool < 0)
        {
            api.FreeMemory(device, allocation.Memory, null);
            return;
        }

        _pools[allocation.Pool].Blocks[allocation.Block].Arena.Free(allocation.Offset);
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
        {
            foreach (var block in pool.Blocks)
            {
                api.FreeMemory(device, block.Memory, null);
            }
        }

        _pools.Clear();
    }

    private GpuAllocation Allocate(in MemoryRequirements requirements, MemoryPropertyFlags properties, bool images)
    {
        uint memoryType = FindMemoryType(requirements.MemoryTypeBits, properties);
        bool hostVisible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;

        if (requirements.Size > BlockSize)
        {
            var memory = AllocateRaw(requirements.Size, memoryType, hostVisible, out byte* mapped);
            return new GpuAllocation { Memory = memory, Size = requirements.Size, Mapped = mapped, Pool = -1, Block = -1 };
        }

        int poolIndex = FindPool(memoryType, images, hostVisible);
        var pool = _pools[poolIndex];
        for (int i = 0; i < pool.Blocks.Count; i++)
        {
            if (pool.Blocks[i].Arena.TryAllocate(requirements.Size, requirements.Alignment, out ulong offset))
            {
                return Make(pool.Blocks[i], poolIndex, i, offset, requirements.Size);
            }
        }

        var blockMemory = AllocateRaw(BlockSize, memoryType, hostVisible, out byte* blockMapped);
        var block = new Block(blockMemory, BlockSize, blockMapped);
        pool.Blocks.Add(block);
        if (!block.Arena.TryAllocate(requirements.Size, requirements.Alignment, out ulong fresh))
        {
            throw new InvalidOperationException("A fresh memory block could not fit the request.");
        }

        return Make(block, poolIndex, pool.Blocks.Count - 1, fresh, requirements.Size);
    }

    private static GpuAllocation Make(Block block, int pool, int index, ulong offset, ulong size) => new()
    {
        Memory = block.Memory,
        Offset = offset,
        Size = size,
        Mapped = block.Mapped == null ? null : block.Mapped + offset,
        Pool = pool,
        Block = index,
    };

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        api.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memory);
        for (int i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.MemoryTypes[i].PropertyFlags & required) == required)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException($"No GPU memory type supports {required}.");
    }

    private int FindPool(uint memoryType, bool images, bool hostVisible)
    {
        for (int i = 0; i < _pools.Count; i++)
        {
            if (_pools[i].MemoryType == memoryType && _pools[i].Images == images && _pools[i].HostVisible == hostVisible)
            {
                return i;
            }
        }

        _pools.Add(new Pool(memoryType, images, hostVisible));
        return _pools.Count - 1;
    }

    private DeviceMemory AllocateRaw(ulong size, uint memoryType, bool hostVisible, out byte* mapped)
    {
        // A buffer can only have a device address if its memory was allocated to allow one. That is asked of every
        // allocation where the device can trace, so a mesh made later for tracing never needs its own block. A device
        // without bufferDeviceAddress must not be asked, so there the allocation is exactly what it always was.
        var addressFlags = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit,
        };
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = canTrace ? &addressFlags : null,
            AllocationSize = size,
            MemoryTypeIndex = memoryType,
        };
        api.AllocateMemory(device, &allocInfo, null, out var memory).Check();

        mapped = null;
        if (hostVisible)
        {
            void* pointer;
            api.MapMemory(device, memory, 0, Vk.WholeSize, 0, &pointer).Check();
            mapped = (byte*)pointer;
        }

        return memory;
    }
}
