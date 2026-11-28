using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Draws the finished picture, or the held one while a transition shows it, into the window's image, through the
/// GPU layer. The window's images are adopted as surfaces, one each, so the layer leaves their layouts to
/// <see cref="FinishStage"/>, which draws the marks and the overlay into the same image after.
/// </summary>
internal sealed class FinishPass : IDisposable
{
    private readonly VulkanGpu _gpu;
    private readonly FullscreenPass _pass;
    private readonly CommandList _commands = new();
    private readonly ImageGroup _sources;
    private AdoptedTarget[] _surfaces = [];

    public FinishPass(VulkanGpu gpu, DescriptorKit kit, (Format Format, DescriptorSetLayout Resources) window)
    {
        _gpu = gpu;
        _sources = new ImageGroup(gpu, gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));
        // Group 1 is the pass's own, laid out from finish.frag; the frame's set and the window's resources are lent.
        _pass = new FullscreenPass(gpu, "finish.frag", [VulkanFormats.ToGpu(window.Format)],
            borrowed: [kit.FrameSetLayout, default, window.Resources]);
    }

    /// <summary>
    /// Points the pass at the finished picture and the held one. Makes its bind group again, so it must only be
    /// called while the GPU is idle.
    /// </summary>
    public void SetSources(ImageView picture, ImageView held) =>
        _sources.Make(_pass.Pipeline, 1, [("sourceTexture", picture), ("heldTexture", held)]);

    /// <summary>
    /// Draws into the swapchain's <paramref name="window"/> image, which must already be a colour attachment, reading
    /// the frame's set and the window's resources.
    /// </summary>
    public void Record(
        CommandBuffer cmd, (Swapchain Swapchain, uint Image) window, (DescriptorSet Frame, DescriptorSet Resources) sets,
        ReadOnlySpan<byte> constants)
    {
        var swapchain = window.Swapchain;
        if (_surfaces.Length != swapchain.Images.Length)
        {
            Release();
            _surfaces = new AdoptedTarget[swapchain.Images.Length];
            for (int i = 0; i < _surfaces.Length; i++)
            {
                _surfaces[i] = new AdoptedTarget(_gpu);
            }
        }

        int index = (int)window.Image;
        var surface = _surfaces[index].ForSurface(swapchain.Views[index], swapchain.Images[index], swapchain.Extent);
        _commands.Reset();
        _pass.Record(_commands, [new ColorTarget(surface, LoadOp.DontCare, default)],
            [_gpu.Adopt(sets.Frame), _sources.Id, _gpu.Adopt(sets.Resources)], constants);
        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        Release();
        _sources.Dispose();
        _pass.Dispose();
    }

    private void Release()
    {
        foreach (var surface in _surfaces)
        {
            surface.Dispose();
        }
    }
}
