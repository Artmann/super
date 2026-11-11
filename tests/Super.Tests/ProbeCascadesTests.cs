using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ProbeCascadesTests
{
    private static readonly float[] Spacings = { 0.5f, 1f, 2f, 4f };

    [Fact]
    public void High_quality_has_four_cascades_of_32_by_16_by_32_at_half_one_two_and_four_metres()
    {
        var layouts = ProbeCascades.Layouts(ProbeQuality.High);

        Assert.Equal(Spacings, layouts.Select(l => l.Spacing).ToArray());
        Assert.All(layouts, l => Assert.Equal((32, 16, 32), (l.CountX, l.CountY, l.CountZ)));
    }

    [Fact]
    public void Medium_and_low_cover_less_ground_at_the_same_spacings()
    {
        var medium = ProbeCascades.Layouts(ProbeQuality.Medium);
        var low = ProbeCascades.Layouts(ProbeQuality.Low);

        Assert.Equal(Spacings, medium.Select(l => l.Spacing).ToArray());
        Assert.Equal(Spacings, low.Select(l => l.Spacing).ToArray());
        Assert.Equal((24, 12, 24), (medium[0].CountX, medium[0].CountY, medium[0].CountZ));
        Assert.Equal((16, 8, 16), (low[0].CountX, low[0].CountY, low[0].CountZ));

        // 16 * 0.5 = 8 m, 8 * 0.5 = 4 m, 16 * 0.5 = 8 m.
        Assert.Equal(8f, low[0].CountX * low[0].Spacing);
        Assert.Equal(4f, low[0].CountY * low[0].Spacing);
        Assert.Equal(8f, low[0].CountZ * low[0].Spacing);
    }

    [Fact]
    public void A_cascade_is_centred_on_the_camera_snapped_to_its_spacing()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);

        cascades.Move(new Vector3(10.3f, 2.2f, -4.9f));

        Assert.Equal(new Int3(-6, -6, -21), cascades.Origin(1));
    }

    [Fact]
    public void Moving_the_camera_less_than_one_spacing_moves_no_cascade()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);
        cascades.Move(new Vector3(0.1f, 0f, 0f));

        var result = cascades.Move(new Vector3(0.4f, 0f, 0f));

        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            Assert.Empty(result.NewSlots(c));
            Assert.False(result.Teleported(c));
        }
    }

    [Fact]
    public void Moving_one_spacing_along_x_leaves_one_slice_of_new_probes()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);
        cascades.Move(new Vector3(0.1f, 0f, 0f));

        var result = cascades.Move(new Vector3(1.1f, 0f, 0f));

        var slice = result.NewSlots(1);
        Assert.Equal(16 * 32, slice.Count);
        Assert.Single(slice.Select(s => cascades.Position(1, s).X).Distinct());
        Assert.Equal(2 * 16 * 32, result.NewSlots(0).Count);
        Assert.Empty(result.NewSlots(2));
        Assert.Empty(result.NewSlots(3));
    }

    [Fact]
    public void Moving_further_than_a_cascade_is_wide_makes_every_probe_in_it_new()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);
        cascades.Move(Vector3.Zero);

        var result = cascades.Move(new Vector3(20f, 0f, 0f));

        Assert.True(result.Teleported(0));
        Assert.Equal(16384, result.NewSlots(0).Distinct().Count());
        Assert.False(result.Teleported(3));
        Assert.Equal(5 * 16 * 32, result.NewSlots(3).Count);
    }

    [Fact]
    public void The_first_move_makes_every_probe_new()
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);

        var result = cascades.Move(new Vector3(3f, 1f, 2f));

        for (int c = 0; c < ProbeCascades.Count; c++)
        {
            Assert.True(result.Teleported(c));
            Assert.Equal(16 * 8 * 16, result.NewSlots(c).Distinct().Count());
        }
    }

    [Fact]
    public void A_slot_and_its_world_position_round_trip_after_a_scroll()
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var random = new Random(seed);
            var cascades = new ProbeCascades(ProbeQuality.High);
            var camera = Vector3.Zero;

            for (int move = 0; move < 30; move++)
            {
                camera += new Vector3(
                    random.NextSingle() * 12f - 6f,
                    random.NextSingle() * 4f - 2f,
                    random.NextSingle() * 12f - 6f);
                cascades.Move(camera);
            }

            var layouts = ProbeCascades.Layouts(ProbeQuality.High);
            for (int c = 0; c < ProbeCascades.Count; c++)
            {
                var origin = cascades.Origin(c);
                var layout = layouts[c];
                var taken = new HashSet<int>();

                for (int n = 0; n < 64; n++)
                {
                    var cell = new Int3(
                        origin.X + random.Next(layout.CountX),
                        origin.Y + random.Next(layout.CountY),
                        origin.Z + random.Next(layout.CountZ));

                    int slot = cascades.Slot(c, cell);
                    Assert.InRange(slot, 0, layout.Total - 1);
                    Assert.Equal(new Vector3(cell.X, cell.Y, cell.Z) * layout.Spacing, cascades.Position(c, slot));
                }

                for (int k = 0; k < layout.CountZ; k++)
                {
                    for (int j = 0; j < layout.CountY; j++)
                    {
                        for (int i = 0; i < layout.CountX; i++)
                        {
                            taken.Add(cascades.Slot(c, new Int3(origin.X + i, origin.Y + j, origin.Z + k)));
                        }
                    }
                }

                Assert.Equal(layout.Total, taken.Count);
            }
        }
    }

    [Fact]
    public void The_finest_cascade_that_holds_a_point_one_cell_inside_is_chosen()
    {
        var cascades = new ProbeCascades(ProbeQuality.High);
        cascades.Move(Vector3.Zero);

        Assert.Equal(1, cascades.Finest(new Vector3(7.6f, 0f, 0f)));
        Assert.Equal(0, cascades.Finest(new Vector3(3f, 0f, 0f)));
        Assert.Equal(-1, cascades.Finest(new Vector3(200f, 0f, 0f)));
    }
}
