using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>How an <see cref="ImageGroup"/> binds an image, and so the layout the image rests in between passes.</summary>
internal enum GroupImageUse
{
    /// <summary>Read through a sampler, resting in <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.</summary>
    Sampled,

    /// <summary>Depth read through a sampler, resting in <see cref="ImageLayout.DepthReadOnlyOptimal"/>, as glass leaves it.</summary>
    Depth,

    /// <summary>An image a compute pass writes, read through a sampler while it rests in <see cref="ImageLayout.General"/>.</summary>
    Stored,

    /// <summary>Bound for a compute shader to write, resting in <see cref="ImageLayout.General"/>.</summary>
    Written,
}

/// <summary>
/// An image for an <see cref="ImageGroup"/>: the binding name its shader gives it, the view, the sampler to read it
/// through, or the group's own when left out, and how the group binds it.
/// </summary>
internal readonly record struct GroupImage(string Name, ImageView View, SamplerId Sampler = default, GroupImageUse Use = GroupImageUse.Sampled)
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
            var use = image.Use;
            _views[i] = gpu.Adopt(new AdoptedView(
                image.View, default, default, ReadOnlyDepth: use == GroupImageUse.Depth, Storage: use >= GroupImageUse.Stored));
            binds[i] = use == GroupImageUse.Written
                ? Bind.StorageImage(image.Name, _views[i])
                : Bind.Image(image.Name, _views[i], image.Sampler.Value == 0 ? sampler : image.Sampler);
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
