using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The light probes' storage and lookup: tiles written straight into the cascade layers and the fixed grid's layer,
/// then read back through the real probeIrradiance in probes.glsl. Updates are paused, so no capture writes over them.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ProbeStorageTests(GpuFixture gpu)
{
    private const int Size = 256;

    // A point just off a floor, near the camera at the origin, so it sits deep inside every cascade.
    private static readonly Vector3 Point = new(0.2f, 0.2f, 0.2f);

    [Fact]
    public void A_written_tile_reads_back_through_the_lookup()
    {
        int errors = gpu.ValidationErrors;
        gpu.Renderer.ClearProbesForTest();
        gpu.Renderer.ProbeUpdatesPaused = true;
        try
        {
            Render();
            var colour = new Vector3(0.2f, 0.4f, 0.6f);
            WriteAround(cascade: 0, Point, colour);
            Render();

            var sample = Sample(Point);

            AssertNear(colour, sample);
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ProbeUpdatesPaused = false;
            gpu.Renderer.ClearProbesForTest();
        }
    }

    [Fact]
    public void A_point_whose_fine_probes_are_uncaptured_falls_back_to_the_coarser_cascade()
    {
        int errors = gpu.ValidationErrors;
        gpu.Renderer.ClearProbesForTest();
        gpu.Renderer.ProbeUpdatesPaused = true;
        try
        {
            Render();
            Assert.Equal(0, gpu.Renderer.LightProbeCascades.Finest(Point));
            WriteAround(cascade: 1, Point, new Vector3(1f, 0f, 0f));
            Render();

            var sample = Sample(Point);

            AssertNear(new Vector3(1f, 0f, 0f), sample);
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ProbeUpdatesPaused = false;
            gpu.Renderer.ClearProbesForTest();
        }
    }

    [Fact]
    public void A_point_with_no_captured_probes_reads_the_sky()
    {
        int errors = gpu.ValidationErrors;
        gpu.Renderer.ClearProbesForTest();
        gpu.Renderer.ProbeUpdatesPaused = true;
        try
        {
            // One probe 6 m away is captured, so the frame does look in the cascades, but none round the point is.
            Render();
            var far = Cell(0, new Vector3(6f, 0f, 0f));
            gpu.Renderer.WriteProbeTileForTest(new ProbeId(0, gpu.Renderer.LightProbeCascades.Slot(0, far)), new Vector3(1f));
            Render(sky: new Vector3(0f, 0f, 0.5f));

            var sample = Sample(Point);

            // -1 is what softLight in lighting.frag turns into the sky's irradiance.
            Assert.Equal(new Vector3(-1f), sample);
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ProbeUpdatesPaused = false;
            gpu.Renderer.ClearProbesForTest();
        }
    }

    [Fact]
    public void The_fixed_grid_wins_inside_its_box()
    {
        int errors = gpu.ValidationErrors;
        gpu.Renderer.ClearProbesForTest();
        gpu.Renderer.ProbeUpdatesPaused = true;

        // 5 by 5 by 5 probes a metre apart, from -2 to 2.
        var box = new ProbeVolumeSettings(new Vector3(-2f), new Vector3(2f), 1f);
        var outside = new Vector3(4.2f, 0.2f, 0.2f);
        try
        {
            // One frame gives the cascades their place and the frame the fixed grid. With updates paused,
            // nothing captures over the tiles written below.
            Render(box);

            // The eight grid probes round the inside point: the biased point (0.2, 0.4, 0.2) is in local cell (2, 2, 2).
            var green = new Vector3(0f, 1f, 0f);
            for (int corner = 0; corner < 8; corner++)
            {
                int x = 2 + (corner & 1);
                int y = 2 + ((corner >> 1) & 1);
                int z = 2 + ((corner >> 2) & 1);
                gpu.Renderer.WriteProbeTileForTest(new ProbeId(ProbeScheduler.FixedGrid, x + 5 * (y + 5 * z)), green);
            }

            var red = new Vector3(1f, 0f, 0f);
            WriteAround(cascade: 0, Point, red);
            WriteAround(cascade: 0, outside, red);
            Render(box);

            var samples = gpu.Renderer.SampleProbeIrradiance([(Point, Vector3.UnitY), (outside, Vector3.UnitY)]);

            AssertNear(green, samples[0]);
            AssertNear(red, samples[1]);
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ProbeUpdatesPaused = false;
            gpu.Renderer.ClearProbesForTest();
        }
    }

    private void Render(ProbeVolumeSettings? lightProbes = null, Vector3? sky = null)
    {
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = Vector3.Zero,
            LightProbes = lightProbes,
        };
        if (sky is { } radiance)
        {
            view.BackgroundRadiance = radiance;
        }

        gpu.Renderer.Render(view, []);
    }

    private Vector3 Sample(Vector3 point) => gpu.Renderer.SampleProbeIrradiance([(point, Vector3.UnitY)])[0];

    // The lookup first moves a point 0.2 spacings along its normal, here up, so its eight probes are round that.
    private void WriteAround(int cascade, Vector3 point, Vector3 irradiance)
    {
        var cascades = gpu.Renderer.LightProbeCascades;
        float spacing = ProbeCascades.Layouts(ProbeQuality.High)[cascade].Spacing;
        var cell = Cell(cascade, point + new Vector3(0f, 0.2f * spacing, 0f));
        for (int corner = 0; corner < 8; corner++)
        {
            var at = new Int3(cell.X + (corner & 1), cell.Y + ((corner >> 1) & 1), cell.Z + ((corner >> 2) & 1));
            gpu.Renderer.WriteProbeTileForTest(new ProbeId(cascade, cascades.Slot(cascade, at)), irradiance);
        }
    }

    private static Int3 Cell(int cascade, Vector3 point)
    {
        float spacing = ProbeCascades.Layouts(ProbeQuality.High)[cascade].Spacing;
        return new Int3(
            (int)MathF.Floor(point.X / spacing), (int)MathF.Floor(point.Y / spacing), (int)MathF.Floor(point.Z / spacing));
    }

    // Within 2% of the brightest channel, which also covers the half floats the atlases store.
    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        float tolerance = 0.02f * MathF.Max(expected.X, MathF.Max(expected.Y, expected.Z));
        Assert.True(
            MathF.Abs(expected.X - actual.X) <= tolerance &&
            MathF.Abs(expected.Y - actual.Y) <= tolerance &&
            MathF.Abs(expected.Z - actual.Z) <= tolerance,
            $"Expected {expected} within 2%, but read {actual}.");
    }
}
