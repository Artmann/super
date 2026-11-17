using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The Environment block Shaders/environment.comp draws the cubes from: the studio's bands and panels, kept on the CPU
/// until <see cref="Write"/> puts them, with a clear sky, in the GPU's buffer.
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

        public int PanelCount;
        private readonly int _padding0;
        private readonly int _padding1;
        private readonly int _padding2;
        public GpuPanelArray Panels;
    }

    private readonly VulkanContext _context;
    private GpuEnvironment _description;

    public EnvironmentUniforms(VulkanContext context)
    {
        _context = context;
        Buffer = new GpuBuffer(context, (ulong)sizeof(GpuEnvironment),
            BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
    }

    public GpuBuffer Buffer { get; }

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
    /// and makes compute shaders wait for it.
    /// </summary>
    public void Write(CommandBuffer cmd, in SkySun sky)
    {
        var api = _context.Api;
        _description.SkySun = new Vector4(sky.Direction, sky.Turbidity);
        _description.SunDisc = new Vector4(sky.DiscRadiance, sky.DiscCosine);

        api.MemoryBarrier(cmd, (PipelineStageFlags2.AllCommandsBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
        var description = _description;
        api.CmdUpdateBuffer(cmd, Buffer.Handle, 0, (ulong)sizeof(GpuEnvironment), &description);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.UniformReadBit));
    }

    public void Dispose() => Buffer.Dispose();
}
