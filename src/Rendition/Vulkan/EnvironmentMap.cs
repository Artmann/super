using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Cube maps of the surrounding studio. Reflections holds one mip per roughness step,
/// Irradiance holds the soft light that reaches a matte surface, and Sky is the sharp
/// studio without its panels, or the clear sky without its sun disc, the same light the
/// irradiance is made from. <see cref="Record"/> redraws all three in any command buffer
/// without waiting, so the sky can change every frame.
/// </summary>
internal sealed unsafe class EnvironmentMap : IDisposable
{
    private const uint ReflectionSize = 512;
    private const uint IrradianceSize = 32;
    private const uint SkySize = 64;
    private const uint SkyMips = 7;
    private const int MaxPanels = 8;

    /// <summary>One roughness step per mip. REFLECTION_LODS in Shaders/lighting.frag must be one less.</summary>
    private const int ReflectionMips = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuPanel
    {
        public Vector4 Direction;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Radiance;
    }

    [InlineArray(MaxPanels)]
    private struct GpuPanelArray
    {
        private GpuPanel _first;
    }

    /// <summary>Must match the Environment block in Shaders/environment.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuEnvironment
    {
        public Vector4 Ceiling;
        public Vector4 Horizon;
        public Vector4 Floor;

        /// <summary>xyz: towards the clear sky's sun; w: its turbidity, 0 for the bands.</summary>
        public Vector4 SkySun;

        /// <summary>rgb: how bright the sun's disc is; a: the cosine of its angular radius.</summary>
        public Vector4 SunDisc;

        public int PanelCount;
        private readonly int _padding0;
        private readonly int _padding1;
        private readonly int _padding2;
        public GpuPanelArray Panels;
    }

    private readonly VulkanContext _context;
    private readonly EnvironmentCube _reflections;
    private readonly EnvironmentCube _irradiance;
    private readonly EnvironmentCube _sky;

    /// <summary>The sky's sharpest level alone, as every reader outside this class has always seen it.</summary>
    private readonly ImageView _skyTop;

    private readonly GpuBuffer _uniforms;
    private readonly GpuBuffer _readback;
    private readonly EnvironmentPrefilter _prefilter;
    private GpuEnvironment _description;

    public readonly Sampler Sampler;
    public ImageView Reflections => _reflections.Sampled;
    public ImageView Irradiance => _irradiance.Sampled;

    /// <summary>
    /// What the light probes see past the scene. Direct lights already give the panels'
    /// diffuse light, so a probe that saw them would count it twice. The sun's disc stays out for the same reason.
    /// </summary>
    public ImageView Sky => _skyTop;

    /// <summary>
    /// What Graphics.EnvironmentLight off gives every side: the irradiance cube's average, the sky's average soft light,
    /// since each face's middle already gathers the half of the sky it faces; and the reflections' average at their
    /// roughest level. Each <see cref="Record"/> copies the face middles into a buffer the CPU reads, so the shaders
    /// read one colour instead of six lookups a pixel. After <see cref="Bake"/> it is exact; after a recorded update
    /// it is the last one the GPU has finished.
    /// </summary>
    public (Vector3 Irradiance, Vector3 Reflection) Flat
    {
        get
        {
            var halves = new ReadOnlySpan<Half>(_readback.Mapped, 2 * CubeCentres.Halves);
            return (CubeCentres.Mean(halves[..CubeCentres.Halves]), CubeCentres.Mean(halves[CubeCentres.Halves..]));
        }
    }

    public EnvironmentMap(VulkanContext context)
    {
        _context = context;
        _reflections = new EnvironmentCube(context, ReflectionSize, ReflectionMips);
        _irradiance = new EnvironmentCube(context, IrradianceSize, 1);
        _sky = new EnvironmentCube(context, SkySize, SkyMips);
        _skyTop = _sky.CreateView(1);
        _uniforms = new GpuBuffer(context, (ulong)sizeof(GpuEnvironment),
            BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        _readback = GpuBuffer.CreateHostVisible(context, 2 * CubeCentres.Bytes, BufferUsageFlags.TransferDstBit);
        new Span<byte>(_readback.Mapped, (int)_readback.Size).Clear();

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
        _prefilter = new EnvironmentPrefilter(context, _uniforms, Sampler, (_reflections, _irradiance, _sky));
    }

    /// <summary>The cube level a test reads back.</summary>
    internal CubeLevel ReflectionLevel(int mip) => _reflections.Level(mip);

    /// <summary>Renders the studio, or a clear sky over its floor, into the cube maps, and waits for it.</summary>
    public void Bake(in EnvironmentDescription description, in SkySun sky)
    {
        SetDescription(description);
        Rebake(sky);
    }

    /// <summary>Renders the cube maps again from the last description with another clear sky, and waits for it.</summary>
    public void Rebake(in SkySun sky)
    {
        var copy = sky;
        _context.SubmitAndWait(cmd => Record(cmd, copy));
    }

    /// <summary>Keeps the bands and panels for the next <see cref="Record"/>. Nothing is drawn yet.</summary>
    public void SetDescription(in EnvironmentDescription description)
    {
        _description.Ceiling = new Vector4(description.Ceiling, 0f);
        _description.Horizon = new Vector4(description.Horizon, 0f);
        _description.Floor = new Vector4(description.Floor, 0f);

        int count = Math.Min(description.Panels.Length, MaxPanels);
        _description.PanelCount = count;
        for (int i = 0; i < count; i++)
        {
            ref readonly var panel = ref description.Panels[i];
            var direction = Vector3.Normalize(panel.Direction);
            var worldUp = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var right = Vector3.Normalize(Vector3.Cross(worldUp, direction));
            _description.Panels[i] = new GpuPanel
            {
                Direction = new Vector4(direction, 0f),
                Right = new Vector4(right, panel.HalfWidth),
                Up = new Vector4(Vector3.Cross(direction, right), panel.HalfHeight),
                Radiance = new Vector4(panel.Radiance, 0f),
            };
        }
    }

    /// <summary>
    /// Records a redraw of all three cubes from the last description and this clear sky. It waits on the GPU for
    /// earlier work that reads the cubes and makes later work wait for it, so it can go anywhere in a frame before the
    /// cubes are read. Only the reflections' sharpest level shows the sun's disc: the direct light already gives a
    /// surface the sun's diffuse light and its highlight, so the soft light and the rougher levels would count it
    /// twice. Their sky still brightens round the sun.
    /// </summary>
    public void Record(CommandBuffer cmd, in SkySun sky)
    {
        var api = _context.Api;
        _description.SkySun = new Vector4(sky.Direction, sky.Turbidity);
        _description.SunDisc = new Vector4(sky.DiscRadiance, sky.DiscCosine);

        // Earlier frames may still read the uniforms and cubes and copy into the readback buffer.
        api.MemoryBarrier(cmd, (PipelineStageFlags2.AllCommandsBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
        var description = _description;
        api.CmdUpdateBuffer(cmd, _uniforms.Handle, 0, (ulong)sizeof(GpuEnvironment), &description);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.UniformReadBit));

        _prefilter.Record(cmd);

        CubeCentres.Record(api, cmd, _irradiance.Level(0), _readback.Handle, 0);
        CubeCentres.Record(api, cmd, _reflections.Level(ReflectionMips - 1), _readback.Handle, CubeCentres.Bytes);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.HostBit, AccessFlags2.HostReadBit));
    }

    public void Dispose()
    {
        _prefilter.Dispose();
        _context.Api.DestroySampler(_context.Device, Sampler, null);
        _readback.Dispose();
        _uniforms.Dispose();
        _context.Api.DestroyImageView(_context.Device, _skyTop, null);
        _sky.Dispose();
        _irradiance.Dispose();
        _reflections.Dispose();
    }
}
