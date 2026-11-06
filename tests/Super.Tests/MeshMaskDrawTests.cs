using System.Numerics;
using Rendition;

namespace Super.Tests;

public class MeshMaskDrawTests
{
    private static readonly ModelRef Body = new(3);

    /// <summary>Parts that tell apart by their first index, each with the given index count.</summary>
    private static DrawItem[] Parts(params uint[] indexCounts) =>
        indexCounts.Select((count, i) => new DrawItem { FirstIndex = (uint)(i * 100), IndexCount = count }).ToArray();

    [Fact]
    public void A_mask_made_for_another_model_is_explained()
    {
        var view = new View();
        var mask = new MeshMask(5, 4, [0]);
        view.Begin(0f);

        var problem = Assert.Throws<ArgumentException>(() => view.Model(7, Body, Matrix4x4.Identity, mask));

        Assert.Equal("meshes", problem.ParamName);
        Assert.StartsWith("The mesh mask was made for model 5, not model 3. Make it with Content.Meshes for this model.", problem.Message);
        Assert.Empty(view.Drawn.ToArray());
    }

    [Fact]
    public void A_mask_for_a_model_with_another_part_count_is_explained()
    {
        var mask = new MeshMask(3, 4, [0]);
        var draws = new DrawItem[8];

        var problem = Assert.Throws<InvalidOperationException>(() => SceneRenderer.AddShown(Parts(3, 3, 3, 3, 3), mask, ref draws, 0));

        Assert.Equal(
            "The mesh mask was made for a model drawn in 4 parts, but the model being drawn has 5. Make it from the same cooked file.",
            problem.Message);
    }

    [Fact]
    public void Shown_parts_are_added_after_the_draws_already_there()
    {
        var mask = new MeshMask(3, 3, [0, 2]);
        var draws = new DrawItem[2];
        draws[0] = new DrawItem { FirstIndex = 999 };

        int count = SceneRenderer.AddShown(Parts(3, 6, 9), mask, ref draws, 1);

        Assert.Equal(3, count);
        Assert.Equal(999u, draws[0].FirstIndex);
        Assert.Equal(0u, draws[1].FirstIndex);
        Assert.Equal(200u, draws[2].FirstIndex);
    }

    [Fact]
    public void No_mask_adds_every_part()
    {
        var draws = Array.Empty<DrawItem>();

        int count = SceneRenderer.AddShown(Parts(3, 6), null, ref draws, 0);

        Assert.Equal(2, count);
        Assert.Equal([0u, 100u], draws.Take(count).Select(d => d.FirstIndex));
    }

    [Fact]
    public void Parts_past_the_first_64_are_shown()
    {
        var mask = new MeshMask(3, 70, [0, 63, 64, 69]);
        var draws = Array.Empty<DrawItem>();

        int count = SceneRenderer.AddShown(Parts(Enumerable.Repeat(3u, 70).ToArray()), mask, ref draws, 0);

        Assert.Equal([0u, 6300u, 6400u, 6900u], draws.Take(count).Select(d => d.FirstIndex));
    }

    [Fact]
    public void Triangles_count_only_the_shown_parts()
    {
        var mask = new MeshMask(3, 3, [1, 2]);

        // 6 + 9 indices shown, 3 hidden: 15 indices are 5 triangles.
        Assert.Equal(5, SceneRenderer.ShownTriangles(Parts(3, 6, 9), mask));
        Assert.Equal(6, SceneRenderer.ShownTriangles(Parts(3, 6, 9), null));
    }
}
