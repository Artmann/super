using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Draws the environment's cubes from its uniforms and filters the blurred ones from them. The sharp levels are drawn
/// analytically by Shaders/environment.comp. The rough reflections are filtered from a private 256² source cube, and
/// the soft light from the sky cube, by Shaders/environment_filter.comp, with filtered importance sampling: each
/// sample reads its source at the mip whose texels cover about the solid angle the sample stands for, so a few samples
/// read pre-blurred texels instead of many reading sharp ones.
/// </summary>
internal sealed class EnvironmentPrefilter : IDisposable
{
    /// <summary>The cube the rough reflections are filtered from: the studio with its panels but no sun disc.</summary>
    private const uint SourceSize = 256;
    private const uint SourceMips = 9;

    /// <summary>
    /// Added to each filter sample's mip. Colbert and Krivanek add one level to hide the pattern a few samples leave,
    /// but with the sample counts below that blurs a rough lobe enough to pull the floor up into it, and no pattern shows.
    /// </summary>
    private const float LodBias = 0f;

    /// <summary>
    /// Filter samples for the first rough level, doubled on each smaller level: those have a quarter of the texels and the
    /// widest blur, so more samples there cost little and keep the floor from bleeding up into a rough lobe.
    /// </summary>
    private const uint ReflectionSamples = 32;

    /// <summary>The soft light is only 32² a face, so it takes plenty.</summary>
    private const uint IrradianceSamples = 256;

    /// <summary>Must match the Draw block in Shaders/environment.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public int Panels;
        public int Disc;
        public int Clouds;
    }

    /// <summary>Must match the Filter block in Shaders/environment_filter.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterConstants
    {
        public int Irradiance;
        public float Roughness;
        public float TexelAngle;
        public float LodBias;
        public uint Samples;
    }

    private readonly VulkanGpu _gpu;
    private readonly EnvironmentCube _source;
    private readonly (EnvironmentCube Reflections, EnvironmentCube Irradiance, EnvironmentCube Sky) _targets;

    /// <summary>What the draws read as the photographed sky while none is set: one black texel a face.</summary>
    private readonly GpuTexture _noImage;
    /// <summary>
    /// The draws: their shader, their groups in the order of <see cref="DrawTargets"/>, the Environment block every
    /// draw reads, and what they read besides the photographed sky, the sky-view table and the weather map.
    /// </summary>
    private readonly (CubeShader<DrawConstants> Shader, ImageGroup[] Groups, BufferId Uniforms, ImageView SkyView,
        (SamplerId Repeat, ImageView View) Weather) _draw;

    /// <summary>
    /// The filters: their shader, one group per reflection mip, where the first, drawn rather than filtered, has none,
    /// and the soft light's group.
    /// </summary>
    private readonly (CubeShader<FilterConstants> Shader, ImageGroup[] Reflections, ImageGroup Irradiance) _filter;
    private readonly RoughSlices _slices = new();

    /// <param name="uniforms">The Environment block and the physical sky's sky-view table, which environment.comp reads.</param>
    /// <param name="weather">The clouds' weather map and the repeating sampler it is read with, for the clouds the captures draw.</param>
    public EnvironmentPrefilter((VulkanContext Context, VulkanGpu Gpu) device, EnvironmentUniforms uniforms,
        (EnvironmentCube Reflections, EnvironmentCube Irradiance, EnvironmentCube Sky) targets, (SamplerId Repeat, ImageView View) weather)
    {
        var (context, gpu) = device;
        _gpu = gpu;
        _targets = targets;
        _source = new EnvironmentCube(context, SourceSize, SourceMips);

        // The photographed sky and the filters' sources are read between mips.
        var sampler = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear, Mipmapped: true));
        var black = new TextureData
        {
            Width = 1, Height = 1, Kind = TextureKind.Linear, Format = TextureFormat.Rgba16f, Mips = [new byte[8]],
        };
        _noImage = GpuTexture.Cube(context, [black, black, black, black, black, black]);

        _draw = (new CubeShader<DrawConstants>(device, "environment.comp"),
            [new ImageGroup(gpu, sampler), new ImageGroup(gpu, sampler), new ImageGroup(gpu, sampler)],
            gpu.Adopt(uniforms.Buffer.Handle, (long)uniforms.Buffer.Size), uniforms.SkyView, weather);
        SetImage(null);

        var filter = new CubeShader<FilterConstants>(device, "environment_filter.comp");
        var reflections = new ImageGroup[_targets.Reflections.MipCount];
        for (int mip = 1; mip < reflections.Length; mip++)
        {
            reflections[mip] = Filter(filter, sampler, _source.Sampled, _targets.Reflections.Storage(mip));
        }

        _filter = (filter, reflections, Filter(filter, sampler, _targets.Sky.Sampled, _targets.Irradiance.Storage(0)));
    }

    /// <summary>The cubes the draws write, each the sharpest level of its own.</summary>
    private EnvironmentCube[] DrawTargets => [_source, _targets.Reflections, _targets.Sky];

    /// <summary>True while a rough reflection level still shows an earlier sky than the sharp ones.</summary>
    public bool Unfinished => _slices.Unfinished;

    /// <summary>The sharp environment with its panels but without the sun's disc, every mip.</summary>
    public ImageView Background => _source.Sampled;

    /// <summary>
    /// Points the draws at a photographed sky's cube, or at none. It makes their bind groups again, so the GPU must
    /// have finished every draw recorded before.
    /// </summary>
    public void SetImage(GpuTexture? image)
    {
        var view = image?.View ?? _noImage.View;
        var targets = DrawTargets;
        for (int i = 0; i < _draw.Groups.Length; i++)
        {
            _draw.Groups[i].Make(_draw.Shader.Pipeline, 0,
            [
                new GroupImage("target", targets[i].Storage(0), Use: GroupImageUse.Written),
                new GroupImage("skyView", _draw.SkyView, Use: GroupImageUse.Stored),
                ("skyImage", view),
                new GroupImage("cloudWeather", _draw.Weather.View, _draw.Weather.Repeat, GroupImageUse.Stored),
            ],
            [("environment", _draw.Uniforms)]);
        }
    }

    // A filter's group, which reads a cube through all its mips and writes one level of another.
    private ImageGroup Filter(CubeShader<FilterConstants> filter, SamplerId sampler, ImageView source, ImageView target)
    {
        var group = new ImageGroup(_gpu, sampler);
        group.Make(filter.Pipeline, 0, [("source", source), new GroupImage("target", target, Use: GroupImageUse.Written)]);
        return group;
    }

    /// <summary>
    /// Records the draws and filters, leaving every level of the three cubes ready for any shader to sample. The
    /// uniforms must already be written and visible to compute shaders.
    /// </summary>
    public void Record(CommandBuffer cmd)
    {
        RecordSharp(cmd);
        RecordRough(cmd, RoughSlices.All);
        _slices.Clear();
    }

    /// <summary>
    /// Records a frame's share. After a change, <paramref name="changed"/>, the sharp levels, the sky and the soft
    /// light all at once, since they show the most; then, while any are out of date, the next slice of the rough
    /// reflections, filtered from the source cube the last change drew. The uniforms must already be written.
    /// </summary>
    public void RecordFrame(CommandBuffer cmd, bool changed)
    {
        if (changed)
        {
            RecordSharp(cmd);
            _slices.Restart();
        }

        if (_slices.Unfinished)
        {
            RecordRough(cmd, _slices.Take());
        }
    }

    /// <summary>Records the source cube, the reflections' sharpest level, the sky and the soft light.</summary>
    private void RecordSharp(CommandBuffer cmd)
    {
        _draw.Shader.Record(cmd, _draw.Groups[0].Id, _source.Level(0), new DrawConstants { Panels = 1 });
        _source.BuildMips(cmd);

        _draw.Shader.Record(cmd, _draw.Groups[1].Id, _targets.Reflections.Level(0), new DrawConstants { Panels = 1, Disc = 1, Clouds = 1 });
        _targets.Reflections.Ready(cmd, 0);

        _draw.Shader.Record(cmd, _draw.Groups[2].Id, _targets.Sky.Level(0), new DrawConstants { Clouds = 1 });
        _targets.Sky.BuildMips(cmd);
        _filter.Shader.Record(cmd, _filter.Irradiance.Id, _targets.Irradiance.Level(0), new FilterConstants
        {
            Irradiance = 1,
            TexelAngle = TexelAngle(_targets.Sky.Size),
            LodBias = LodBias,
            Samples = IrradianceSamples,
        });
        _targets.Irradiance.Ready(cmd, 0);
    }

    /// <summary>Records these rough reflection levels, filtered from the source cube.</summary>
    private void RecordRough(CommandBuffer cmd, IEnumerable<int> mips)
    {
        int roughest = (int)_targets.Reflections.MipCount - 1;
        foreach (int mip in mips)
        {
            _filter.Shader.Record(cmd, _filter.Reflections[mip].Id, _targets.Reflections.Level(mip), new FilterConstants
            {
                Roughness = mip / (float)roughest,
                TexelAngle = TexelAngle(SourceSize),
                LodBias = LodBias,
                Samples = ReflectionSamples << (mip - 1),
            });
            _targets.Reflections.Ready(cmd, mip);
        }
    }

    public void Dispose()
    {
        foreach (var group in _draw.Groups)
        {
            group.Dispose();
        }

        foreach (var group in _filter.Reflections)
        {
            group?.Dispose();
        }

        _filter.Irradiance.Dispose();
        _gpu.Release(_draw.Uniforms);
        _filter.Shader.Dispose();
        _draw.Shader.Dispose();
        _noImage.Dispose();
        _source.Dispose();
    }

    /// <summary>The solid angle of one texel of a cube's sharpest level, on average.</summary>
    private static float TexelAngle(uint size) => 4f * MathF.PI / (6f * size * size);
}
