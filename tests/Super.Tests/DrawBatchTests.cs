using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>Which draws the renderer sends to the GPU as one instanced draw.</summary>
public sealed class DrawBatchTests
{
    private static DrawItem Part(uint firstIndex, float x = 0f) => new()
    {
        Mesh = new MeshHandle(1),
        FirstIndex = firstIndex,
        IndexCount = 36,
        Material = new MaterialHandle(2),
        Transform = Matrix4x4.CreateTranslation(x, 0f, 0f),
    };

    private static List<DrawBatch> Batch(DrawItem[] draws, bool keepOrder, out int[] order)
    {
        order = [.. Enumerable.Range(0, draws.Length)];
        var batches = new List<DrawBatch>();
        SceneDraws.Batch(draws, order, keepOrder, batches);
        return batches;
    }

    [Fact]
    public void Copies_of_one_part_that_look_the_same_are_one_draw()
    {
        DrawItem[] draws = [Part(0, x: 1f), Part(0, x: 2f), Part(0, x: 3f)];

        var batch = Assert.Single(Batch(draws, keepOrder: false, out _));

        Assert.Equal(new DrawBatch(0, 3), batch);
    }

    [Fact]
    public void Copies_of_a_model_with_two_parts_are_one_draw_for_each_part()
    {
        // As a view hands them over: each copy's parts together.
        DrawItem[] draws = [Part(0, 1f), Part(36, 1f), Part(0, 2f), Part(36, 2f), Part(0, 3f), Part(36, 3f)];

        var batches = Batch(draws, keepOrder: false, out int[] order);

        Assert.Equal(2, batches.Count);
        foreach (var batch in batches)
        {
            Assert.Equal(3, batch.Count);
            var parts = order.AsSpan(batch.First, batch.Count).ToArray().Select(i => draws[i].FirstIndex).Distinct();
            Assert.Single(parts);
        }

        Assert.Equal([0, 1, 2, 3, 4, 5], order.Order());
    }

    [Fact]
    public void Draws_that_look_different_are_drawn_apart()
    {
        var plain = Part(0);
        DrawItem[] different =
        [
            plain with { Tint = new Vector3(1f, 0f, 0f) },
            plain with { Ghost = new Ghost(Vector3.One) },
            plain with { Material = new MaterialHandle(3) },
            plain with { Mesh = new MeshHandle(4) },
            plain with { IndexCount = 12 },
        ];

        foreach (var other in different)
        {
            Assert.Equal(2, Batch([plain, other], keepOrder: false, out _).Count);
        }
    }

    [Fact]
    public void Fish_that_swim_out_of_step_are_one_draw()
    {
        // The swim is each copy's own, like its matrix, so a shoal's hundred phases don't make a hundred draws.
        DrawItem[] shoal =
        [
            Part(0, x: 1f) with { Swim = new SwimMotion(0.1f, 1f, 0.5f) },
            Part(0, x: 2f) with { Swim = new SwimMotion(0.1f, 1f, 2.5f) },
            Part(0, x: 3f) with { Swim = new SwimMotion(0.2f, 1.5f, 4f) },
            Part(0, x: 4f),
        ];

        var batch = Assert.Single(Batch(shoal, keepOrder: false, out _));

        Assert.Equal(new DrawBatch(0, 4), batch);
    }

    [Fact]
    public void Copies_that_look_the_same_keep_the_order_they_came_in()
    {
        var red = Part(0) with { Tint = new Vector3(1f, 0f, 0f) };
        var other = Part(0) with { Mesh = new MeshHandle(7) };
        // Positions tell the copies apart: 0 to 5 as listed.
        DrawItem[] draws =
        [
            red with { Transform = Matrix4x4.CreateTranslation(0f, 0f, 0f) },
            other with { Transform = Matrix4x4.CreateTranslation(1f, 0f, 0f) },
            red with { Transform = Matrix4x4.CreateTranslation(2f, 0f, 0f) },
            Part(0, x: 3f),
            other with { Transform = Matrix4x4.CreateTranslation(4f, 0f, 0f) },
            red with { Transform = Matrix4x4.CreateTranslation(5f, 0f, 0f) },
        ];

        var batches = Batch(draws, keepOrder: false, out int[] order);

        Assert.Equal(3, batches.Count);
        var groups = batches.Select(batch => order.AsSpan(batch.First, batch.Count).ToArray()).OrderBy(group => group[0]).ToArray();
        Assert.Equal([0, 2, 5], groups[0]);
        Assert.Equal([1, 4], groups[1]);
        Assert.Equal([3], groups[2]);
    }

    [Fact]
    public void A_meshs_parts_are_drawn_in_the_order_the_mesh_holds_them()
    {
        // A model's parts are laid out to be drawn in order, and drawing them in another costs overdraw.
        DrawItem[] draws =
        [
            Part(72) with { Material = new MaterialHandle(1) },
            Part(0) with { Material = new MaterialHandle(9) },
            Part(36) with { Material = new MaterialHandle(5) },
            Part(108) with { Material = new MaterialHandle(3) },
        ];

        Batch(draws, keepOrder: false, out int[] order);

        Assert.Equal([0u, 36u, 72u, 108u], order.Select(i => draws[i].FirstIndex));
    }

    [Fact]
    public void Skinned_draws_are_drawn_one_at_a_time()
    {
        var skinned = Part(0) with { Bones = new BoneRange(0, 20) };

        Assert.Equal(2, Batch([skinned, skinned], keepOrder: false, out _).Count);
    }

    [Fact]
    public void Draws_that_are_apart_in_the_list_meet_when_order_doesnt_matter()
    {
        var red = Part(0) with { Tint = new Vector3(1f, 0f, 0f) };
        DrawItem[] draws = [Part(0), red, Part(0), red];

        Assert.Equal(2, Batch(draws, keepOrder: false, out _).Count);
    }

    [Theory]
    [InlineData(false, MaterialFlags.None, false)]
    [InlineData(false, MaterialFlags.TwoSided | MaterialFlags.Foliage | MaterialFlags.WorldVariation, false)]
    [InlineData(true, MaterialFlags.None, true)]
    [InlineData(false, MaterialFlags.Dithered, true)]
    [InlineData(false, MaterialFlags.Transparent | MaterialFlags.TwoSided, true)]
    public void Only_a_material_that_leaves_pixels_out_skips_the_solid_pipelines(
        bool cutouts, MaterialFlags flags, bool expected)
    {
        Assert.Equal(expected, SceneDraws.HasHoles(cutouts, flags));
    }

    [Fact]
    public void Kept_order_shares_a_draw_only_between_neighbours()
    {
        var red = Part(0) with { Tint = new Vector3(1f, 0f, 0f) };
        DrawItem[] draws = [Part(0), Part(0), red, Part(0)];

        var batches = Batch(draws, keepOrder: true, out int[] order);

        Assert.Equal([new DrawBatch(0, 2), new DrawBatch(2, 1), new DrawBatch(3, 1)], batches);
        Assert.Equal([0, 1, 2, 3], order);
    }
}
