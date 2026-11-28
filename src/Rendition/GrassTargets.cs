using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The grass's buffers and its ground map, made the first frame with grass and pointed at by every frame slot's set:
/// the uniforms and squares each slot writes, and the blades, the indirect draw's arguments and the map they share.
/// The arguments and the map are adopted into the GPU layer, which draws and copies them.
/// </summary>
internal sealed unsafe class GrassTargets : IDisposable
{
    /// <summary>The format of the ground map's info: how thick the grass stands, and the draw's look.</summary>
    public const Format InfoFormat = Format.R8G8B8A8Unorm;

    // One scattered blade, as Shaders/grass.glsl lays out its Blade.
    private const int BladeSize = 48;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly GpuBuffer[] _uniforms = new GpuBuffer[FrameRing.Count];
    private readonly GpuBuffer[] _tiles = new GpuBuffer[FrameRing.Count];
    private readonly GpuBuffer _blades;
    private readonly GpuBuffer _args;
    private readonly GpuBuffer _argsStart;
    private readonly RenderTarget _height;
    private readonly RenderTarget _info;

    public GrassTargets((VulkanContext Context, VulkanGpu Gpu) device, Sampler nearest, ReadOnlySpan<DescriptorSet> sets)
    {
        var context = device.Context;
        (_context, _gpu) = device;
        for (int i = 0; i < FrameRing.Count; i++)
        {
            _uniforms[i] = GpuBuffer.CreateHostVisible(context, (ulong)sizeof(GrassUniforms), BufferUsageFlags.UniformBufferBit);
            _tiles[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(GrassField.MaxTiles * sizeof(GrassTile)), BufferUsageFlags.StorageBufferBit);
        }

        ulong blades = (ulong)GrassField.DetailStart(GrassField.Details) * BladeSize;
        _blades = new GpuBuffer(context, blades, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        _args = new GpuBuffer(context, GrassStage.ArgsSize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);
        _argsStart = CreateArgsStart(context);
        Args = _gpu.Adopt(_args.Handle, GrassStage.ArgsSize);
        ArgsStart = _gpu.Adopt(_argsStart.Handle, GrassStage.ArgsSize);

        var extent = new Extent2D(GrassField.CaptureSize, GrassField.CaptureSize);
        _height = new RenderTarget(context, GBuffer.DepthFormat, extent, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        _info = new RenderTarget(context, InfoFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
        Height = _gpu.Adopt(new AdoptedView(_height.View, _height.Image, extent));
        Info = _gpu.Adopt(new AdoptedView(_info.View, _info.Image, extent));
        WriteSets(nearest, sets);
    }

    /// <summary>Each detail's indirect draw's arguments, which the scatter counts the blades into.</summary>
    public BufferId Args { get; }

    /// <summary>The arguments each frame starts from, copied over <see cref="Args"/>: each detail's vertices and no blades.</summary>
    public BufferId ArgsStart { get; }

    /// <summary>The ground map's heights, as depth: 0 where there is no ground.</summary>
    public ViewId Height { get; }

    /// <summary>The ground map's info.</summary>
    public ViewId Info { get; }

    /// <summary>The mapped uniforms of <paramref name="slot"/>.</summary>
    public void* Uniforms(int slot) => _uniforms[slot].Mapped;

    /// <summary>The mapped squares of <paramref name="slot"/>.</summary>
    public void* Tiles(int slot) => _tiles[slot].Mapped;

    // WebGPU can't write a buffer from inside an encoder, so each frame copies the arguments from this one.
    private static GpuBuffer CreateArgsStart(VulkanContext context)
    {
        var start = GpuBuffer.CreateHostVisible(context, GrassStage.ArgsSize, BufferUsageFlags.TransferSrcBit);
        var words = new Span<uint>(start.Mapped, GrassField.Details * 4);
        words.Clear();
        for (int i = 0; i < GrassField.Details; i++)
        {
            words[4 * i] = (uint)GrassField.DetailVertices[i];
        }

        return start;
    }

    private void WriteSets(Sampler nearest, ReadOnlySpan<DescriptorSet> sets)
    {
        var blades = new DescriptorBufferInfo(_blades.Handle, 0, _blades.Size);
        var args = new DescriptorBufferInfo(_args.Handle, 0, _args.Size);
        var height = new DescriptorImageInfo(nearest, _height.View, ImageLayout.ShaderReadOnlyOptimal);
        var info = new DescriptorImageInfo(nearest, _info.View, ImageLayout.ShaderReadOnlyOptimal);
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

        _gpu.Release(Args);
        _gpu.Release(ArgsStart);
        _gpu.Release(Height);
        _gpu.Release(Info);
        _blades.Dispose();
        _args.Dispose();
        _argsStart.Dispose();
        _height.Dispose();
        _info.Dispose();
    }
}
