using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ProbeGridTests
{
    [Fact]
    public void A_7_by_3_by_5_metre_box_at_1_metre_spacing_has_8_by_4_by_6_probes()
    {
        var settings = new ProbeVolumeSettings(new Vector3(-2f, 0f, 1f), new Vector3(5f, 3f, 6f), 1f);

        Assert.Equal((8, 4, 6), ProbeGrid.Counts(settings));
    }

    [Fact]
    public void A_flat_box_still_has_two_probes_across_each_axis()
    {
        // Rendition takes the settings without View's checks, so a box with no size can reach it.
        var settings = new ProbeVolumeSettings(Vector3.One, Vector3.One, 1f);

        Assert.Equal((2, 2, 2), ProbeGrid.Counts(settings));
    }

    [Fact]
    public void Probe_positions_run_x_fastest_from_min()
    {
        var min = new Vector3(-2f, -0.5f, -9f);
        var settings = new ProbeVolumeSettings(min, new Vector3(9f, 5f, 6f), 0.5f);
        var counts = ProbeGrid.Counts(settings);

        Assert.Equal(min, ProbeGrid.Position(settings, 0));
        Assert.Equal(min + new Vector3(0.5f, 0f, 0f), ProbeGrid.Position(settings, 1));
        Assert.Equal(min + new Vector3(0f, 0.5f, 0f), ProbeGrid.Position(settings, counts.x));
        Assert.Equal(min + new Vector3(0f, 0f, 0.5f), ProbeGrid.Position(settings, counts.x * counts.y));
    }

    [Fact]
    public void More_than_4096_probes_is_refused()
    {
        var view = new View();

        // 17 x 17 x 17 = 4913 probes.
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.LightProbes(Vector3.Zero, new Vector3(16f), 1f));

        Assert.Equal("spacing", error.ParamName);
        Assert.Contains("4096", error.Message);
        Assert.Contains("4913", error.Message);
        Assert.Null(view.LightProbeVolume);
    }

    [Fact]
    public void Exactly_4096_probes_is_allowed()
    {
        var view = new View();

        // 16 x 16 x 16 = 4096 probes.
        view.LightProbes(Vector3.Zero, new Vector3(15f), 1f);

        Assert.Equal(new ProbeVolumeSettings(Vector3.Zero, new Vector3(15f), 1f), view.LightProbeVolume);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void A_zero_spacing_is_refused(float spacing)
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.LightProbes(Vector3.Zero, Vector3.One, spacing));

        Assert.Equal("spacing", error.ParamName);
        Assert.Contains("more than 0", error.Message);
    }

    [Theory]
    [InlineData(0, "x")]
    [InlineData(1, "y")]
    [InlineData(2, "z")]
    public void A_box_whose_max_is_not_past_its_min_names_the_axis(int axis, string name)
    {
        var view = new View();
        var max = new Vector3(4f);
        max[axis] = 0f;

        var error = Assert.Throws<ArgumentException>(() => view.LightProbes(Vector3.Zero, max, 1f));

        Assert.Equal("max", error.ParamName);
        Assert.Contains($"along {name}", error.Message);
    }

    [Fact]
    public void The_volume_lasts_one_frame()
    {
        var view = new View();
        view.Begin(0f);
        view.LightProbes(Vector3.Zero, Vector3.One, 0.5f);
        var kept = view.LightProbeVolume;

        view.Begin(0f);

        Assert.Equal(new ProbeVolumeSettings(Vector3.Zero, Vector3.One, 0.5f), kept);
        Assert.Null(view.LightProbeVolume);
    }

    [Fact]
    public void A_probe_behind_the_surface_gets_no_weight()
    {
        float weight = ProbeGrid.Weight(
            point: Vector3.Zero, normal: Vector3.UnitY, probe: new Vector3(0f, -0.5f, 0f),
            meanDepth: 10f, meanSquares: 100f, distance: 0.5f);

        Assert.Equal(0f, weight);
    }

    [Fact]
    public void A_probe_in_front_of_the_surface_on_its_point_gets_nearly_all_the_weight()
    {
        float weight = ProbeGrid.Weight(
            point: Vector3.Zero, normal: Vector3.UnitY, probe: new Vector3(0f, 0.05f, 0f),
            meanDepth: 10f, meanSquares: 100f, distance: 0.05f);

        // Trilinear 0.95 at 1 metre spacing, facing 1, nothing in the way 1.
        Assert.Equal(0.95f, weight, 3);
    }

    [Fact]
    public void A_probe_half_a_cell_away_on_each_axis_gets_an_eighth()
    {
        float weight = ProbeGrid.Weight(
            point: Vector3.Zero, normal: Vector3.Normalize(Vector3.One), probe: new Vector3(0.5f),
            meanDepth: 10f, meanSquares: 100f, distance: 0.866f);

        Assert.Equal(0.125f, weight, 3);
    }

    [Fact]
    public void A_probe_seen_through_a_wall_is_weighted_down_by_chebyshev()
    {
        var probe = new Vector3(0f, 0.05f, 0f);
        float meanDepth = 0.5f;
        float meanSquares = meanDepth * meanDepth + 0.0004f;

        float throughWall = ProbeGrid.Weight(Vector3.Zero, Vector3.UnitY, probe, meanDepth, meanSquares, distance: 2f);
        float nearer = ProbeGrid.Weight(Vector3.Zero, Vector3.UnitY, probe, meanDepth, meanSquares, distance: 0.4f);

        Assert.True(throughWall < 0.1f, $"Through the wall the weight was {throughWall}.");
        Assert.True(nearer > 0.9f, $"In front of the wall the weight was {nearer}.");
    }

    // 2 by 2 by 2 probes, so 8 in all.
    private static readonly ProbeVolumeSettings SmallBox = new(Vector3.Zero, Vector3.One, 1f);

    [Fact]
    public void A_new_box_captures_in_batches_through_two_rounds_and_is_ready_after_the_first()
    {
        var schedule = new ProbeSchedule();
        schedule.Update(SmallBox, lighting: 1);

        Assert.Equal((0, 5), schedule.Batch(5));
        schedule.Captured(5);
        Assert.False(schedule.Ready);
        Assert.Equal((5, 3), schedule.Batch(5));
        schedule.Captured(3);

        Assert.True(schedule.Ready);
        Assert.True(schedule.CapturesBounce);
        Assert.Equal((0, 5), schedule.Batch(5));
        schedule.Captured(5);
        schedule.Captured(3);

        Assert.False(schedule.CapturesBounce);
        Assert.Equal((0, 0), schedule.Batch(5));
    }

    [Fact]
    public void Changed_lighting_starts_round_one_again_but_keeps_the_old_probes_in_use()
    {
        var schedule = new ProbeSchedule();
        schedule.Update(SmallBox, lighting: 1);
        schedule.Captured(8);
        schedule.Captured(8);

        schedule.Update(SmallBox, lighting: 2);

        Assert.Equal(1, schedule.Round);
        Assert.Equal((0, 8), schedule.Batch(64));
        Assert.True(schedule.Ready);
    }

    [Fact]
    public void A_moved_box_is_not_ready_until_round_one_is_captured_again()
    {
        var schedule = new ProbeSchedule();
        schedule.Update(SmallBox, lighting: 1);
        schedule.Captured(8);
        schedule.Captured(8);

        schedule.Update(SmallBox with { Min = -Vector3.One }, lighting: 1);

        // Now 3 by 3 by 3 probes.
        Assert.False(schedule.Ready);
        Assert.Equal(1, schedule.Round);
        Assert.Equal((0, 27), schedule.Batch(64));
    }

    [Fact]
    public void The_same_box_and_lighting_do_not_start_again()
    {
        var schedule = new ProbeSchedule();
        schedule.Update(SmallBox, lighting: 1);
        schedule.Captured(8);
        schedule.Captured(8);

        schedule.Update(SmallBox, lighting: 1);

        Assert.Equal((0, 0), schedule.Batch(64));
    }
}
