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
    private readonly EnvironmentCube _reflections;
    private readonly EnvironmentCube _irradiance;
    private readonly EnvironmentCube _sky;
    private readonly CubeShader<DrawConstants> _draw;
    private readonly CubeShader<FilterConstants> _filter;
    private readonly DescriptorSet _drawSource;
    private readonly DescriptorSet _drawReflections;
    private readonly DescriptorSet _drawSky;

    /// <summary>One set per reflection mip; the first, drawn rather than filtered, has none.</summary>
    private readonly DescriptorSet[] _filterReflections;
    private readonly DescriptorSet _filterIrradiance;

    /// <param name="uniforms">The Environment block environment.comp reads.</param>
    public EnvironmentPrefilter(VulkanContext context, GpuBuffer uniforms, Sampler sampler,
        (EnvironmentCube Reflections, EnvironmentCube Irradiance, EnvironmentCube Sky) targets)
    {
        (_reflections, _irradiance, _sky) = targets;
        _source = new EnvironmentCube(context, SourceSize, SourceMips);

        _draw = new CubeShader<DrawConstants>(context, "environment.comp", DescriptorType.UniformBuffer, 3);
        _drawSource = _draw.CreateSet(uniforms, _source.Storage(0));
        _drawReflections = _draw.CreateSet(uniforms, _reflections.Storage(0));
        _drawSky = _draw.CreateSet(uniforms, _sky.Storage(0));

        _filter = new CubeShader<FilterConstants>(context, "environment_filter.comp",
            DescriptorType.CombinedImageSampler, _reflections.MipCount);
        _filterReflections = new DescriptorSet[_reflections.MipCount];
        for (int mip = 1; mip < _filterReflections.Length; mip++)
        {
            _filterReflections[mip] = _filter.CreateSet(sampler, _source.Sampled, _reflections.Storage(mip));
        }

        _filterIrradiance = _filter.CreateSet(sampler, _sky.Sampled, _irradiance.Storage(0));
    }

    /// <summary>
    /// Records the draws and filters, leaving every level of the three cubes ready for any shader to sample. The
    /// uniforms must already be written and visible to compute shaders.
    /// </summary>
    public void Record(CommandBuffer cmd)
    {
        _draw.Record(cmd, _drawSource, _source.Level(0), new DrawConstants { Panels = 1 });
        _source.BuildMips(cmd);

        _draw.Record(cmd, _drawReflections, _reflections.Level(0), new DrawConstants { Panels = 1, Disc = 1 });
        _reflections.Ready(cmd, 0);
        int roughest = (int)_reflections.MipCount - 1;
        for (int mip = 1; mip <= roughest; mip++)
        {
            _filter.Record(cmd, _filterReflections[mip], _reflections.Level(mip), new FilterConstants
            {
                Roughness = mip / (float)roughest,
                TexelAngle = TexelAngle(SourceSize),
                LodBias = LodBias,
                Samples = ReflectionSamples << (mip - 1),
            });
            _reflections.Ready(cmd, mip);
        }

        _draw.Record(cmd, _drawSky, _sky.Level(0), default);
        _sky.BuildMips(cmd);
        _filter.Record(cmd, _filterIrradiance, _irradiance.Level(0), new FilterConstants
        {
            Irradiance = 1,
            TexelAngle = TexelAngle(_sky.Size),
            LodBias = LodBias,
            Samples = IrradianceSamples,
        });
        _irradiance.Ready(cmd, 0);
    }

    public void Dispose()
    {
        _filter.Dispose();
        _draw.Dispose();
        _source.Dispose();
    }

    /// <summary>The solid angle of one texel of a cube's sharpest level, on average.</summary>
    private static float TexelAngle(uint size) => 4f * MathF.PI / (6f * size * size);
}
