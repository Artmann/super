using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Vulkan;

namespace Super.Tests;

/// <summary>trace.glsl reads the geometry table and the mesh vertices at fixed offsets, so the C# side must agree.</summary>
public class TraceGeometryLayoutTests
{
    [Fact]
    public void Matches_the_shaders_std430_layout()
    {
        Assert.Equal(24, Unsafe.SizeOf<GpuTraceGeometry>());
        Assert.Equal(16, (int)Marshal.OffsetOf<GpuTraceGeometry>(nameof(GpuTraceGeometry.FirstIndex)));
        Assert.Equal(20, (int)Marshal.OffsetOf<GpuTraceGeometry>(nameof(GpuTraceGeometry.Material)));
    }

    [Fact]
    public void Vertices_sit_where_trace_glsl_reads_them()
    {
        // trace.glsl steps through vertices 16 floats at a time, with the normal at float 3 and the uv at float 10.
        Assert.Equal(64, Unsafe.SizeOf<Vertex>());
        Assert.Equal(3 * sizeof(float), (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal)));
        Assert.Equal(10 * sizeof(float), (int)Marshal.OffsetOf<Vertex>(nameof(Vertex.Uv)));
    }
}
