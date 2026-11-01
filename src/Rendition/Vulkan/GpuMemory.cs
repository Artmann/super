using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>A range of device memory handed out by <see cref="GpuMemory"/>.</summary>
internal readonly unsafe struct GpuAllocation
{
    public readonly DeviceMemory Memory;
    public readonly ulong Offset;
    public readonly ulong Size;

    /// <summary>CPU address of the range, or null when the memory isn't host visible.</summary>
    public readonly void* Mapped;

    /// <summary>Which pool and block the range came from, or -1 for a dedicated allocation.</summary>
    internal readonly int Pool;
    internal readonly int Block;

    internal GpuAllocation(DeviceMemory memory, ulong offset, ulong size, void* mapped, int pool, int block)
    {
        Memory = memory;
        Offset = offset;
        Size = size;
        Mapped = mapped;
        Pool = pool;
        Block = block;
    }
}

/// <summary>
/// Sub-allocates all device memory from 64 MB blocks, one set of pools per memory type and
/// kept apart for buffers and images. Anything larger than a block gets its own allocation.
/// Host-visible blocks are mapped once for their whole life. See docs/design.md.
/// </summary>
internal sealed unsafe class GpuMemory(VulkanContext context) : IDisposable
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

    public GpuAllocation AllocateBuffer(in MemoryRequirements requirements, MemoryPropertyFlags properties) =>
        Allocate(requirements, properties, images: false);

    public GpuAllocation AllocateImage(in MemoryRequirements requirements, MemoryPropertyFlags properties) =>
        Allocate(requirements, properties, images: true);

    public void Free(in GpuAllocation allocation)
    {
        if (allocation.Pool < 0)
        {
            context.Api.FreeMemory(context.Device, allocation.Memory, null);
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
                context.Api.FreeMemory(context.Device, block.Memory, null);
            }
        }

        _pools.Clear();
    }

    private GpuAllocation Allocate(in MemoryRequirements requirements, MemoryPropertyFlags properties, bool images)
    {
        uint memoryType = context.FindMemoryType(requirements.MemoryTypeBits, properties);
        bool hostVisible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;

        if (requirements.Size > BlockSize)
        {
            var memory = AllocateRaw(requirements.Size, memoryType, hostVisible, out byte* mapped);
            return new GpuAllocation(memory, 0, requirements.Size, mapped, -1, -1);
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

    private static GpuAllocation Make(Block block, int pool, int index, ulong offset, ulong size) =>
        new(block.Memory, offset, size, block.Mapped == null ? null : block.Mapped + offset, pool, index);

    private int FindPool(uint memoryType, bool images, bool hostVisible)
    {
        for (int i = 0; i < _pools.Count; i++)
        {
            if (_pools[i].MemoryType == memoryType && _pools[i].Images == images)
            {
                return i;
            }
        }

        _pools.Add(new Pool(memoryType, images, hostVisible));
        return _pools.Count - 1;
    }

    private DeviceMemory AllocateRaw(ulong size, uint memoryType, bool hostVisible, out byte* mapped)
    {
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = memoryType,
        };
        context.Api.AllocateMemory(context.Device, &allocInfo, null, out var memory).Check();

        mapped = null;
        if (hostVisible)
        {
            void* pointer;
            context.Api.MapMemory(context.Device, memory, 0, Vk.WholeSize, 0, &pointer).Check();
            mapped = (byte*)pointer;
        }

        return memory;
    }
}
