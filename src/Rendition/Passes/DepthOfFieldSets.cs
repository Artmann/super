using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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
/// The bind groups depth of field's passes read: the circle pass reads the picture and depth, the blur reads the
/// circles, and the combine reads the near and far layers and depth. Colour is read through a linear sampler, and depth
/// and the circles through a nearest one.
/// </summary>
internal sealed class DepthOfFieldSets : IDisposable
{
    private readonly SamplerId _point;

    public DepthOfFieldSets(VulkanGpu gpu)
    {
        _point = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest));
        var linear = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        Coc = new ImageGroup(gpu, linear);
        Blur = new ImageGroup(gpu, linear);
        Combine = new ImageGroup(gpu, linear);
    }

    public ImageGroup Coc { get; }

    public ImageGroup Blur { get; }

    public ImageGroup Combine { get; }

    /// <summary>
    /// Points the groups for <paramref name="pipelines"/> at the picture blurred, at depth, which glass leaves
    /// read-only, and at the layers. Frames still in flight must have finished.
    /// </summary>
    public void Write(ImageView source, ImageView depth, DepthOfFieldLayers layers, (PipelineId Coc, PipelineId Blur, PipelineId Combine) pipelines)
    {
        var depthImage = new GroupImage("depthTexture", depth, _point, ReadOnlyDepth: true);
        Coc.Make(pipelines.Coc, 1, [("sourceTexture", source), depthImage]);
        Blur.Make(pipelines.Blur, 1, [new GroupImage("circleTexture", layers.Coc.View, _point)]);
        Combine.Make(pipelines.Combine, 1, [("nearTexture", layers.Near.View), depthImage, ("farTexture", layers.Far.View)]);
    }

    public void Dispose()
    {
        Coc.Dispose();
        Blur.Dispose();
        Combine.Dispose();
    }
}
