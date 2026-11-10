using System.Numerics;
using System.Runtime.CompilerServices;
using Rendition.Passes;
using Super;
using Super.Cook;
using Super.Cook.Models;

namespace Courtyard.Tests;

/// <summary>
/// Cooks content/decals/courtyard.json with the materials it names into a temp folder, once for the class, and opens
/// it as the game does.
/// </summary>
public sealed class CookedDecals : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "courtyard-decals-" + Guid.NewGuid().ToString("N"));

    public CookedDecals()
    {
        var only = new[] { "decals/**", "materials/stain*", "materials/rust-streak*", "materials/grime*" };
        var result = new CookPipeline(ModelCookers.All, new CookOptions())
            .Cook([new FolderRoot(ContentFolder(), only)], new FolderStore(_folder));
        Problems = result.Problems.Select(problem => problem.ToString()).ToList();
        Content = new Content(new CookedFolder(_folder));
    }

    public Content Content { get; }

    public IReadOnlyList<string> Problems { get; }

    public IReadOnlyList<PlacedDecal> Decals =>
        Content.LoadAll<DecalList>(CourtyardGame.DecalFolder)[CourtyardGame.DecalListId].Decals;

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>The example's content folder, found from this file so the test reads the real list.</summary>
    private static string ContentFolder([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "Courtyard", "content"));
    }
}

// Cooks the stain graphs, which takes a while.
[Trait("Speed", "Slow")]
public sealed class DecalCatalogTests(CookedDecals cooked) : IClassFixture<CookedDecals>
{
    [Fact]
    public void The_courtyards_list_cooks_and_loads_with_the_barrel_ring_under_the_barrel()
    {
        Assert.True(cooked.Problems.Count == 0, string.Join(Environment.NewLine, cooked.Problems));

        var ring = Assert.Single(cooked.Decals, decal => decal.Name == "barrel-ring");

        // art/courtyard.py stands the barrel at Blender (0.2, 3.25) on the ground, which is (0.2, 0, -3.25) here.
        Assert.Equal(new Vector3(0.2f, 0f, -3.25f), ring.Position);
        Assert.Equal("materials/stain.material.cs", ring.Material);
        Assert.Null(ring.Facing);
    }

    [Fact]
    public void Every_decal_names_a_cooked_material()
    {
        var missing = new List<string>();
        foreach (var decal in cooked.Decals)
        {
            try
            {
                cooked.Content.Surface(decal.Material);
            }
            catch (ContentException problem)
            {
                missing.Add($"{decal.Name}: {problem.Message}");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void No_two_decals_share_a_name()
    {
        var shared = cooked.Decals
            .GroupBy(decal => decal.Name)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} is used {group.Count()} times")
            .ToList();

        Assert.True(shared.Count == 0, string.Join(Environment.NewLine, shared));
    }

    [Fact]
    public void The_grime_the_list_and_the_gravel_fit_in_one_frames_decals()
    {
        int decals = WallStains.Boxes().Count + cooked.Decals.Count + GravelSpills.Boxes(CourtyardGame.GravelSeed).Count;

        Assert.InRange(decals, 1, DecalBatch.MaxDecals);
    }
}

public sealed class PlacedDecalTests
{
    // The box's third row is the way the image's V 0 edge points, scaled by the decal's length.
    private static Vector3 Top(Matrix4x4 box) => Vector3.Normalize(new Vector3(box.M31, box.M32, box.M33));

    private static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, got {actual}.");
    }

    [Fact]
    public void A_ground_decal_unturned_points_its_top_at_the_brick_wall()
    {
        var decal = new PlacedDecal("a", "m", Vector3.Zero, new Vector3(1f, 2f, 0.5f));

        Near(-Vector3.UnitZ, Top(decal.Box()));
    }

    [Fact]
    public void A_ground_decal_turned_90_degrees_points_its_top_left()
    {
        // Seen from above, anticlockwise a quarter turn from towards the wall (−Z) is towards −X.
        var decal = new PlacedDecal("a", "m", Vector3.Zero, new Vector3(1f, 2f, 0.5f), Turn: 90f);

        Near(-Vector3.UnitX, Top(decal.Box()));
    }

    [Fact]
    public void A_wall_decal_points_its_top_up_the_wall()
    {
        var decal = new PlacedDecal("a", "m", Vector3.Zero, new Vector3(1f, 2f, 0.5f), Facing: Vector3.UnitZ);

        Near(Vector3.UnitY, Top(decal.Box()));
    }

    [Fact]
    public void The_box_is_sized_and_placed_as_the_decal_says()
    {
        var decal = new PlacedDecal("a", "m", new Vector3(1f, 2f, 3f), new Vector3(0.4f, 0.6f, 0.2f), Facing: Vector3.UnitX);
        var box = decal.Box();

        Assert.Equal(0.4f, new Vector3(box.M11, box.M12, box.M13).Length(), 5);
        Assert.Equal(0.2f, new Vector3(box.M21, box.M22, box.M23).Length(), 5);
        Assert.Equal(0.6f, new Vector3(box.M31, box.M32, box.M33).Length(), 5);
        Near(Vector3.UnitX, Vector3.Normalize(new Vector3(box.M21, box.M22, box.M23)));
        Assert.Equal(new Vector3(1f, 2f, 3f), box.Translation);
    }
}
