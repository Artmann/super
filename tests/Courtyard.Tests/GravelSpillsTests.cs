using System.Numerics;
using Super.Materials;

namespace Courtyard.Tests;

/// <summary>
/// Where the Courtyard lays its gravel decals: in the mud off the path's edges and round the barrel, never out on the
/// paving, in or by the puddle or against the wall; and where in its image the gravel lies thick.
/// </summary>
public sealed class GravelSpillsTests
{
    public static TheoryData<int> Seeds()
    {
        var seeds = new TheoryData<int> { CourtyardGame.GravelSeed };
        for (var seed = 0; seed < 14; seed++)
        {
            seeds.Add(seed);
        }

        return seeds;
    }

    // How far a point is from the nearest piece of the path's middle line, worked out here apart from the game's own.
    private static float FromMiddleLine(Vector2 point)
    {
        var best = float.MaxValue;
        foreach (var arm in GravelSpills.PathArms)
        {
            for (var i = 0; i + 1 < arm.Length; i++)
            {
                for (var step = 0; step <= 200; step++)
                {
                    best = MathF.Min(best, Vector2.Distance(point, Vector2.Lerp(arm[i], arm[i + 1], step / 200f)));
                }
            }
        }

        return best;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void No_spill_lies_with_its_middle_on_the_paving(int seed)
    {
        var onPaving = GravelSpills.Boxes(seed)
            .Select(GravelSpills.Centre)
            .Where(centre => FromMiddleLine(centre) < GravelSpills.PathHalfWidth)
            .ToList();

        Assert.Empty(onPaving);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void No_spill_lies_with_its_middle_in_the_puddle_or_its_muddy_rim(int seed)
    {
        // In art/courtyard.py the water's edge wobbles out to about 1.15 in squared radii, and its wet, trodden bank
        // lies past that. 1.5, 22 % past the radii, keeps gravel off the water and the steepest of the bank.
        var inPuddle = GravelSpills.Boxes(seed)
            .Select(GravelSpills.Centre)
            .Where(centre => ((centre - GravelSpills.PuddleCentre) / GravelSpills.PuddleRadii).LengthSquared() <= 1.5f)
            .ToList();

        Assert.Empty(inPuddle);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void No_spill_lies_with_its_middle_within_30_cm_of_the_wall(int seed)
    {
        var byWall = GravelSpills.Boxes(seed)
            .Select(GravelSpills.Centre)
            .Where(centre => centre.Y > GravelSpills.WallY - 0.3f)
            .ToList();

        Assert.Empty(byWall);
    }

    [Fact]
    public void Each_part_of_the_spills_is_laid_the_same_whatever_order_the_parts_are_laid_in()
    {
        // Each side of each arm and the ring draw their own random numbers, so laying them last to first gives the
        // same boxes as laying them first to last.
        var backwards = new List<Matrix4x4>();
        for (var part = GravelSpills.Parts - 1; part >= 0; part--)
        {
            backwards.AddRange(GravelSpills.Spills(CourtyardGame.GravelSeed, part));
        }

        var forwards = GravelSpills.Boxes(CourtyardGame.GravelSeed);

        Assert.True(forwards.Count > GravelSpills.Parts, $"Only {forwards.Count} spills were laid.");
        Assert.Equal(Sorted(forwards), Sorted(backwards));
    }

    [Fact]
    public void The_gravel_image_lies_thick_from_a_sharp_edge_near_V_0_and_thins_out_towards_V_1()
    {
        // The spill is an oval 0.39 of the image high round V 0.45, so it ends at V 0.06 and 0.84, give or take the
        // 1 % it is bent by. It is fuller towards V 0: at V 0.11, 0.87 of its half height up, the oval is
        // 1 - 0.87² = 0.24 and the fullness 1.15 - 0.5 × 0.11 = 1.1, so 0.26, which levels to 0.33 and covers 80 % of
        // the grit. At V 0.8, as far from the middle the other way, the fullness is only 0.75: 0.2 × 0.75 = 0.15,
        // levelled to 0.18, covers 37 %.
        const int size = 512;
        var opacity = GraphRunner.Run(new Gravel(), size).Opacity;
        var rows = new float[size];
        for (var y = 0; y < size; y++)
        {
            // The middle of the spill across U, clear of the oval's ends.
            float sum = 0f;
            for (var x = size * 3 / 10; x < size * 7 / 10; x++)
            {
                sum += opacity[y * size + x];
            }

            rows[y] = sum / (size * 4 / 10);
        }

        float At(float v) => rows[(int)(v * size)];

        for (var y = 0; y < size; y++)
        {
            var v = (y + 0.5f) / size;
            if (v < 0.05f || v > 0.88f)
            {
                Assert.True(rows[y] < 0.005f, $"At V {v:0.000} the gravel is {rows[y]:0.000} opaque, outside the spill.");
            }
            else if (v >= 0.12f && v <= 0.72f)
            {
                Assert.True(rows[y] > 0.35f, $"At V {v:0.000} the gravel is only {rows[y]:0.000} opaque, inside the spill.");
            }
        }

        // The border fade, from V 0.05 to 0.13, trims the oval's end towards V 0 as well, so at V 0.09 it is halved to
        // 0.1 and covers 15 % of the grit: that edge goes from its first grains to thick in about 4 % of the image.
        // The end towards V 1, at 0.84, is clear of the fade at 0.87; there the cover falls from 87 % at V 0.76 through
        // 37 % at 0.8 to 12 % at 0.82, so that edge takes about 6 %, half again as long.
        var rise = Width(rows, size, from: 0.05f, step: 1);
        var fall = Width(rows, size, from: 0.88f, step: -1);
        Assert.True(rise * 1.5f < fall, $"The spill's edge is {rise:0.000} of the image wide towards V 0 and {fall:0.000} towards V 1.");
        Assert.True(At(0.8f) < At(0.11f) / 2f, $"At V 0.8 the gravel is {At(0.8f):0.000} opaque and at V 0.11 {At(0.11f):0.000}.");
    }

    // How wide the spill's edge is, walking in from a bare row at `from`: from the first row more than 0.05 opaque to
    // the first more than 0.35, as a part of the image.
    private static float Width(float[] rows, int size, float from, int step)
    {
        int? first = null;
        for (var y = (int)(from * size); y >= 0 && y < size; y += step)
        {
            if (first is null && rows[y] > 0.05f)
            {
                first = y;
            }

            if (rows[y] > 0.35f)
            {
                return Math.Abs(y - first!.Value) / (float)size;
            }
        }

        return 1f;
    }

    private static List<string> Sorted(List<Matrix4x4> boxes)
    {
        return boxes.Select(box => box.ToString()).Order(StringComparer.Ordinal).ToList();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Gravel_lies_round_the_barrels_foot_with_its_sharp_edge_against_the_staves(int seed)
    {
        // A spill lies thick from 0.39 of its length out from its middle towards its +Z end (the image's V 0.11) back
        // to 0.25 the other way (V 0.75), as the image's own test above finds.
        // The ring's spills point straight at the barrel; the path's spills nearby point at the path.
        var round = GravelSpills.Boxes(seed)
            .Where(box =>
            {
                var toBarrel = GravelSpills.BarrelCentre - GravelSpills.Centre(box);
                var along = new Vector2(box.M31, -box.M33);
                return toBarrel.Length() < 1f && Vector2.Dot(Vector2.Normalize(toBarrel), Vector2.Normalize(along)) > 0.999f;
            })
            .ToList();

        Assert.NotEmpty(round);
        foreach (var box in round)
        {
            var centre = GravelSpills.Centre(box);
            var along = new Vector2(box.M31, -box.M33);
            var sharpEdge = Vector2.Distance(centre + along * 0.39f, GravelSpills.BarrelCentre);
            var outerThick = Vector2.Distance(centre - along * 0.25f, GravelSpills.BarrelCentre);

            // From the foot of the staves out onto the mud round them.
            Assert.InRange(sharpEdge, GravelSpills.BarrelFoot - 0.05f, GravelSpills.BarrelFoot + 0.1f);
            Assert.InRange(outerThick, GravelSpills.BarrelFoot + 0.2f, 2f);
        }
    }
}
