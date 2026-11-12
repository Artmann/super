using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Vulkan;

/// <summary>
/// Where one copy reads its baked light, as gbuffer.vert reads it from set 0, binding 10: its lightmap rectangle, which
/// moves the second uv onto the page, and the bindless indices of the page's irradiance, direction and shadow mask
/// textures. Must match LightmapRect in Shaders/instances.glsl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLightmapRect
{
    /// <summary>xy scale and zw offset from the second uv to 0 to 1 on the page.</summary>
    public Vector4 ScaleOffset;
    public int Irradiance;
    public int Direction;

    /// <summary>The shadow mask's texture, or -1 when the bake has none, which reads as fully lit.</summary>
    public int Mask;

    /// <summary>The page's number in the bake, or -1 for a copy with no lightmap.</summary>
    public int Page;

    /// <summary>
    /// The placed chunk this copy belongs to, its index in the frame's chunk buffer (set 0, binding 11), so its bake is
    /// relit by the frame's sky and suns turned into that chunk's space; or -1 for none. Set per copy by
    /// <see cref="InstanceBuffer.Add"/>, since every copy of a chunk shares one rectangle.
    /// </summary>
    public int Chunk;
    private int _pad0;
    private int _pad1;
    private int _pad2;

    /// <summary>A copy with no lightmap: it is lit live, as if no bake were loaded.</summary>
    public static GpuLightmapRect None => new() { Irradiance = -1, Direction = -1, Mask = -1, Page = -1, Chunk = -1 };
}

/// <summary>
/// One frame's model matrices, one for each copy drawn, which the scene's vertex shaders
/// read by <c>gl_InstanceIndex</c> (set 0, binding 2, in Shaders/instances.glsl), and beside
/// them where each copy stood last frame, for motion vectors (binding 9), and where it reads its
/// baked light (binding 10). It grows to fit, but only before a frame is recorded, since the
/// frame's descriptor set points at it.
/// </summary>
internal sealed unsafe class InstanceBuffer : IDisposable
{
    private const int StartingCapacity = 4096;

    private readonly VulkanContext _context;
    private GpuBuffer _buffer;
    private GpuBuffer _previous;
    private GpuBuffer _lightmaps;

    public InstanceBuffer(VulkanContext context)
    {
        _context = context;
        (_buffer, _previous, _lightmaps) = Create(context, StartingCapacity);
        Capacity = StartingCapacity;
    }

    public Buffer Handle => _buffer.Handle;

    public ulong Size => _buffer.Size;

    /// <summary>Where each copy stood last frame, as <see cref="PreviousInstance"/>s.</summary>
    public Buffer PreviousHandle => _previous.Handle;

    public ulong PreviousSize => _previous.Size;

    /// <summary>Where each copy reads its baked light, as <see cref="GpuLightmapRect"/>s.</summary>
    public Buffer LightmapHandle => _lightmaps.Handle;

    public ulong LightmapSize => _lightmaps.Size;

    public int Capacity { get; private set; }

    /// <summary>How many matrices this frame has written so far.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Where the draws added now stood last frame. Set while the G-buffer pass records the frame's own draws; null
    /// otherwise, which gives every copy no last frame.
    /// </summary>
    public MotionHistory? History { get; set; }

    /// <summary>
    /// The loaded bake's lightmap rectangles, by <see cref="DrawItem.BakeId"/>. Set while the G-buffer pass records,
    /// a reflection capture's included; null otherwise, and a copy it doesn't hold gets <see cref="GpuLightmapRect.None"/>.
    /// </summary>
    public IReadOnlyDictionary<ulong, GpuLightmapRect>? Lightmaps { get; set; }

    /// <summary>
    /// The chunk each of the draws being recorded belongs to, by its index among them, or -1 for none. Set while the
    /// G-buffer pass records the camera's draws; null otherwise, so a reflection capture's copies keep their plain baked
    /// light only.
    /// </summary>
    public int[]? Chunks { get; set; }

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
        _lightmaps.Dispose();
        (_buffer, _previous, _lightmaps) = Create(_context, capacity);
        Capacity = capacity;
        return true;
    }

    /// <summary>
    /// Writes the next copy's matrix, where it stood last frame and its lightmap, and returns its index.
    /// <paramref name="index"/> is the draw's place among the draws being recorded, which finds its chunk in
    /// <see cref="Chunks"/>.
    /// </summary>
    public int Add(in DrawItem draw, int index)
    {
        if (Count >= Capacity)
        {
            throw new InvalidOperationException($"This frame draws more than the {Capacity} copies it made room for.");
        }

        ((Matrix4x4*)_buffer.Mapped)[Count] = draw.Transform;
        ((PreviousInstance*)_previous.Mapped)[Count] = History is { } history ? history.Previous(draw) : PreviousInstance.None(draw);
        var lightmap = GpuLightmapRect.None;
        if (draw.BakeId != 0 && Lightmaps is { } lightmaps && lightmaps.TryGetValue(draw.BakeId, out var rect))
        {
            lightmap = rect;
            lightmap.Chunk = Chunks is { } chunks ? chunks[index] : -1;
        }

        ((GpuLightmapRect*)_lightmaps.Mapped)[Count] = lightmap;
        return Count++;
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _previous.Dispose();
        _lightmaps.Dispose();
    }

    private static (GpuBuffer Models, GpuBuffer Previous, GpuBuffer Lightmaps) Create(VulkanContext context, int capacity) => (
        GpuBuffer.CreateHostVisible(context, (ulong)(capacity * sizeof(Matrix4x4)), BufferUsageFlags.StorageBufferBit),
        GpuBuffer.CreateHostVisible(context, (ulong)(capacity * sizeof(PreviousInstance)), BufferUsageFlags.StorageBufferBit),
        GpuBuffer.CreateHostVisible(context, (ulong)(capacity * sizeof(GpuLightmapRect)), BufferUsageFlags.StorageBufferBit));
}
