using System.Numerics;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Cube maps of the surrounding studio. Reflections holds one mip per roughness step,
/// Irradiance holds the soft light that reaches a matte surface, and Sky is the sharp
/// studio without its panels, or the clear sky without its sun disc, the same light the
/// irradiance is made from. <see cref="Record"/> redraws all three in any command buffer
/// without waiting, and <see cref="RecordFrame"/> spreads the rough reflections over a few frames, so the sky can
/// change every frame within its budget.
/// </summary>
internal sealed unsafe class EnvironmentMap : IDisposable
{
    private const uint ReflectionSize = 512;
    private const uint IrradianceSize = 32;
    private const uint SkySize = 64;
    private const uint SkyMips = 7;

    /// <summary>One roughness step per mip. REFLECTION_LODS in Shaders/lighting.frag must be one less.</summary>
    private const int ReflectionMips = 6;

    private readonly VulkanContext _context;
    private readonly EnvironmentCube _reflections;
    private readonly EnvironmentCube _irradiance;
    private readonly EnvironmentCube _sky;
    private readonly EnvironmentUniforms _uniforms;
    private readonly EnvironmentFlat _flat;
    private readonly EnvironmentPrefilter _prefilter;

    public readonly Sampler Sampler;
    public ImageView Reflections => _reflections.Sampled;
    public ImageView Irradiance => _irradiance.Sampled;

    /// <summary>
    /// What the light probes see past the scene: the sky's sharpest level alone, as every reader outside this class
    /// has always seen it. Direct lights already give the panels' diffuse light, so a probe that saw them would count
    /// it twice. The sun's disc stays out for the same reason.
    /// </summary>
    public ImageView Sky { get; }

    /// <summary>
    /// What Graphics.EnvironmentLight off gives every side; see <see cref="EnvironmentFlat"/>. After <see cref="Bake"/>
    /// it is exact; after a recorded update it is the last one the GPU has finished, and its reflection half can trail
    /// the last slice of the rough reflections.
    /// </summary>
    public (Vector3 Irradiance, Vector3 Reflection) Flat => _flat.Value;

    /// <summary>True while the rough reflection levels still show an earlier sky than the sharp ones.</summary>
    public bool Unfinished => _prefilter.Unfinished;

    public EnvironmentMap(VulkanContext context)
    {
        _context = context;
        _reflections = new EnvironmentCube(context, ReflectionSize, ReflectionMips);
        _irradiance = new EnvironmentCube(context, IrradianceSize, 1);
        _sky = new EnvironmentCube(context, SkySize, SkyMips);
        Sky = _sky.CreateView(1);
        _uniforms = new EnvironmentUniforms(context);
        _flat = new EnvironmentFlat(context, _irradiance.Level(0), _reflections.Level(ReflectionMips - 1));

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = Vk.LodClampNone,
        };
        context.Api.CreateSampler(context.Device, &samplerInfo, null, out Sampler).Check();
        _prefilter = new EnvironmentPrefilter(context, _uniforms.Buffer, Sampler, (_reflections, _irradiance, _sky));
    }

    /// <summary>The cube level a test reads back.</summary>
    internal CubeLevel ReflectionLevel(int mip) => _reflections.Level(mip);

    /// <summary>Renders the studio, or a clear sky over its floor, into the cube maps, and waits for it.</summary>
    public void Bake(in EnvironmentDescription description, in SkySun sky)
    {
        SetDescription(description);
        var copy = sky;
        _context.SubmitAndWait(cmd => Record(cmd, copy));
    }

    /// <summary>Keeps the bands and panels for the next record. Nothing is drawn yet.</summary>
    public void SetDescription(in EnvironmentDescription description) => _uniforms.Set(description);

    /// <summary>
    /// Records a redraw of all three cubes, every level, from the last description and this clear sky. It waits on the
    /// GPU for earlier work that reads the cubes and makes later work wait for it, so it can go anywhere in a frame
    /// before the cubes are read. Only the reflections' sharpest level shows the sun's disc: the direct light already
    /// gives a surface the sun's diffuse light and its highlight, so the soft light and the rougher levels would count
    /// it twice. Their sky still brightens round the sun.
    /// </summary>
    public void Record(CommandBuffer cmd, in SkySun sky)
    {
        _uniforms.Write(cmd, sky);
        _prefilter.Record(cmd);
        _flat.Record(cmd);
    }

    /// <summary>
    /// Records a frame's share of a redraw. When <paramref name="changed"/>, the sharp levels, the sky and the soft
    /// light from this clear sky all at once, and the first slice of the rough reflections; after that, a slice a frame
    /// until they are all done. A sky that changes every frame keeps the slices turning, so each rough level is at most
    /// a few frames old; redrawing them all every frame would cost about twice the GPU time.
    /// </summary>
    public void RecordFrame(CommandBuffer cmd, in SkySun sky, bool changed)
    {
        if (!changed && !_prefilter.Unfinished)
        {
            return;
        }

        if (changed)
        {
            _uniforms.Write(cmd, sky);
        }

        _prefilter.RecordFrame(cmd, changed);
        _flat.Record(cmd);
    }

    public void Dispose()
    {
        _prefilter.Dispose();
        _context.Api.DestroySampler(_context.Device, Sampler, null);
        _flat.Dispose();
        _uniforms.Dispose();
        _context.Api.DestroyImageView(_context.Device, Sky, null);
        _sky.Dispose();
        _irradiance.Dispose();
        _reflections.Dispose();
    }
}
