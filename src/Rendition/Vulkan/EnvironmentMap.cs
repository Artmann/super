using System.Numerics;
using Rendition.Gpu.Vulkan;
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

    /// <summary>The three cubes the prefilter draws: the reflections, the soft light and the sky.</summary>
    private readonly (EnvironmentCube Reflections, EnvironmentCube Irradiance, EnvironmentCube Sky) _cubes;
    private readonly EnvironmentUniforms _uniforms;
    private readonly EnvironmentFlat _flat;
    private readonly EnvironmentPrefilter _prefilter;

    public readonly Sampler Sampler;
    public ImageView Reflections => _cubes.Reflections.Sampled;
    public ImageView Irradiance => _cubes.Irradiance.Sampled;

    /// <summary>
    /// What the light probes see past the scene: the sky's sharpest level alone, as every reader outside this class
    /// has always seen it. Direct lights already give the panels' diffuse light, so a probe that saw them would count
    /// it twice. The sun's disc stays out for the same reason.
    /// </summary>
    public ImageView Sky { get; }

    /// <summary>
    /// What shows behind the scene and what fog and haze melt into: the sharp environment with its panels but without
    /// the sun's disc, which would show through as a hot spot. Its sharpest mip is what shaders read.
    /// </summary>
    public ImageView Background => _prefilter.Background;

    /// <summary>The physical sky's sky-view table, in the General layout, which the clouds take their sky light from.</summary>
    public ImageView SkyView => _uniforms.SkyView;

    /// <summary>
    /// What Graphics.Lighting.EnvironmentLight off gives every side; see <see cref="EnvironmentFlat"/>. After a
    /// <see cref="Record"/> that was waited for it is exact; after a recorded update it is the last one the GPU has finished, and its reflection half can trail
    /// the last slice of the rough reflections.
    /// </summary>
    public (Vector3 Irradiance, Vector3 Reflection) Flat => _flat.Value;

    /// <summary>True while the rough reflection levels still show an earlier sky than the sharp ones.</summary>
    public bool Unfinished => _prefilter.Unfinished;

    /// <param name="weather">The clouds' weather map and the repeating sampler it is read with, for the clouds the captures draw.</param>
    public EnvironmentMap(VulkanContext context, VulkanGpu gpu, (Sampler Repeat, ImageView View) weather)
    {
        _context = context;
        _cubes = (
            new EnvironmentCube(context, ReflectionSize, ReflectionMips),
            new EnvironmentCube(context, IrradianceSize, 1),
            new EnvironmentCube(context, SkySize, SkyMips));
        Sky = _cubes.Sky.CreateView(1);
        _flat = new EnvironmentFlat(context, _cubes.Irradiance.Level(0), _cubes.Reflections.Level(ReflectionMips - 1));

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
        _uniforms = new EnvironmentUniforms(context, gpu);
        _prefilter = new EnvironmentPrefilter(context, _uniforms, Sampler, _cubes, weather);
    }

    /// <summary>The cube level a test reads back.</summary>
    internal CubeLevel ReflectionLevel(int mip) => _cubes.Reflections.Level(mip);

    /// <summary>
    /// Draws the sky from a photographed sky's cube from the next record on, or from none. The GPU must have finished
    /// every redraw recorded before.
    /// </summary>
    public void SetImage(GpuTexture? image) => _prefilter.SetImage(image);

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
        _uniforms.Dispose();
        _context.Api.DestroySampler(_context.Device, Sampler, null);
        _flat.Dispose();
        _context.Api.DestroyImageView(_context.Device, Sky, null);
        _cubes.Sky.Dispose();
        _cubes.Irradiance.Dispose();
        _cubes.Reflections.Dispose();
    }
}
