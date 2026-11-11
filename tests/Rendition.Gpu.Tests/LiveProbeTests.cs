using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The light probes as they update a few a frame, rasterized: each test makes a renderer of its own without ray
/// queries, renders a scene from <see cref="ProbeScenes"/> until the probes round the camera have all been captured,
/// and reads them back through the real lookup in probes.glsl.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class LiveProbeTests
{
    private const int Size = 256;

    // Cascades 0 and 1 hold 32 by 16 by 32 probes each at High, and 32 are updated a frame, so they settle in about
    // 1,024 frames.
    private const int MaxFrames = 2000;

    [Fact]
    public void A_red_wall_beside_a_probe_tints_it_red_rasterized()
    {
        var red = Settled(gpu => ProbeScenes.RedWall(gpu, lightIsSpot: false), new Vector3(0.7f, 0.3f, 0f), Vector3.UnitX);
        var white = Settled(
            gpu => ProbeScenes.RedWall(gpu, lightIsSpot: false, wallIsWhite: true), new Vector3(0.7f, 0.3f, 0f), Vector3.UnitX);

        Assert.True(red.X > 0.01f, $"The red wall gave {red}.");
        Assert.True(red.X > 2f * red.Y && red.X > 2f * red.Z, $"The red wall gave {red}.");
        Assert.True(white.X > 0.01f && white.X < 1.2f * white.Y, $"The white wall gave {white}.");
    }

    [Fact]
    public void A_spot_light_lights_the_probe_only_inside_its_cone_rasterized()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: true);
        Settle(gpu, scene);

        // Facing down, each reads the floor under it. The spot's 30° cone reaches 0.9 m from the origin on the floor,
        // so (0, 0, -1.5), 45° off its axis, is outside it; a point light would light it a third as brightly.
        var samples = gpu.Renderer.SampleProbeIrradiance(
        [
            (new Vector3(0f, 0.3f, 0f), -Vector3.UnitY),
            (new Vector3(0f, 0.3f, -1.5f), -Vector3.UnitY),
        ]);

        Assert.True(samples[0].X > 0.01f, $"Inside the cone the probes read {samples[0]}.");
        Assert.True(samples[1].X >= 0f, $"Outside the cone the probes read {samples[1]}.");
        Assert.True(samples[0].X >= 10f * samples[1].X, $"Inside the cone {samples[0]}, outside it {samples[1]}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_closed_box_reads_black_under_a_bright_sky_rasterized()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.ClosedBox(gpu);
        Settle(gpu, scene);

        var samples = gpu.Renderer.SampleProbeIrradiance(
        [
            (new Vector3(0.2f, 0.2f, 0.2f), Vector3.UnitY),
            (new Vector3(3f, 0.5f, 0f), Vector3.UnitX),
        ]);

        // At least 0 on every channel: -1 would mean no probe could tell, and the lighting would use the bright sky.
        var inside = samples[0];
        Assert.True(inside.X >= 0f && inside.Y >= 0f && inside.Z >= 0f, $"Inside the box the probes read {inside}.");
        Assert.True(inside.X < 0.01f && inside.Y < 0.01f && inside.Z < 0.01f, $"Inside the box the probes read {inside}.");
        Assert.True(samples[1].X > 1f, $"Outside the box the probes read {samples[1]}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_probe_inside_a_solid_box_is_left_out_rasterized()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.SolidBox(gpu);
        Settle(gpu, scene);

        // Every probe inside the box sees only the backs of its faces, so none of them counts. What is left is either
        // nothing (-1) or the coarser cascades' probes outside the box: their weights through it are tiny, but the
        // lookup normalizes by the total, so they read as the sky. A probe inside taken as real would read black.
        float spacing = ProbeCascades.Layouts(ProbeQuality.High)[2].Spacing;
        Assert.True(spacing > 1.1f, $"Cascade 2's probes are {spacing} m apart, inside the box.");

        var inside = gpu.Renderer.SampleProbeIrradiance([(new Vector3(0.2f, 0.2f, 0.2f), Vector3.UnitY)])[0];

        Assert.True(inside == new Vector3(-1f) || inside.X > 1f, $"Inside the solid box the probes read {inside}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_white_room_lit_by_one_light_settles_and_does_not_grow_rasterized()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.WhiteRoom(gpu);
        Settle(gpu, scene);

        var centre = new Vector3(0.2f, 1.3f, 0.2f);
        var readings = new List<float>();
        for (int frame = 0; frame <= 500; frame++)
        {
            if (frame % 50 == 0)
            {
                readings.Add(gpu.Renderer.SampleProbeIrradiance([(centre, Vector3.UnitY)])[0].X);
            }

            Render(gpu, scene, Vector3.Zero);
        }

        Assert.True(readings[0] > 0.01f, $"The room's centre read {readings[0]}.");
        float last = readings[^1];
        float before = readings[^2];
        Assert.True(MathF.Abs(last - before) <= 0.02f * before, $"The readings were {string.Join(", ", readings)}.");
        Assert.All(readings, reading => Assert.True(reading <= 2f * readings[0], $"The readings were {string.Join(", ", readings)}."));
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void Scrolling_the_camera_keeps_the_probes_it_did_not_leave()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);
        Settle(gpu, scene);
        var point = new Vector3(0.7f, 0.3f, 0f);
        var before = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];

        Render(gpu, scene, new Vector3(0f, 0f, 1f));
        var after = gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitX)])[0];

        Assert.True(before.X > 0.01f, $"Before the move the probes read {before}.");
        Assert.True(Vector3.Distance(before, after) <= 0.01f * before.Length(), $"Before the move {before}, after it {after}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void No_frame_on_the_load_path_waits_for_probes()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);

        Render(gpu, scene, Vector3.Zero);

        Assert.InRange(gpu.Renderer.ProbeUpdatesLastFrame, 1, ProbeBudget.For(ProbeQuality.High).Raster);
        Assert.False(gpu.Renderer.ProbesSettled);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_frame_stops_taking_probes_at_its_triangle_budget()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = ProbeScenes.RedWall(gpu, lightIsSpot: false);

        // One triangle is less than any probe sees here, so only the first probe of each batch is drawn; the rest
        // stay uncaptured for a later frame rather than being marked as done.
        gpu.Renderer.ProbeTrianglesPerFrame = 1;
        Render(gpu, scene, Vector3.Zero);
        int capped = gpu.Renderer.ProbeUpdatesLastFrame;
        gpu.Renderer.ProbeTrianglesPerFrame = Rendition.Passes.ProbeCapturePass.MaxTrianglesPerFrame;
        Render(gpu, scene, Vector3.Zero);

        Assert.Equal(1, capped);
        Assert.Equal(ProbeBudget.For(ProbeQuality.High).Raster, gpu.Renderer.ProbeUpdatesLastFrame);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void Scrolling_forgets_the_probes_it_left()
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        gpu.Renderer.ClearProbesForTest();
        gpu.Renderer.ProbeUpdatesPaused = true;
        Render(gpu, new ProbeScene([], [], Vector3.Zero), Vector3.Zero);

        // Cascade 0 is 16 m across, so moving 4 m along x hands the slots of the probes round x = -6.3 to the probes
        // round x = 9.7. The move clears them, so they read as uncaptured rather than as the green written here.
        WriteAround(gpu, new Vector3(-6.3f, 0.3f, 0.2f), new Vector3(0f, 1f, 0f));

        // Surfaces read the cascades once a frame starts with one written, so a frame in place comes first.
        Render(gpu, new ProbeScene([], [], Vector3.Zero), Vector3.Zero);
        var written = gpu.Renderer.SampleProbeIrradiance([(new Vector3(-6.3f, 0.3f, 0.2f), Vector3.UnitY)])[0];
        Assert.True(Vector3.Distance(written, new Vector3(0f, 1f, 0f)) < 1e-3f, $"The written probes read {written}.");

        Render(gpu, new ProbeScene([], [], Vector3.Zero), new Vector3(4f, 0f, 0f));
        var moved = gpu.Renderer.SampleProbeIrradiance([(new Vector3(9.7f, 0.3f, 0.2f), Vector3.UnitY)])[0];

        Assert.True(moved.Y < 0.5f, $"The probes round x = 9.7 read {moved}.");
        Assert.Equal(0, gpu.ValidationErrors);
    }

    // Renders the scene on a renderer of its own until it settles, and samples it there.
    private static Vector3 Settled(Func<GpuFixture, ProbeScene> make, Vector3 point, Vector3 normal)
    {
        using var gpu = new GpuFixture(disableRayTracing: true);
        var scene = make(gpu);
        Settle(gpu, scene);
        var sample = gpu.Renderer.SampleProbeIrradiance([(point, normal)])[0];
        Assert.Equal(0, gpu.ValidationErrors);
        return sample;
    }

    private static void Settle(GpuFixture gpu, ProbeScene scene)
    {
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            Render(gpu, scene, Vector3.Zero);
            if (gpu.Renderer.ProbesSettled)
            {
                return;
            }
        }

        Assert.Fail($"The probes had not settled after {MaxFrames} frames.");
    }

    private static void Render(GpuFixture gpu, ProbeScene scene, Vector3 camera)
    {
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = camera,
            LocalLights = scene.Lights,
            BackgroundRadiance = scene.Sky,
        };

        gpu.Renderer.Render(view, scene.Draws);
    }

    // Writes the eight cascade-0 probes round a point, as the lookup finds them for a normal facing up.
    private static void WriteAround(GpuFixture gpu, Vector3 point, Vector3 irradiance)
    {
        var cascades = gpu.Renderer.LightProbeCascades;
        float spacing = ProbeCascades.Layouts(ProbeQuality.High)[0].Spacing;
        var biased = point + new Vector3(0f, 0.2f * spacing, 0f);
        var cell = new Int3(
            (int)MathF.Floor(biased.X / spacing), (int)MathF.Floor(biased.Y / spacing), (int)MathF.Floor(biased.Z / spacing));
        for (int corner = 0; corner < 8; corner++)
        {
            var at = new Int3(cell.X + (corner & 1), cell.Y + ((corner >> 1) & 1), cell.Z + ((corner >> 2) & 1));
            gpu.Renderer.WriteProbeTileForTest(new ProbeId(0, cascades.Slot(0, at)), irradiance);
        }
    }
}
