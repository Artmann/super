using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// One host-visible buffer for each frame in flight, made outside the layer and adopted into it, so a pass can write
/// a frame's instances while the GPU reads another's. A frame's buffer can be made bigger once the GPU is done with it.
/// </summary>
internal sealed unsafe class FrameBuffers : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly BufferUsageFlags _usage;
    private readonly GpuBuffer[] _buffers;
    private readonly BufferId[] _ids;

    /// <summary><paramref name="frames"/> buffers of <paramref name="bytes"/> each, used as <paramref name="usage"/> says.</summary>
    public FrameBuffers((VulkanContext Context, VulkanGpu Gpu) device, int frames, long bytes, BufferUsageFlags usage)
    {
        (_context, _gpu) = device;
        _usage = usage;
        _buffers = new GpuBuffer[frames];
        _ids = new BufferId[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            Make(frame, bytes);
        }
    }

    /// <summary>Frame <paramref name="frame"/>'s buffer, for vertex buffers and bind groups.</summary>
    public BufferId this[int frame] => _ids[frame];

    /// <summary>How many bytes frame <paramref name="frame"/>'s buffer holds.</summary>
    public long Size(int frame) => (long)_buffers[frame].Size;

    /// <summary>Frame <paramref name="frame"/>'s buffer as the CPU writes it, <paramref name="count"/> items long.</summary>
    public Span<T> Items<T>(int frame, int count) where T : unmanaged => new(_buffers[frame].Mapped, count);

    /// <summary>
    /// Replaces frame <paramref name="frame"/>'s buffer with one of <paramref name="bytes"/>, which drops what it held
    /// and changes its id, so bind groups holding it must be made again.
    /// </summary>
    public void Grow(int frame, long bytes)
    {
        Free(frame);
        Make(frame, bytes);
    }

    public void Dispose()
    {
        for (int frame = 0; frame < _buffers.Length; frame++)
        {
            Free(frame);
        }
    }

    private void Make(int frame, long bytes)
    {
        _buffers[frame] = GpuBuffer.CreateHostVisible(_context, (ulong)bytes, _usage);
        _ids[frame] = _gpu.Adopt(_buffers[frame].Handle, bytes);
    }

    private void Free(int frame)
    {
        _gpu.Release(_ids[frame]);
        _buffers[frame].Dispose();
    }
}
