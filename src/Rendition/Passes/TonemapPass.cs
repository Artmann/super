using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Tonemaps the picture, with bloom and the lens flare added, into Ldr. The first pass drawn through the GPU layer:
/// its own images are a bind group made by binding name, and the frame's, the meter's and the grade's sets, which
/// passes that haven't moved still make, are adopted beside it.
/// </summary>
internal sealed class TonemapPass : IDisposable
{
    private readonly VulkanGpu _gpu;
    private readonly FullscreenPass _pass;
    private readonly CommandList _commands = new();
    private readonly ImageGroup _sources;
    private readonly AdoptedTarget _ldr;

    public TonemapPass(VulkanGpu gpu, DescriptorKit kit, DescriptorSetLayout meterLayout)
    {
        _gpu = gpu;
        _ldr = new AdoptedTarget(gpu);
        _sources = new ImageGroup(gpu, gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));
        // Group 1 is the pass's own, laid out from tonemap.frag; the rest are lent by the passes that fill them.
        _pass = new FullscreenPass(gpu, "tonemap.frag", [GpuFormat.Rgba8UnormSrgb],
            borrowed: [kit.FrameSetLayout, default, meterLayout, kit.OneTextureLayout]);
    }

    /// <summary>
    /// Points the pass at the picture, bloom and the flare. Makes its bind group again, so it must only be called
    /// while the GPU is idle, as every caller of <see cref="PostStage.WriteSets"/> is.
    /// </summary>
    public void SetSources(ImageView source, ImageView bloom, ImageView flare) =>
        _sources.Make(_pass.Pipeline, 1, [("hdrTexture", source), ("bloomTexture", bloom), ("flareTexture", flare)]);

    /// <summary>
    /// Tonemaps into <paramref name="ldr"/>, the target and its size, reading the frame's, the meter's and the
    /// grade's sets.
    /// </summary>
    public void Record(CommandBuffer cmd, (RenderTarget Target, Extent2D Extent) ldr, (DescriptorSet Frame, DescriptorSet Meter, DescriptorSet Grade) sets)
    {
        _commands.Reset();
        _pass.Record(_commands, [new ColorTarget(_ldr.For(ldr.Target, ldr.Extent), LoadOp.DontCare, default)],
            [_gpu.Adopt(sets.Frame), _sources.Id, _gpu.Adopt(sets.Meter), _gpu.Adopt(sets.Grade)], []);
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        _sources.Dispose();
        _ldr.Dispose();
        _pass.Dispose();
    }
}
