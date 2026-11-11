using System.Numerics;
using Rendition.Passes;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>
/// One frame's model matrices, one for each copy drawn, which the scene's vertex shaders
/// read by <c>gl_InstanceIndex</c> (set 0, binding 2, in Shaders/instances.glsl), and beside
/// them where each copy stood last frame, for motion vectors (binding 9). It grows to fit,
/// but only before a frame is recorded, since the frame's descriptor set points at it.
/// </summary>
internal sealed unsafe class InstanceBuffer : IDisposable
{
    private const int StartingCapacity = 4096;

    private readonly VulkanContext _context;
    private GpuBuffer _buffer;
    private GpuBuffer _previous;

    public InstanceBuffer(VulkanContext context)
    {
        _context = context;
        (_buffer, _previous) = Create(context, StartingCapacity);
        Capacity = StartingCapacity;
    }

    public Buffer Handle => _buffer.Handle;

    public ulong Size => _buffer.Size;

    /// <summary>Where each copy stood last frame, as <see cref="PreviousInstance"/>s.</summary>
    public Buffer PreviousHandle => _previous.Handle;

    public ulong PreviousSize => _previous.Size;

    public int Capacity { get; private set; }

    /// <summary>How many matrices this frame has written so far.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Where the draws added now stood last frame. Set while the G-buffer pass records the frame's own draws; null
    /// otherwise, which gives every copy no last frame.
    /// </summary>
    public MotionHistory? History { get; set; }

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
        _previous.Dispose();
        (_buffer, _previous) = Create(_context, capacity);
        Capacity = capacity;
        return true;
    }

    /// <summary>Writes the next copy's matrix, and where it stood last frame, and returns its index.</summary>
    public int Add(in DrawItem draw)
    {
        if (Count >= Capacity)
        {
            throw new InvalidOperationException($"This frame draws more than the {Capacity} copies it made room for.");
        }

        ((Matrix4x4*)_buffer.Mapped)[Count] = draw.Transform;
        ((PreviousInstance*)_previous.Mapped)[Count] = History is { } history ? history.Previous(draw) : PreviousInstance.None(draw);
        return Count++;
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _previous.Dispose();
    }

    private static (GpuBuffer Models, GpuBuffer Previous) Create(VulkanContext context, int capacity) => (
        GpuBuffer.CreateHostVisible(context, (ulong)(capacity * sizeof(Matrix4x4)), BufferUsageFlags.StorageBufferBit),
        GpuBuffer.CreateHostVisible(context, (ulong)(capacity * sizeof(PreviousInstance)), BufferUsageFlags.StorageBufferBit));
}
