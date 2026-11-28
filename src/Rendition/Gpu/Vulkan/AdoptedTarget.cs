using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// A target a pass draws to but doesn't own, adopted into the layer. It is adopted again only when the target is
/// remade: a new view, a new image or a new size. The image is checked as well as the view because a driver can
/// give a remade view the old handle.
/// </summary>
internal sealed class AdoptedTarget(VulkanGpu gpu) : IDisposable
{
    private (ImageView View, Image Image, Extent2D Extent, ViewId Id) _adopted;

    /// <summary>The id to draw to <paramref name="target"/>, which is <paramref name="extent"/> big.</summary>
    public ViewId For(RenderTarget target, Extent2D extent)
    {
        // Silk's Extent2D.Equals boxes, so the sides are compared one by one.
        var (view, image) = (target.View, target.Image);
        if (_adopted.View.Handle != view.Handle || _adopted.Image.Handle != image.Handle
            || _adopted.Extent.Width != extent.Width || _adopted.Extent.Height != extent.Height)
        {
            Dispose();
            _adopted = (view, image, extent, gpu.Adopt(view, image, extent));
        }

        return _adopted.Id;
    }

    public void Dispose()
    {
        if (_adopted.Id.Value != 0)
        {
            gpu.Release(_adopted.Id);
            _adopted = default;
        }
    }
}
