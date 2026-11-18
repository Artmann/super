using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Draws the environment's cubes from its uniforms and filters the blurred ones from them. The sharp levels are drawn
/// analytically by Shaders/environment.comp. The rough reflections are filtered from a private 256² source cube, and
/// the soft light from the sky cube, by Shaders/environment_filter.comp, with filtered importance sampling: each
/// sample reads its source at the mip whose texels cover about the solid angle the sample stands for, so a few samples
/// read pre-blurred texels instead of many reading sharp ones.
/// </summary>
internal sealed unsafe class EnvironmentPrefilter : IDisposable
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

    private readonly EnvironmentCube _source;
    private readonly (EnvironmentCube Reflections, EnvironmentCube Irradiance, EnvironmentCube Sky) _targets;
    private readonly Sampler _sampler;

    /// <summary>What the draws read as the photographed sky while none is set: one black texel a face.</summary>
    private readonly GpuTexture _noImage;
    private readonly CubeShader<DrawConstants> _draw;
    private readonly CubeShader<FilterConstants> _filter;
    private readonly (DescriptorSet Source, DescriptorSet Reflections, DescriptorSet Sky) _drawSets;

    /// <summary>One set per reflection mip; the first, drawn rather than filtered, has none.</summary>
    private readonly DescriptorSet[] _filterReflections;
    private readonly DescriptorSet _filterIrradiance;
    private readonly RoughSlices _slices = new();

    /// <param name="uniforms">The Environment block and the physical sky's sky-view table, which environment.comp reads.</param>
    public EnvironmentPrefilter(VulkanContext context, EnvironmentUniforms uniforms, Sampler sampler,
        (EnvironmentCube Reflections, EnvironmentCube Irradiance, EnvironmentCube Sky) targets)
    {
        _targets = targets;
        _source = new EnvironmentCube(context, SourceSize, SourceMips);

        _sampler = sampler;
        var black = new TextureData
        {
            Width = 1, Height = 1, Kind = TextureKind.Linear, Format = TextureFormat.Rgba16f, Mips = [new byte[8]],
        };
        _noImage = GpuTexture.Cube(context, [black, black, black, black, black, black]);

        _draw = new CubeShader<DrawConstants>(context, "environment.comp", DescriptorType.UniformBuffer, 3);
        _drawSets = (
            _draw.CreateSet(uniforms.Buffer, _source.Storage(0), sampler, uniforms.SkyView, _noImage.View),
            _draw.CreateSet(uniforms.Buffer, _targets.Reflections.Storage(0), sampler, uniforms.SkyView, _noImage.View),
            _draw.CreateSet(uniforms.Buffer, _targets.Sky.Storage(0), sampler, uniforms.SkyView, _noImage.View));

        _filter = new CubeShader<FilterConstants>(context, "environment_filter.comp",
            DescriptorType.CombinedImageSampler, _targets.Reflections.MipCount);
        _filterReflections = new DescriptorSet[_targets.Reflections.MipCount];
        for (int mip = 1; mip < _filterReflections.Length; mip++)
        {
            _filterReflections[mip] = _filter.CreateSet(sampler, _source.Sampled, _targets.Reflections.Storage(mip));
        }

        _filterIrradiance = _filter.CreateSet(sampler, _targets.Sky.Sampled, _targets.Irradiance.Storage(0));
    }

    /// <summary>True while a rough reflection level still shows an earlier sky than the sharp ones.</summary>
    public bool Unfinished => _slices.Unfinished;

    /// <summary>The sharp environment with its panels but without the sun's disc, every mip.</summary>
    public ImageView Background => _source.Sampled;

    /// <summary>
    /// Points the draws at a photographed sky's cube, or at none. It rewrites their descriptor sets, so the GPU must
    /// have finished every draw recorded before.
    /// </summary>
    public void SetImage(GpuTexture? image)
    {
        var view = image?.View ?? _noImage.View;
        _draw.SetCube(_drawSets.Source, _sampler, view);
        _draw.SetCube(_drawSets.Reflections, _sampler, view);
        _draw.SetCube(_drawSets.Sky, _sampler, view);
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
        _draw.Record(cmd, _drawSets.Source, _source.Level(0), new DrawConstants { Panels = 1 });
        _source.BuildMips(cmd);

        _draw.Record(cmd, _drawSets.Reflections, _targets.Reflections.Level(0), new DrawConstants { Panels = 1, Disc = 1 });
        _targets.Reflections.Ready(cmd, 0);

        _draw.Record(cmd, _drawSets.Sky, _targets.Sky.Level(0), default);
        _targets.Sky.BuildMips(cmd);
        _filter.Record(cmd, _filterIrradiance, _targets.Irradiance.Level(0), new FilterConstants
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
            _filter.Record(cmd, _filterReflections[mip], _targets.Reflections.Level(mip), new FilterConstants
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
        _filter.Dispose();
        _draw.Dispose();
        _noImage.Dispose();
        _source.Dispose();
    }

    /// <summary>The solid angle of one texel of a cube's sharpest level, on average.</summary>
    private static float TexelAngle(uint size) => 4f * MathF.PI / (6f * size * size);
}
