using System.Numerics;
using Rendition;
using Super;
using Super.Testing;

namespace Hamlet.Tests;

// Each building is a baked chunk, and a chunk's contents are its static draws with their colours, so every
// building that draws the same static parts in the same colours shares one bake.
public sealed class ChunkTests
{
    private static HeadlessRun<HamletGame> Start(int seed)
    {
        var run = HeadlessGame.Create<HamletGame>(seed);
        run.Ticks(1);
        return run;
    }

    /// <summary>
    /// The villages of seeds 1 to 12, each drawn once, in one game: R makes the next seed's village. Building the
    /// kinds' static meshes takes seconds, so one game is much quicker than twelve.
    /// </summary>
    private static IEnumerable<int> Seeds(HeadlessRun<HamletGame> run)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            if (seed > 1)
            {
                run.Input.Press(Key.R);
                run.Ticks(1);
            }

            Assert.Equal(seed, run.Game.Village.Seed);
            yield return seed;
        }
    }

    /// <summary>The draws of building <paramref name="index"/>'s parts, by its keys, in the plan's part order.</summary>
    private static List<ModelDraw> PartsOf(HeadlessRun<HamletGame> run, int index)
    {
        var plan = run.Game.Village.Buildings[index].Plan;
        int first = HamletGame.BuildingKey + index * HamletGame.KeysPerBuilding;
        return [.. run.Drawn.Where(draw => draw.Key >= first && draw.Key < first + plan.Parts.Length).OrderBy(draw => draw.Key)];
    }

    [Fact]
    public void Seed_one_has_the_seven_chunk_variants_the_committed_bakes_are_for()
    {
        using var run = Start(1);
        var buildings = run.Game.Village.Buildings;

        var variants = new List<(string Name, (ModelRef Model, Vector3? Color)[] Contents)>();
        for (int i = 0; i < buildings.Count; i++)
        {
            var contents = PartsOf(run, i).Where(draw => draw.Static).Select(draw => (draw.Model, draw.Color)).ToArray();
            Assert.True(contents.Length > 0, $"Building {i}, a {buildings[i].Plan.Name}, draws nothing static, so its chunk has nothing to bake.");
            if (!variants.Any(variant => variant.Name == buildings[i].Plan.Name && variant.Contents.SequenceEqual(contents)))
            {
                variants.Add((buildings[i].Plan.Name, contents));
            }
        }

        // One well, one workshop, one tower and four cottage layouts: the seven .lightbake files in content/lighting.
        // If this changes, the bakes are stale and the Hamlet must be baked again.
        Assert.True(variants.Count == 7, $"Seed 1 has {variants.Count} chunk variants: {string.Join(", ", variants.Select(variant => variant.Name))}.");
    }

    [Fact]
    public void Every_seed_places_a_village_of_buildings()
    {
        // The village tries for 4 wells (one in the middle, three more), 4 towers, 8 workshops and 24 cottages: at
        // most 40 buildings. A building that finds no spot is dropped, so a seed can place fewer; a village short of
        // three quarters of its 40 would be a thin one.
        using var run = Start(1);
        foreach (int seed in Seeds(run))
        {
            int count = run.Game.Village.Buildings.Count;
            Assert.True(count <= 40, $"Seed {seed} placed {count} buildings, more than the 40 it tries for.");
            Assert.True(count >= 30, $"Seed {seed} placed only {count} buildings.");
        }
    }

    [Fact]
    public void Every_seed_gives_each_mixed_light_a_mask_slot()
    {
        // The frame has four shadow mask slots, shared by every baked chunk's Mixed lights (the forge fires). Lights of
        // one placed chunk, or of chunks whose ranges overlap, can't share a slot, so a village with too many fires
        // close together would leave one without a slot, and it would lose its shadow. The Hamlet has no baked space,
        // so no slot is held for a space's sun; the real assignment is run with none.
        using var run = Start(1);
        foreach (int seed in Seeds(run))
        {
            var fires = new List<(int Key, int Chunk, Vector3 Position, float Range)>();
            foreach (var building in run.Game.Village.Buildings)
            {
                foreach (var lamp in building.Plan.Lamps.Where(lamp => lamp.Mode == LightMode.Mixed))
                {
                    fires.Add((fires.Count, building.Index, building.ToWorld(lamp.Position), lamp.Range));
                }
            }

            Assert.NotEmpty(fires);
            var slots = new int[fires.Count];
            var unslotted = new List<int>();
            Rendition.MaskSlots.Assign(-1, [], fires.ToArray(), slots, unslotted);

            Assert.True(unslotted.Count == 0, $"Seed {seed}: {unslotted.Count} of {fires.Count} Mixed lights found no mask slot.");
            Assert.All(slots, slot => Assert.InRange(slot, 0, 3));
        }
    }

    [Fact]
    public void No_seed_places_two_buildings_with_overlapping_boxes()
    {
        // A part or a lamp in two chunks' boxes goes to the first, so overlapping boxes would put one building's
        // parts in its neighbour's bake. Points every quarter metre over each box's footprint must lie in no other box.
        using var run = Start(1);
        foreach (int seed in Seeds(run))
        {
            var buildings = run.Game.Village.Buildings;
            var inverses = new Matrix4x4[buildings.Count];
            for (int i = 0; i < buildings.Count; i++)
            {
                Matrix4x4.Invert(buildings[i].Placement, out inverses[i]);
            }

            for (int i = 0; i < buildings.Count; i++)
            {
                var plan = buildings[i].Plan;
                for (float x = plan.Min.X; x <= plan.Max.X; x += 0.25f)
                {
                    for (float z = plan.Min.Z; z <= plan.Max.Z; z += 0.25f)
                    {
                        var world = buildings[i].ToWorld(new Vector3(x, 0f, z));
                        for (int j = 0; j < buildings.Count; j++)
                        {
                            if (j != i && InFootprint(buildings[j].Plan, inverses[j], world))
                            {
                                Assert.Fail($"Seed {seed}: building {i}, a {plan.Name}, reaches into building {j}'s box, a {buildings[j].Plan.Name}, at {world}.");
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>Whether a point lies inside a building's box across the ground, more than a millimetre in.</summary>
    private static bool InFootprint(BuildingPlan plan, Matrix4x4 inverse, Vector3 world)
    {
        var local = Vector3.Transform(world, inverse);
        const float Inside = 0.001f;
        return local.X > plan.Min.X + Inside && local.X < plan.Max.X - Inside && local.Z > plan.Min.Z + Inside && local.Z < plan.Max.Z - Inside;
    }

    [Fact]
    public void Every_seed_draws_each_kind_with_the_same_colours()
    {
        // The colour each roof or accent part of each kind and cottage layout was drawn with, the first time it was seen.
        var colours = new Dictionary<(string Name, CottageLayout? Layout, int Part), Vector3>();
        using var run = Start(1);
        foreach (int seed in Seeds(run))
        {
            var buildings = run.Game.Village.Buildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                var plan = buildings[i].Plan;
                var drawn = PartsOf(run, i);
                Assert.Equal(plan.Parts.Length, drawn.Count);
                for (int p = 0; p < plan.Parts.Length; p++)
                {
                    if (plan.Parts[p].Tint is not (PartTint.Roof or PartTint.Accent))
                    {
                        continue;
                    }

                    var colour = Assert.NotNull(drawn[p].Color);
                    var variant = (plan.Name, plan.Layout, p);
                    if (colours.TryGetValue(variant, out var first))
                    {
                        Assert.True(first == colour, $"Seed {seed} drew part {p} of a {plan.Name} {plan.Layout} in {colour}, but another in {first}.");
                    }
                    else
                    {
                        colours[variant] = colour;
                    }
                }
            }
        }

        // Every kind has a strong colour, so each must have been seen.
        foreach (string kind in (string[])["cottage", "workshop", "tower", "well"])
        {
            Assert.Contains(colours.Keys, variant => variant.Name == kind);
        }
    }
}
