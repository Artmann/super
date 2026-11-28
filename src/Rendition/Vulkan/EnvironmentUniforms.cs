using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Rendition.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// What Shaders/environment.comp draws the cubes from: the Environment block, with the studio's bands and panels kept
/// on the CPU until <see cref="Write"/> puts them, with a clear sky, in the GPU's buffer; and the physical sky's
/// tables, which the same write records.
/// </summary>
internal sealed unsafe class EnvironmentUniforms : IDisposable
{
    private const int MaxPanels = 8;

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

        /// <summary>x: 1 to draw the photographed sky; y, z: the cosine and sine of its rotation; w: its scale.</summary>
        public Vector4 Image;

        /// <summary>The clouds: x coverage; y density, 0 for no clouds; z the base's height; w the layer's depth.</summary>
        public Vector4 CloudShape;

        /// <summary>xyz: towards the light on the clouds.</summary>
        public Vector4 CloudLight;

        /// <summary>rgb: that light, in the sky's units; a: the cirrus.</summary>
        public Vector4 CloudRadiance;

        /// <summary>xy: where the camera stands in the weather, in metres; zw: the same for the cirrus.</summary>
        public Vector4 CloudWeather;

        /// <summary>rgb: the air's haze over the clouds, per metre.</summary>
        public Vector4 CloudAir;

        public int PanelCount;
        private readonly int _padding0;
        private readonly int _padding1;
        private readonly int _padding2;
        public GpuPanelArray Panels;
    }

    private readonly VulkanContext _context;
    private readonly SkyAtmosphere _atmosphere;
    private GpuEnvironment _description;

    public EnvironmentUniforms(VulkanContext context, VulkanGpu gpu)
    {
        _context = context;
        _atmosphere = new SkyAtmosphere(context, gpu);
        Buffer = new GpuBuffer(context, (ulong)sizeof(GpuEnvironment),
            BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
    }

    public GpuBuffer Buffer { get; }

    /// <summary>The physical sky's sky-view table, ready to sample in General after a <see cref="Write"/>.</summary>
    public ImageView SkyView => _atmosphere.SkyView;

    /// <summary>Keeps the bands and panels for the next <see cref="Write"/>. Nothing reaches the GPU yet.</summary>
    public void Set(in EnvironmentDescription description)
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
    /// Records the copy of the kept description and this clear sky into the buffer, after earlier frames have read it,
    /// and the physical sky's tables for it, and makes compute shaders wait for both.
    /// </summary>
    public void Write(CommandBuffer cmd, in SkySun sky)
    {
        var api = _context.Api;
        _description.SkySun = new Vector4(sky.Direction, sky.Turbidity);
        _description.SunDisc = new Vector4(sky.DiscRadiance, sky.DiscCosine);
        _description.Image = sky.Image is null
            ? Vector4.Zero
            : new Vector4(1f, MathF.Cos(sky.Rotation), MathF.Sin(sky.Rotation), sky.Scale);
        _description.Floor.W = sky.Turbidity > 0f ? Atmosphere.GroundLight(sky.Direction, sky.Turbidity) : 1f;
        _description.CloudShape = sky.Clouds.Shape;
        _description.CloudLight = sky.Clouds.Light;
        _description.CloudRadiance = sky.Clouds.Radiance;
        _description.CloudWeather = sky.Clouds.Weather;
        _description.CloudAir = sky.Clouds.Air;

        api.MemoryBarrier(cmd, (PipelineStageFlags2.AllCommandsBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
        var description = _description;
        api.CmdUpdateBuffer(cmd, Buffer.Handle, 0, (ulong)sizeof(GpuEnvironment), &description);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.UniformReadBit));
        _atmosphere.Record(cmd, sky);
    }

    public void Dispose()
    {
        _atmosphere.Dispose();
        Buffer.Dispose();
    }
}
