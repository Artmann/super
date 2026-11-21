using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The cloud passes' two descriptor sets, as Shaders/cloud_sets.glsl lays them out, one for each of the two histories
/// the resolve pass writes in turn: the noise, the sky, the march, the picture, the shadow map, and last frame's
/// picture and this frame's.
/// </summary>
internal sealed unsafe class CloudSets : IDisposable
{
    private const uint ShapeBinding = 0;
    private const uint DetailBinding = 1;
    private const uint WeatherBinding = 2;
    private const uint SkyViewBinding = 3;
    private const uint MarchTargetBinding = 4;
    private const uint MarchBinding = 5;
    private const uint HistoryBinding = 6;
    private const uint PictureBinding = 7;
    private const uint ShadowBinding = 8;
    private const uint NextHistoryBinding = 9;

    private readonly VulkanContext _context;
    private readonly DescriptorPool _pool;

    public readonly DescriptorSetLayout Layout;
    public readonly DescriptorSet[] Sets = new DescriptorSet[2];

    public CloudSets(VulkanContext context)
    {
        _context = context;
        const ShaderStageFlags compute = ShaderStageFlags.ComputeBit;
        Layout = context.CreateSetLayout(
        [
            new(ShapeBinding, DescriptorType.CombinedImageSampler, 1, compute),
            new(DetailBinding, DescriptorType.CombinedImageSampler, 1, compute),
            new(WeatherBinding, DescriptorType.CombinedImageSampler, 1, compute),
            new(SkyViewBinding, DescriptorType.CombinedImageSampler, 1, compute),
            new(MarchTargetBinding, DescriptorType.StorageImage, 1, compute),
            new(MarchBinding, DescriptorType.CombinedImageSampler, 1, compute),
            new(HistoryBinding, DescriptorType.CombinedImageSampler, 1, compute),
            new(PictureBinding, DescriptorType.StorageImage, 1, compute),
            new(ShadowBinding, DescriptorType.StorageImage, 1, compute),
            new(NextHistoryBinding, DescriptorType.StorageImage, 1, compute),
        ]);
        _pool = context.CreateDescriptorPool(
            2,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 12),
            new DescriptorPoolSize(DescriptorType.StorageImage, 8));
        context.AllocateSets(_pool, Layout, Sets);
    }

    /// <summary>Points both sets at the noise, read through the repeating sampler, and at the physical sky's sky-view table.</summary>
    public void SetSources(Sampler repeat, (ImageView Shape, ImageView Detail, ImageView Weather) noise, in DescriptorImageInfo skyView)
    {
        var infos = stackalloc DescriptorImageInfo[]
        {
            new(repeat, noise.Shape, ImageLayout.General),
            new(repeat, noise.Detail, ImageLayout.General),
            new(repeat, noise.Weather, ImageLayout.General),
            skyView,
        };
        var writes = stackalloc WriteDescriptorSet[8];
        for (int i = 0; i < 2; i++)
        {
            for (int s = 0; s < 4; s++)
            {
                writes[4 * i + s] = Descriptors.ImageWrite(Sets[i], ShapeBinding + (uint)s, DescriptorType.CombinedImageSampler, &infos[s]);
            }
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 8, writes, 0, null);
    }

    /// <summary>
    /// Points the sets at the targets: set i writes history i and reads the other as last frame's, and both write the
    /// march, the picture and the shadow map and read the march.
    /// </summary>
    public void PointAt(Sampler linear, (ImageView March, ImageView Picture, ImageView Shadow) targets, ImageView first, ImageView second)
    {
        var histories = stackalloc ImageView[] { first, second };
        var marchTarget = new DescriptorImageInfo(default, targets.March, ImageLayout.General);
        var march = new DescriptorImageInfo(linear, targets.March, ImageLayout.General);
        var picture = new DescriptorImageInfo(default, targets.Picture, ImageLayout.General);
        var shadow = new DescriptorImageInfo(default, targets.Shadow, ImageLayout.General);
        var previous = stackalloc DescriptorImageInfo[2];
        var next = stackalloc DescriptorImageInfo[2];
        var writes = stackalloc WriteDescriptorSet[12];
        for (int i = 0; i < 2; i++)
        {
            previous[i] = new DescriptorImageInfo(linear, histories[1 - i], ImageLayout.General);
            next[i] = new DescriptorImageInfo(default, histories[i], ImageLayout.General);
            writes[6 * i] = Descriptors.ImageWrite(Sets[i], MarchTargetBinding, DescriptorType.StorageImage, &marchTarget);
            writes[6 * i + 1] = Descriptors.ImageWrite(Sets[i], MarchBinding, DescriptorType.CombinedImageSampler, &march);
            writes[6 * i + 2] = Descriptors.ImageWrite(Sets[i], HistoryBinding, DescriptorType.CombinedImageSampler, &previous[i]);
            writes[6 * i + 3] = Descriptors.ImageWrite(Sets[i], PictureBinding, DescriptorType.StorageImage, &picture);
            writes[6 * i + 4] = Descriptors.ImageWrite(Sets[i], ShadowBinding, DescriptorType.StorageImage, &shadow);
            writes[6 * i + 5] = Descriptors.ImageWrite(Sets[i], NextHistoryBinding, DescriptorType.StorageImage, &next[i]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 12, writes, 0, null);
    }

    public void Dispose()
    {
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, Layout, null);
    }
}
