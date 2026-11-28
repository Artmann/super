using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// An image for an <see cref="ImageGroup"/>: the binding name its shader gives it, the view, and the sampler to read
/// it through, or the group's own when left out. A <see cref="ReadOnlyDepth"/> image is depth, sampled while it rests
/// in <see cref="ImageLayout.DepthReadOnlyOptimal"/>.
/// </summary>
internal readonly record struct GroupImage(string Name, ImageView View, SamplerId Sampler = default, bool ReadOnlyDepth = false)
{
    public static implicit operator GroupImage((string Name, ImageView View) image) => new(image.Name, image.View);
}

/// <summary>
/// A bind group of images made outside the layer, each bound by the name its shader gives it. Made again whenever the
/// images change, which must only happen while the GPU is idle.
/// </summary>
internal sealed class ImageGroup(VulkanGpu gpu, SamplerId sampler) : IDisposable
{
    private ViewId[] _views = [];

    /// <summary>The group to set, or 0 before the first <see cref="Make"/>.</summary>
    public BindGroupId Id { get; private set; }

    /// <summary>Lets go of the group and its images, then makes it again for <paramref name="pipeline"/>'s group <paramref name="group"/>.</summary>
    public void Make(PipelineId pipeline, int group, ReadOnlySpan<GroupImage> images)
    {
        Dispose();
        if (_views.Length != images.Length)
        {
            _views = new ViewId[images.Length];
        }

        var binds = new Bind[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            var image = images[i];
            _views[i] = image.ReadOnlyDepth ? gpu.AdoptDepth(image.View) : gpu.Adopt(image.View);
            binds[i] = Bind.Image(image.Name, _views[i], image.Sampler.Value == 0 ? sampler : image.Sampler);
        }

        Id = gpu.CreateBindGroup(pipeline, group, binds);
    }

    public void Dispose()
    {
        if (Id.Value == 0)
        {
            return;
        }

        gpu.Destroy(Id);
        foreach (var view in _views)
        {
            gpu.Release(view);
        }

        Id = default;
    }
}
