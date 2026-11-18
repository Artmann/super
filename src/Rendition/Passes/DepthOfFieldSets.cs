using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The half-size layers depth of field works in: each pixel's circle, and the near and far layers gathered from it.
/// </summary>
internal sealed class DepthOfFieldLayers : IDisposable
{
    public DepthOfFieldLayers(VulkanContext context, Extent2D half)
    {
        Half = half;
        Coc = Create(context, half);
        Near = Create(context, half);
        Far = Create(context, half);
    }

    public Extent2D Half { get; }

    public RenderTarget Coc { get; }

    public RenderTarget Near { get; }

    public RenderTarget Far { get; }

    public void Dispose()
    {
        Coc.Dispose();
        Near.Dispose();
        Far.Dispose();
    }

    private static RenderTarget Create(VulkanContext context, Extent2D half) =>
        new(context, GBuffer.HdrFormat, half, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
}

/// <summary>
/// The three descriptor sets depth of field's passes read, three images each: the circle pass reads the picture and
/// depth, the blur reads the circles, and the combine reads the near and far layers.
/// </summary>
internal sealed unsafe class DepthOfFieldSets : IDisposable
{
    private readonly VulkanContext _context;
    private readonly (Sampler Point, Sampler Linear) _samplers;
    private readonly DescriptorPool _pool;

    public DepthOfFieldSets(VulkanContext context, (Sampler Point, Sampler Linear) samplers)
    {
        _context = context;
        _samplers = samplers;
        Layout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        ]);

        const int setCount = 3;
        _pool = context.CreateDescriptorPool(setCount, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, setCount * 3));
        Coc = context.AllocateSet(_pool, Layout);
        Blur = context.AllocateSet(_pool, Layout);
        Combine = context.AllocateSet(_pool, Layout);
    }

    public DescriptorSetLayout Layout { get; }

    public DescriptorSet Coc { get; }

    public DescriptorSet Blur { get; }

    public DescriptorSet Combine { get; }

    /// <summary>Points the sets at the picture blurred, at depth, which glass leaves read-only, and at the layers.</summary>
    public void Write(ImageView source, ImageView depth, DepthOfFieldLayers layers)
    {
        var (point, linear) = _samplers;
        var depthInfo = new DescriptorImageInfo(point, depth, ImageLayout.DepthReadOnlyOptimal);
        Write(Coc,
        [
            new(linear, source, ImageLayout.ShaderReadOnlyOptimal),
            depthInfo,
            new(linear, source, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        Write(Blur,
        [
            new(point, layers.Coc.View, ImageLayout.ShaderReadOnlyOptimal),
            depthInfo,
            new(linear, layers.Coc.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
        Write(Combine,
        [
            new(linear, layers.Near.View, ImageLayout.ShaderReadOnlyOptimal),
            depthInfo,
            new(linear, layers.Far.View, ImageLayout.ShaderReadOnlyOptimal),
        ]);
    }

    public void Dispose()
    {
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, Layout, null);
    }

    private void Write(DescriptorSet set, ReadOnlySpan<DescriptorImageInfo> images)
    {
        var writes = stackalloc WriteDescriptorSet[images.Length];
        fixed (DescriptorImageInfo* imagesPtr = images)
        {
            for (int i = 0; i < images.Length; i++)
            {
                writes[i] = Descriptors.ImageWrite(set, (uint)i, DescriptorType.CombinedImageSampler, &imagesPtr[i]);
            }

            _context.Api.UpdateDescriptorSets(_context.Device, (uint)images.Length, writes, 0, null);
        }
    }
}
