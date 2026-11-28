using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The noise the clouds are made of, which cloud_noise.comp fills once through the GPU layer and which tiles, so a
/// repeating sampler reads it. The small weather map is made at once, since the sky captures read it whether or not the
/// clouds are drawn yet; the shape and detail volumes are made the first time the clouds are drawn and freed while
/// they're switched off.
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
    private readonly VulkanGpu _gpu;
    private readonly CellVolume _weather;
    private (CellVolume Shape, CellVolume Detail)? _volumes;

    public CloudNoise(VulkanContext context, VulkanGpu gpu)
    {
        _context = context;
        _gpu = gpu;
        RepeatId = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear, SamplerAddress.Repeat));
        _weather = Make((NoiseKind.Weather, (WeatherSize, WeatherSize, 1)))[0];
    }

    /// <summary>The layer's sampler every noise texture is read with, which repeats them.</summary>
    public SamplerId RepeatId { get; }

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
    }

    // Makes noise textures of these kinds and sizes and fills them, waiting for each.
    private CellVolume[] Make(params ReadOnlySpan<(NoiseKind Kind, (int X, int Y, int Z) Size)> textures)
    {
        var volumes = new CellVolume[textures.Length];
        using var pass = new ComputePass(_gpu, "cloud_noise.comp");
        using var group = new ImageGroup(_gpu, default);
        var commands = new CommandList();
        for (int i = 0; i < textures.Length; i++)
        {
            var (kind, size) = textures[i];
            volumes[i] = new CellVolume(_context, size, new ClearColorValue(0f, 0f, 0f, 0f));
            group.Make(pass.Pipeline, 0, [new("target", volumes[i].View, Use: GroupImageUse.Written)]);
            int value = (int)kind;
            commands.Reset();
            pass.Record(commands, [group.Id], new ReadOnlySpan<byte>(&value, sizeof(int)), ((size.X + 3) / 4, (size.Y + 3) / 4, (size.Z + 3) / 4));
            _context.SubmitAndWait(cmd => _gpu.Play(commands, cmd));
        }

        return volumes;
    }
}
