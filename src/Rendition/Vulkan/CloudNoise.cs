using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The noise the clouds are made of, which cloud_noise.comp fills once and which tiles, so the
/// <see cref="Repeat"/> sampler reads it. The small weather map is made at once, since the sky captures read it whether
/// or not the clouds are drawn yet; the shape and detail volumes are made the first time the clouds are drawn and
/// freed while they're switched off.
/// </summary>
internal sealed unsafe class CloudNoise : IDisposable
{
    /// <summary>What cloud_noise.comp fills. Must match its push constant.</summary>
    private enum NoiseKind
    {
        Shape = 0,
        Detail = 1,
        Weather = 2,
    }

    private const int ShapeSize = 128;
    private const int DetailSize = 32;
    private const int WeatherSize = 512;

    private readonly VulkanContext _context;
    private readonly CellVolume _weather;
    private (CellVolume Shape, CellVolume Detail)? _volumes;

    public CloudNoise(VulkanContext context)
    {
        _context = context;
        Repeat = CreateRepeatSampler(context);
        _weather = Make((NoiseKind.Weather, (WeatherSize, WeatherSize, 1)))[0];
    }

    /// <summary>The sampler every noise texture is read with, which repeats them.</summary>
    public Sampler Repeat { get; }

    /// <summary>The weather map: coverage, type and the cirrus.</summary>
    public ImageView Weather => _weather.View;

    /// <summary>The shape and detail volumes, once <see cref="MakeVolumes"/> has made them.</summary>
    public (ImageView Shape, ImageView Detail)? Volumes => _volumes is { } made ? (made.Shape.View, made.Detail.View) : null;

    /// <summary>Makes and fills the shape and detail volumes, if they aren't there, waiting for the GPU.</summary>
    public void MakeVolumes()
    {
        if (_volumes is null)
        {
            var made = Make((NoiseKind.Shape, (ShapeSize, ShapeSize, ShapeSize)), (NoiseKind.Detail, (DetailSize, DetailSize, DetailSize)));
            _volumes = (made[0], made[1]);
        }
    }

    // Frees the shape and detail volumes, with the noise. They are kept from when they are made until then.
    private void ReleaseVolumes()
    {
        if (_volumes is { } made)
        {
            made.Shape.Dispose();
            made.Detail.Dispose();
            _volumes = null;
        }
    }

    public void Dispose()
    {
        ReleaseVolumes();
        _weather.Dispose();
        _context.Api.DestroySampler(_context.Device, Repeat, null);
    }

    private static Sampler CreateRepeatSampler(VulkanContext context)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxLod = 0f,
        };
        context.Api.CreateSampler(context.Device, &info, null, out var sampler).Check();
        return sampler;
    }

    // Makes noise textures of these kinds and sizes and fills them, waiting for each.
    private CellVolume[] Make(params ReadOnlySpan<(NoiseKind Kind, (int X, int Y, int Z) Size)> textures)
    {
        var api = _context.Api;
        var volumes = new CellVolume[textures.Length];
        var layout = _context.CreateSetLayout([new(0, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit)]);
        var pool = _context.CreateDescriptorPool(1, new DescriptorPoolSize(DescriptorType.StorageImage, 1));
        var set = _context.AllocateSet(pool, layout);
        var pipelineLayout = _context.CreatePipelineLayout([layout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, sizeof(int)));
        var pipeline = PipelineFactory.CreateCompute(_context, "cloud_noise.comp", pipelineLayout);
        try
        {
            for (int i = 0; i < textures.Length; i++)
            {
                volumes[i] = new CellVolume(_context, textures[i].Size, new ClearColorValue(0f, 0f, 0f, 0f));
                Fill(volumes[i], textures[i], set, (pipelineLayout, pipeline));
            }
        }
        finally
        {
            pipeline.Destroy();
            api.DestroyPipelineLayout(_context.Device, pipelineLayout, null);
            api.DestroyDescriptorPool(_context.Device, pool, null);
            api.DestroyDescriptorSetLayout(_context.Device, layout, null);
        }

        return volumes;
    }

    // Points the noise pass's set at the volume and fills it with noise of this kind, waiting for the GPU.
    private void Fill(CellVolume volume, (NoiseKind Kind, (int X, int Y, int Z) Size) texture, DescriptorSet set, (PipelineLayout Layout, LivePipeline Pipeline) pass)
    {
        var api = _context.Api;
        var info = new DescriptorImageInfo(default, volume.View, ImageLayout.General);
        var write = Descriptors.ImageWrite(set, 0, DescriptorType.StorageImage, &info);
        api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        _context.SubmitAndWait(cmd =>
        {
            int value = (int)texture.Kind;
            var bound = set;
            var size = texture.Size;
            api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pass.Pipeline);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pass.Layout, 0, 1, &bound, 0, null);
            api.CmdPushConstants(cmd, pass.Layout, ShaderStageFlags.ComputeBit, 0, sizeof(int), &value);
            api.CmdDispatch(cmd, (uint)(size.X + 3) / 4, (uint)(size.Y + 3) / 4, (uint)(size.Z + 3) / 4);
            api.MemoryBarrier(cmd,
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));
        });
    }
}
