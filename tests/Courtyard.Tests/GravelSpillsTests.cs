using System.Numerics;
using Rendition.Passes;

namespace Courtyard.Tests;

/// <summary>
/// Where the Courtyard lays its gravel decals: in the mud off the path's edges and round the barrel, never out on the
/// paving, in the puddle or against the wall, and few enough that the wall's grime and the gravel fit in one frame.
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
    public void No_spill_lies_with_its_middle_in_the_puddle(int seed)
    {
        var inPuddle = GravelSpills.Boxes(seed)
            .Select(GravelSpills.Centre)
            .Where(centre => ((centre - GravelSpills.PuddleCentre) / GravelSpills.PuddleRadii).LengthSquared() <= 1f)
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

    [Theory]
    [MemberData(nameof(Seeds))]
    public void The_grime_and_the_gravel_fit_in_one_frames_decals(int seed)
    {
        var decals = WallStains.Boxes().Count + GravelSpills.Boxes(seed).Count;

        Assert.InRange(decals, 1, DecalBatch.MaxDecals);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Gravel_lies_round_the_barrels_foot_with_its_thick_side_against_the_staves(int seed)
    {
        // A spill is thickest from 0.05 to 0.44 of its length out from its middle towards its +Z end.
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
            var outerThick = Vector2.Distance(centre + along * 0.05f, GravelSpills.BarrelCentre);
            var innerThick = Vector2.Distance(centre + along * 0.44f, GravelSpills.BarrelCentre);

            // From under the edge of the staves out onto the mud round them.
            Assert.InRange(innerThick, 0f, GravelSpills.BarrelFoot + 0.1f);
            Assert.InRange(outerThick, GravelSpills.BarrelFoot + 0.1f, 2f);
        }
    }
}
