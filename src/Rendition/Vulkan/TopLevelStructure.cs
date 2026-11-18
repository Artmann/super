using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A built top-level structure: its handle, the address a shader traces it by, and the geometry table its instances
/// index, <see cref="GeometryCount"/> entries, one per part. Valid only in the command buffer it was recorded into;
/// see <see cref="RayTracing.TopLevel(CommandBuffer, IReadOnlyList{TraceInstance})"/>.
/// </summary>
internal readonly unsafe record struct TopLevelStructure(
    AccelerationStructureKHR Handle, ulong Address, GpuBuffer GeometryTable, int GeometryCount)
{
    /// <summary>The geometry table as the GPU will read it. For tests, before the work it was built for is done.</summary>
    public ReadOnlySpan<GpuTraceGeometry> ReadGeometries() =>
        new(GeometryTable.Mapped, GeometryCount);
}

/// <summary>
/// One entry of the geometry table, for one part: where its mesh's vertices and indices are, where its triangles start
/// among the indices, its material, and its draw's tint. A hit's entry is its instance's custom index plus its geometry
/// index. Must match TraceGeometry in Shaders/trace.glsl, laid out as std430: 32 bytes, with the addresses read as
/// uvec2 and the tint as three half floats, red and green in one word and blue in the low half of the next.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuTraceGeometry
{
    public ulong Vertices;
    public ulong Indices;
    public uint FirstIndex;
    public uint Material;
    public uint TintRG;
    public uint TintB;
}
