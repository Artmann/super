using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// How much of each flaring light is seen, worked out on the GPU by lens_flare_visibility.comp from the depth in a
/// small disc round the light, and kept in a buffer lens_flare.vert reads, so nothing comes back to the CPU. Under
/// anti-aliasing the buffer keeps the last four frames' visibility and the flare takes the most of them, so the jitter
/// of a light seen through leaves doesn't make it flicker. A cut starts the four frames afresh.
/// </summary>
internal sealed unsafe class LensFlareVisibility : IDisposable
{
    /// <summary>The floats each light has in the buffer: four frames, the visibility drawn with, and pads.</summary>
    private const int SourceFloats = 8;

    private const ulong BufferSize = LensFlareSettings.MaxSources * SourceFloats * sizeof(float);

    /// <summary>Must match Constants in lens_flare_visibility.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Source0;
        public Vector4 Source1;
        public Vector4 Source2;
        public Vector4 Source3;
        public float Aspect;
        public int Count;
        public int Slot;
        public int Flags;
    }

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly GpuBuffer _buffer;
    private readonly BufferId _bufferId;
    private readonly DescriptorPool _pool;
    private readonly ComputePass _pass;
    private readonly CommandList _commands = new();
    private readonly Sampler _depthSampler;
    private readonly LensFlareHistory _history = new();

    /// <summary>
    /// Taps reading depth through <paramref name="depthSampler"/>. Their set is made outside the GPU layer, as the
    /// flare's draw, which hasn't moved yet, reads the buffer too; the taps borrow its layout.
    /// </summary>
    public LensFlareVisibility((VulkanContext Context, VulkanGpu Gpu) device, Sampler depthSampler)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        _depthSampler = depthSampler;
        _buffer = new GpuBuffer(
            context, BufferSize, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        _bufferId = gpu.Adopt(_buffer.Handle, (long)BufferSize);

        (SetLayout, _pool, Set) = context.CreateStateSet(ShaderStageFlags.VertexBit, new DescriptorBufferInfo(_buffer.Handle, 0, BufferSize), images: 2);
        _pass = new ComputePass(gpu, "lens_flare_visibility.comp", [SetLayout]);

        // Nothing seen until the first frame looks, which starts the frames afresh anyway.
        _commands.ClearBuffer(_bufferId, 0, (int)BufferSize);
        context.SubmitAndWait(cmd => _gpu.Play(_commands, cmd));
    }

    /// <summary>The buffer, which the compute and vertex shaders read, and the scene's depth, which the compute shader reads.</summary>
    public DescriptorSetLayout SetLayout { get; }

    /// <summary>The set of <see cref="SetLayout"/>.</summary>
    public DescriptorSet Set { get; }

    /// <summary>Points the taps at the scene's depth. Only call it while no frame in flight uses the set.</summary>
    public void SetDepth(ImageView depth)
    {
        var image = new DescriptorImageInfo(_depthSampler, depth, ImageLayout.DepthReadOnlyOptimal);
        var write = Descriptors.ImageWrite(Set, 1, DescriptorType.CombinedImageSampler, &image);
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <summary>
    /// Points the sun's taps at the clouds' picture, whose alpha says how much of the sky they let through. Only call
    /// it while no frame in flight uses the set.
    /// </summary>
    public void SetClouds(ImageView picture)
    {
        var image = new DescriptorImageInfo(_depthSampler, picture, ImageLayout.General);
        var write = Descriptors.ImageWrite(Set, 2, DescriptorType.CombinedImageSampler, &image);
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <inheritdoc cref="LensFlareHistory.Take"/>
    public void Take(ReadOnlySpan<FlareSource> sources, bool hold, bool historyReady, bool clouds) =>
        _history.Take(sources, hold, historyReady, clouds);

    /// <summary>Records the taps for the lights <see cref="Take"/> was given, after the scene's depth is drawn.</summary>
    public void Record(CommandBuffer cmd, ReadOnlySpan<FlareSource> sources, float aspect)
    {
        var (slot, flags) = _history.Record(sources);
        var constants = new Constants
        {
            Source0 = Pack(sources, 0),
            Source1 = Pack(sources, 1),
            Source2 = Pack(sources, 2),
            Source3 = Pack(sources, 3),
            Aspect = aspect,
            Count = sources.Length,
            Slot = slot,
            Flags = flags,
        };
        // A compute pass sees the depth drawn and the buffer read before it, and the flare's draw after it sees the buffer.
        _commands.Reset();
        _pass.Record(_commands, [_gpu.Adopt(Set)], new ReadOnlySpan<byte>(&constants, sizeof(Constants)), (sources.Length, 1, 1));
        _gpu.Play(_commands, cmd);
    }

    /// <inheritdoc cref="LensFlareHistory.Skip"/>
    public void Skip() => _history.Skip();

    public void Dispose()
    {
        var api = _context.Api;
        _pass.Dispose();
        _gpu.Release(_bufferId);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, SetLayout, null);
        _buffer.Dispose();
    }

    private static Vector4 Pack(ReadOnlySpan<FlareSource> sources, int index)
    {
        if (index >= sources.Length)
        {
            return Vector4.Zero;
        }

        var source = sources[index];
        return new Vector4(source.Uv, source.Depth, source.Radius);
    }
}
