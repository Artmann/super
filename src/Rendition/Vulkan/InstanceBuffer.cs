using System.Numerics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>
/// One frame's model matrices, one for each copy drawn, which the scene's vertex shaders
/// read by <c>gl_InstanceIndex</c> (set 0, binding 2, in Shaders/instances.glsl). It
/// grows to fit, but only before a frame is recorded, since the frame's descriptor set
/// points at it.
/// </summary>
internal sealed unsafe class InstanceBuffer : IDisposable
{
    private const int StartingCapacity = 4096;

    private readonly VulkanContext _context;
    private GpuBuffer _buffer;

    public InstanceBuffer(VulkanContext context)
    {
        _context = context;
        _buffer = Create(context, StartingCapacity);
        Capacity = StartingCapacity;
    }

    public Buffer Handle => _buffer.Handle;

    public ulong Size => _buffer.Size;

    public int Capacity { get; private set; }

    /// <summary>How many matrices this frame has written so far.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Empties the buffer for a frame that writes at most <paramref name="count"/> matrices.
    /// Returns true when it had to grow, so the descriptor must point at the new buffer.
    /// Call it only once the GPU is done with this frame.
    /// </summary>
    public bool Reset(int count)
    {
        Count = 0;
        if (count <= Capacity)
        {
            return false;
        }

        int capacity = Math.Max(count, Capacity * 2);
        _buffer.Dispose();
        _buffer = Create(_context, capacity);
        Capacity = capacity;
        return true;
    }

    /// <summary>Writes the next copy's matrix and returns its index.</summary>
    public int Add(in Matrix4x4 model)
    {
        if (Count >= Capacity)
        {
            throw new InvalidOperationException($"This frame draws more than the {Capacity} copies it made room for.");
        }

        ((Matrix4x4*)_buffer.Mapped)[Count] = model;
        return Count++;
    }

    public void Dispose() => _buffer.Dispose();

    private static GpuBuffer Create(VulkanContext context, int capacity) =>
        GpuBuffer.CreateHostVisible(context, (ulong)(capacity * sizeof(Matrix4x4)), BufferUsageFlags.StorageBufferBit);
}
