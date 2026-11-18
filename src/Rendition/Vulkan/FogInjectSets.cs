using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The volumetric fog passes' two descriptor sets, one for each of the volumes the inject pass writes in turn: the
/// sun's shadow, the sky, the light probes' atlases, the history it reads, the volume it writes and the sum.
/// </summary>
internal sealed unsafe class FogInjectSets : IDisposable
{
    // Where probes.glsl reads the live light probes' atlases, in every set that includes it.
    private const uint ProbeIrradianceBinding = 11;
    private const uint ProbeDepthBinding = 12;

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly DescriptorPool _pool;

    public readonly DescriptorSetLayout Layout;
    public readonly DescriptorSet[] Sets = new DescriptorSet[2];

    /// <summary>Makes the sets, with the fog volumes' buffer at <paramref name="volumesBinding"/>.</summary>
    public FogInjectSets(VulkanContext context, Sampler linearSampler, uint volumesBinding)
    {
        _context = context;
        _sampler = linearSampler;
        Layout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(volumesBinding, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(ProbeIrradianceBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(ProbeDepthBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            2,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 10),
            new DescriptorPoolSize(DescriptorType.StorageImage, 4),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 2));
        context.AllocateSets(_pool, Layout, Sets);
    }

    /// <summary>
    /// Points both sets at the sun's shadow cascades, the sky, and the live light probes' two atlases, which
    /// probes.glsl reads at bindings 11 and 12.
    /// </summary>
    public void SetSources(
        in DescriptorImageInfo sunShadow, in DescriptorImageInfo sky,
        in DescriptorImageInfo probeIrradiance, in DescriptorImageInfo probeDepth)
    {
        var infos = stackalloc DescriptorImageInfo[] { sunShadow, sky, probeIrradiance, probeDepth };
        uint* bindings = stackalloc uint[] { 0, 1, ProbeIrradianceBinding, ProbeDepthBinding };
        var writes = stackalloc WriteDescriptorSet[8];
        for (int i = 0; i < 2; i++)
        {
            for (int s = 0; s < 4; s++)
            {
                writes[4 * i + s] = Descriptors.ImageWrite(Sets[i], bindings[s], DescriptorType.CombinedImageSampler, &infos[s]);
            }
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 8, writes, 0, null);
    }

    /// <summary>
    /// Points the sets at the fog's volumes: set i writes injected volume i and reads the other as history, and both
    /// write the sum.
    /// </summary>
    public void PointAt(ImageView first, ImageView second, ImageView sum)
    {
        var injected = stackalloc ImageView[] { first, second };
        var summed = new DescriptorImageInfo(default, sum, ImageLayout.General);
        var history = stackalloc DescriptorImageInfo[2];
        var written = stackalloc DescriptorImageInfo[2];
        var writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 2; i++)
        {
            history[i] = new DescriptorImageInfo(_sampler, injected[1 - i], ImageLayout.General);
            written[i] = new DescriptorImageInfo(default, injected[i], ImageLayout.General);
            writes[3 * i] = Descriptors.ImageWrite(Sets[i], 2, DescriptorType.CombinedImageSampler, &history[i]);
            writes[3 * i + 1] = Descriptors.ImageWrite(Sets[i], 3, DescriptorType.StorageImage, &written[i]);
            writes[3 * i + 2] = Descriptors.ImageWrite(Sets[i], 4, DescriptorType.StorageImage, &summed);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    public void Dispose()
    {
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, Layout, null);
    }
}
