using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The grass's buffers and its ground map, made the first frame with grass and pointed at by every frame slot's set:
/// the uniforms and squares each slot writes, and the blades, the indirect draw's arguments and the map they share.
/// </summary>
internal sealed unsafe class GrassTargets : IDisposable
{
    /// <summary>The format of the ground map's info: how thick the grass stands, and the draw's look.</summary>
    public const Format InfoFormat = Format.R8G8B8A8Unorm;

    // One scattered blade, as Shaders/grass.glsl lays out its Blade.
    private const int BladeSize = 48;

    private readonly VulkanContext _context;
    private readonly GpuBuffer[] _uniforms = new GpuBuffer[FrameRing.Count];
    private readonly GpuBuffer[] _tiles = new GpuBuffer[FrameRing.Count];
    private readonly GpuBuffer _blades;

    public GrassTargets(VulkanContext context, Sampler nearest, ReadOnlySpan<DescriptorSet> sets)
    {
        _context = context;
        for (int i = 0; i < FrameRing.Count; i++)
        {
            _uniforms[i] = GpuBuffer.CreateHostVisible(context, (ulong)sizeof(GrassUniforms), BufferUsageFlags.UniformBufferBit);
            _tiles[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(GrassField.MaxTiles * sizeof(GrassTile)), BufferUsageFlags.StorageBufferBit);
        }

        _blades = new GpuBuffer(context, (ulong)GrassStage.MaxBlades * BladeSize, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        Args = new GpuBuffer(context, 16,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
        var extent = new Extent2D(GrassField.CaptureSize, GrassField.CaptureSize);
        Height = new RenderTarget(context, GBuffer.DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        Info = new RenderTarget(context, InfoFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        WriteSets(nearest, sets);
    }

    /// <summary>The indirect draw's arguments, which the scatter counts the blades into.</summary>
    public GpuBuffer Args { get; }

    /// <summary>The ground map's heights, as depth: 0 where there is no ground.</summary>
    public RenderTarget Height { get; }

    /// <summary>The ground map's info.</summary>
    public RenderTarget Info { get; }

    /// <summary>The mapped uniforms of <paramref name="slot"/>.</summary>
    public void* Uniforms(int slot) => _uniforms[slot].Mapped;

    /// <summary>The mapped squares of <paramref name="slot"/>.</summary>
    public void* Tiles(int slot) => _tiles[slot].Mapped;

    private void WriteSets(Sampler nearest, ReadOnlySpan<DescriptorSet> sets)
    {
        var blades = new DescriptorBufferInfo(_blades.Handle, 0, _blades.Size);
        var args = new DescriptorBufferInfo(Args.Handle, 0, Args.Size);
        var height = new DescriptorImageInfo(nearest, Height.View, ImageLayout.ShaderReadOnlyOptimal);
        var info = new DescriptorImageInfo(nearest, Info.View, ImageLayout.ShaderReadOnlyOptimal);
        var uniforms = stackalloc DescriptorBufferInfo[FrameRing.Count];
        var tiles = stackalloc DescriptorBufferInfo[FrameRing.Count];
        var writes = stackalloc WriteDescriptorSet[6 * FrameRing.Count];
        for (int i = 0; i < FrameRing.Count; i++)
        {
            uniforms[i] = new DescriptorBufferInfo(_uniforms[i].Handle, 0, _uniforms[i].Size);
            tiles[i] = new DescriptorBufferInfo(_tiles[i].Handle, 0, _tiles[i].Size);
            var set = sets[i];
            writes[6 * i] = Descriptors.BufferWrite(set, 0, &uniforms[i], DescriptorType.UniformBuffer);
            writes[6 * i + 1] = Descriptors.BufferWrite(set, 1, &tiles[i]);
            writes[6 * i + 2] = Descriptors.BufferWrite(set, 2, &blades);
            writes[6 * i + 3] = Descriptors.BufferWrite(set, 3, &args);
            writes[6 * i + 4] = Descriptors.ImageWrite(set, 4, DescriptorType.CombinedImageSampler, &height);
            writes[6 * i + 5] = Descriptors.ImageWrite(set, 5, DescriptorType.CombinedImageSampler, &info);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, (uint)(6 * FrameRing.Count), writes, 0, null);
    }

    public void Dispose()
    {
        foreach (var buffer in _uniforms)
        {
            buffer.Dispose();
        }

        foreach (var buffer in _tiles)
        {
            buffer.Dispose();
        }

        _blades.Dispose();
        Args.Dispose();
        Height.Dispose();
        Info.Dispose();
    }
}
