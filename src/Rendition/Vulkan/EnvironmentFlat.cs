using System.Numerics;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// What Graphics.EnvironmentLight off gives every side: the irradiance cube's average, the sky's average soft light,
/// since each face's middle already gathers the half of the sky it faces; and the reflections' average at their
/// roughest level. <see cref="Record"/> copies the face middles into a buffer the CPU reads, so the shaders read one
/// colour instead of six lookups a pixel.
/// </summary>
internal sealed unsafe class EnvironmentFlat : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuBuffer _readback;
    private readonly CubeLevel _irradiance;
    private readonly CubeLevel _roughest;

    /// <summary>Reads the middles of <paramref name="irradiance"/> and <paramref name="roughest"/>, the roughest reflections.</summary>
    public EnvironmentFlat(VulkanContext context, CubeLevel irradiance, CubeLevel roughest)
    {
        _context = context;
        _irradiance = irradiance;
        _roughest = roughest;
        _readback = GpuBuffer.CreateHostVisible(context, 2 * CubeCentres.Bytes, BufferUsageFlags.TransferDstBit);
        new Span<byte>(_readback.Mapped, (int)_readback.Size).Clear();
    }

    /// <summary>The colours the last copy the GPU has finished holds.</summary>
    public (Vector3 Irradiance, Vector3 Reflection) Value
    {
        get
        {
            var halves = new ReadOnlySpan<Half>(_readback.Mapped, 2 * CubeCentres.Halves);
            return (CubeCentres.Mean(halves[..CubeCentres.Halves]), CubeCentres.Mean(halves[CubeCentres.Halves..]));
        }
    }

    /// <summary>Records the copy of both levels' face middles, after the work that drew them.</summary>
    public void Record(CommandBuffer cmd)
    {
        var api = _context.Api;

        // An earlier frame may still copy into the readback buffer.
        api.MemoryBarrier(cmd, (PipelineStageFlags2.AllCommandsBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
        CubeCentres.Record(api, cmd, _irradiance, _readback.Handle, 0);
        CubeCentres.Record(api, cmd, _roughest, _readback.Handle, CubeCentres.Bytes);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.HostBit, AccessFlags2.HostReadBit));
    }

    public void Dispose() => _readback.Dispose();
}
