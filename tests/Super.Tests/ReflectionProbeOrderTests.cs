using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class ReflectionProbeOrderTests
{
    // A camera at the origin looking down -z.
    private static readonly Matrix4x4 LookingDownZ = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
    private static readonly Matrix4x4 Perspective = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 3f, 16f / 9f, 0.1f);

    [Fact]
    public void The_gpu_probe_matches_light_data_glsl()
    {
        Assert.Equal(48, Marshal.SizeOf<GpuReflectionProbe>());
        Assert.Equal(16, (int)Marshal.OffsetOf<GpuReflectionProbe>(nameof(GpuReflectionProbe.BoxMin)));
        Assert.Equal(32, (int)Marshal.OffsetOf<GpuReflectionProbe>(nameof(GpuReflectionProbe.BoxMax)));
    }

    [Fact]
    public void Probes_blend_by_priority_and_then_the_smaller_box_first()
    {
        ReflectionProbe?[] slots =
        [
            Box(new Vector3(0f, 0f, -10f), 8f),                    // The yard.
            Box(new Vector3(0f, 0f, -10f), 2f),                    // A room in it.
            Box(new Vector3(0f, 0f, -10f), 4f) with { Priority = 1f }, // A hall the game says to trust most.
        ];

        var (order, gpu) = Prepare(slots);

        Assert.Equal(3, order.Count);
        Assert.Equal([2f, 1f, 0f], gpu.Take(3).Select(probe => probe.BoxMin.W));
    }

    [Fact]
    public void Equal_probes_keep_the_order_of_their_cubes()
    {
        ReflectionProbe?[] slots =
        [
            Box(new Vector3(-2f, 0f, -10f), 2f),
            Box(new Vector3(0f, 0f, -10f), 2f),
            Box(new Vector3(2f, 0f, -10f), 2f),
        ];

        var (_, gpu) = Prepare(slots);

        Assert.Equal([0f, 1f, 2f], gpu.Take(3).Select(probe => probe.BoxMin.W));
    }

    [Fact]
    public void Free_cubes_and_probes_the_view_cant_see_are_left_out()
    {
        ReflectionProbe?[] slots =
        [
            null,
            Box(new Vector3(0f, 0f, 20f), 2f),   // Behind the camera.
            Box(new Vector3(0f, 0f, -10f), 2f),
        ];

        var (order, gpu) = Prepare(slots);

        Assert.Equal(1, order.Count);
        Assert.Equal(2f, gpu[0].BoxMin.W);
        Assert.Equal(new Vector4(-1f, -1f, -11f, 2f), gpu[0].BoxMin);
        Assert.Equal(new Vector4(1f, 1f, -9f, 0f), gpu[0].BoxMax);
        Assert.Equal(new Vector4(0f, 0f, -10f, 0.5f), gpu[0].Position);
    }

    [Fact]
    public void The_depths_reach_from_the_nearest_box_corner_to_the_furthest()
    {
        ReflectionProbe?[] slots =
        [
            Box(new Vector3(0f, 0f, -6f), 4f),   // z from -8 to -4.
            Box(new Vector3(1f, 0f, -11f), 2f),  // z from -12 to -10.
        ];

        var (order, _) = Prepare(slots);

        Assert.Equal(4f, order.NearDepth, 4);
        Assert.Equal(12f, order.FarDepth, 4);
    }

    [Fact]
    public void The_clusters_reach_over_the_probes_without_any_light()
    {
        var lights = new LocalLights();

        lights.Prepare(
            [], (LookingDownZ, Perspective, 720), new GpuLocalLight[LocalLight.MaxPerFrame], new GpuShadowTile[LocalLights.MaxTiles],
            cover: (4f, 12f));

        Assert.Equal(0, lights.LightCount);
        Assert.Equal(4f, lights.NearDepth, 4);
        Assert.Equal(12f, lights.FarDepth, 4);
    }

    [Fact]
    public void The_clusters_reach_over_both_the_lights_and_the_probes()
    {
        var lights = new LocalLights();
        LocalLight[] lamp = [new LocalLight { Key = 1, Position = new Vector3(0f, 0f, -7f), Radiance = Vector3.One, Range = 2f }];

        lights.Prepare(
            lamp, (LookingDownZ, Perspective, 720), new GpuLocalLight[LocalLight.MaxPerFrame], new GpuShadowTile[LocalLights.MaxTiles],
            cover: (6f, 20f));

        // The lamp reaches from 5 to 9 m, and the probes from 6 to 20 m.
        Assert.Equal(5f, lights.NearDepth, 4);
        Assert.Equal(20f, lights.FarDepth, 4);
    }

    private static ReflectionProbe Box(Vector3 center, float size) => new()
    {
        Position = center,
        BoxMin = center - new Vector3(size / 2f),
        BoxMax = center + new Vector3(size / 2f),
        BlendDistance = 0.5f,
    };

    private static (ReflectionProbeOrder Order, GpuReflectionProbe[] Gpu) Prepare(ReflectionProbe?[] slots)
    {
        var order = new ReflectionProbeOrder();
        var gpu = new GpuReflectionProbe[ReflectionProbeOrder.MaxProbes];
        order.Prepare(slots, LookingDownZ, Perspective, gpu);
        return (order, gpu);
    }
}
