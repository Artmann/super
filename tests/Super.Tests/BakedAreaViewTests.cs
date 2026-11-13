using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public class BakedAreaViewTests
{
    private static readonly Vector3 Min = new(-8f, -1f, -12f);
    private static readonly Vector3 Max = new(8f, 6f, 12f);

    [Fact]
    public void Baked_lighting_is_kept_for_the_frame()
    {
        var view = new View();
        view.Begin(0f);

        view.BakedLighting("crypt", Min, Max, 1f);

        Assert.Equal(new BakedSpace("crypt", Min, Max, 1f), view.BakedSpace);
    }

    [Fact]
    public void Begin_forgets_the_baked_areas()
    {
        var view = new View();
        view.Begin(0f);
        view.BakedLighting("crypt", Min, Max, 1f);
        view.BakedChunk("room", Matrix4x4.Identity, -Vector3.One, Vector3.One);

        view.Begin(0f);

        Assert.Null(view.BakedSpace);
        Assert.Empty(view.BakedChunks);
        // With the old space gone a new one can be named.
        view.BakedLighting("yard", Min, Max, 1f);
        Assert.Equal("yard", view.BakedSpace?.Name);
    }

    [Fact]
    public void Second_baked_space_in_a_frame_is_explained()
    {
        var view = new View();
        view.BakedLighting("crypt", Min, Max, 1f);

        var error = Assert.Throws<InvalidOperationException>(() => view.BakedLighting("yard", Min, Max, 1f));

        Assert.Contains("\"crypt\"", error.Message);
        Assert.Contains("\"yard\"", error.Message);
        Assert.Equal("crypt", view.BakedSpace?.Name);
    }

    [Fact]
    public void Baked_space_over_4096_probes_is_explained()
    {
        var view = new View();

        // 21 x 21 x 21 = 9261 probes.
        var error = Assert.Throws<ArgumentException>(() => view.BakedLighting("hall", Vector3.Zero, new Vector3(20f), 1f));

        Assert.Contains("9261", error.Message);
        Assert.Contains("4096", error.Message);
        Assert.Null(view.BakedSpace);
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(2f, -2f)]
    public void Baked_space_that_is_empty_or_inside_out_is_explained(float minX, float maxX)
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(
            () => view.BakedLighting("crypt", new Vector3(minX, 0f, 0f), new Vector3(maxX, 4f, 4f), 1f));

        Assert.Contains("along x", error.Message);
        Assert.Null(view.BakedSpace);
    }

    [Fact]
    public void Baked_space_box_is_explained_with_invariant_numbers_under_swedish_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentException>(
                () => view.BakedLighting("crypt", new Vector3(0f, 2.5f, 0f), new Vector3(4f, -0.5f, 4f), 1f));

            Assert.Contains("along y than it starts, but it runs from 2.5 to -0.5.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Baked_space_with_no_probe_spacing_is_explained(float spacing)
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.BakedLighting("crypt", Min, Max, spacing));

        Assert.Equal("probeSpacing", error.ParamName);
        Assert.Contains("more than 0", error.Message);
        Assert.Null(view.BakedSpace);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..\\x")]
    [InlineData("")]
    [InlineData("a b")]
    public void Baked_name_with_a_slash_is_explained(string name)
    {
        var view = new View();

        var space = Assert.Throws<ArgumentException>(() => view.BakedLighting(name, Min, Max, 1f));
        var chunk = Assert.Throws<ArgumentException>(
            () => view.BakedChunk(name, Matrix4x4.Identity, -Vector3.One, Vector3.One));

        Assert.Contains($"\"{name}\"", space.Message);
        Assert.Contains("letters, digits", space.Message);
        Assert.Contains($"\"{name}\"", chunk.Message);
        Assert.Null(view.BakedSpace);
        Assert.Empty(view.BakedChunks);
    }

    [Fact]
    public void Baked_chunk_is_kept_in_the_order_given()
    {
        var view = new View();
        var first = new BakedChunk("cottage", Matrix4x4.CreateTranslation(10f, 0f, 0f), new(-6f, 0f, -8f), new(6f, 7f, 8f));
        var second = new BakedChunk("barn", Matrix4x4.CreateRotationY(1f), new(-4f, 0f, -4f), new(4f, 5f, 4f));
        var third = new BakedChunk("well", Matrix4x4.CreateTranslation(0f, 0f, 30f), -Vector3.One, Vector3.One);

        foreach (var chunk in new[] { first, second, third })
        {
            view.BakedChunk(chunk.Name, chunk.Transform, chunk.Min, chunk.Max);
        }

        Assert.Equal([first, second, third], view.BakedChunks);
    }

    [Fact]
    public void Lightmap_density_is_kept_on_the_space_and_each_chunk()
    {
        var view = new View();

        view.BakedLighting("crypt", Min, Max, 1f, texelsPerMetre: 8);
        view.BakedChunk("well", Matrix4x4.Identity, -Vector3.One, Vector3.One, texelsPerMetre: 40);
        view.BakedChunk("barn", Matrix4x4.Identity, -Vector3.One, Vector3.One);

        Assert.Equal(8, view.BakedSpace?.TexelsPerMetre);
        Assert.Equal([40, 20], view.BakedChunks.Select(chunk => chunk.TexelsPerMetre));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Lightmap_density_out_of_range_is_explained(int texelsPerMetre)
    {
        var view = new View();

        var space = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.BakedLighting("crypt", Min, Max, 1f, texelsPerMetre));
        var chunk = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.BakedChunk("well", Matrix4x4.Identity, -Vector3.One, Vector3.One, texelsPerMetre));

        Assert.StartsWith(
            $"The baked space's lightmap density must be 1 to 200 texels a metre, not {texelsPerMetre}.", space.Message);
        Assert.StartsWith(
            $"The baked chunk's lightmap density must be 1 to 200 texels a metre, not {texelsPerMetre}.", chunk.Message);
        Assert.Null(view.BakedSpace);
        Assert.Empty(view.BakedChunks);
    }

    [Fact]
    public void Chunk_257_is_explained()
    {
        var view = new View();
        for (int i = 0; i < 256; i++)
        {
            view.BakedChunk("room", Matrix4x4.CreateTranslation(i * 20f, 0f, 0f), -Vector3.One, Vector3.One);
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => view.BakedChunk("room", Matrix4x4.CreateTranslation(-20f, 0f, 0f), -Vector3.One, Vector3.One));

        Assert.Contains("256", error.Message);
        Assert.Equal(256, view.BakedChunks.Count);
    }

    [Fact]
    public void Chunk_with_a_scaled_transform_is_explained()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(() => view.BakedChunk(
            "room", Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(1f, 0f, 0f), -Vector3.One, Vector3.One));

        Assert.Contains("\"room\"", error.Message);
        Assert.Contains("rotation and translation", error.Message);
        Assert.Empty(view.BakedChunks);
    }

    [Fact]
    public void Chunk_with_a_mirrored_transform_is_explained()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(() => view.BakedChunk(
            "room", Matrix4x4.CreateScale(-1f, 1f, 1f), -Vector3.One, Vector3.One));

        Assert.Contains("rotation and translation", error.Message);
    }

    [Fact]
    public void Chunk_turned_and_moved_is_rigid()
    {
        var view = new View();
        var transform = Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.3f, 1.9f) * Matrix4x4.CreateTranslation(100f, -3f, 250f);

        view.BakedChunk("room", transform, -Vector3.One, Vector3.One);

        Assert.Single(view.BakedChunks);
    }

    [Fact]
    public void Chunk_box_that_is_inside_out_is_explained()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(
            () => view.BakedChunk("room", Matrix4x4.Identity, new Vector3(0f, 0f, 3f), new Vector3(1f, 1f, 2f)));

        Assert.Contains("along z", error.Message);
        Assert.Empty(view.BakedChunks);
    }

    [Fact]
    public void Chunk_names_may_repeat()
    {
        var view = new View();
        var here = Matrix4x4.CreateTranslation(0f, 0f, 0f);
        var there = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(30f, 0f, 0f);

        view.BakedChunk("room", here, -Vector3.One, Vector3.One);
        view.BakedChunk("room", there, -Vector3.One, Vector3.One);

        Assert.Equal(2, view.BakedChunks.Count);
        Assert.Equal(here, view.BakedChunks[0].Transform);
        Assert.Equal(there, view.BakedChunks[1].Transform);
    }

    [Fact]
    public void Edge_blend_is_kept_on_each_chunk_and_is_half_a_metre_unless_given()
    {
        var view = new View();

        view.BakedChunk("well", Matrix4x4.Identity, -Vector3.One, Vector3.One, edgeBlend: 0f);
        view.BakedChunk("barn", Matrix4x4.Identity, -Vector3.One, Vector3.One);
        view.BakedChunk("hall", Matrix4x4.Identity, -Vector3.One, Vector3.One, edgeBlend: 1f);

        Assert.Equal([0f, 0.5f, 1f], view.BakedChunks.Select(chunk => chunk.EdgeBlend));
    }

    [Fact]
    public void Edge_blend_over_half_the_box_throws()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();
            // 4 metres across x and 3 across z, so at most 1.5 metres of band fit. The box is only half a metre tall,
            // which doesn't count: the band runs in from the sides, not the top or bottom.
            var min = new Vector3(-2f, 0f, -1.5f);
            var max = new Vector3(2f, 0.5f, 1.5f);
            view.BakedChunk("room", Matrix4x4.Identity, min, max, edgeBlend: 1.5f);

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.BakedChunk("room", Matrix4x4.Identity, min, max, edgeBlend: 1.75f));

            Assert.StartsWith(
                "The baked chunk \"room\"'s edge blend of 1.75 metres is more than half its box's smaller side across x and z, 3 metres.",
                error.Message);
            Assert.Equal("edgeBlend", error.ParamName);
            Assert.Single(view.BakedChunks);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Default_edge_blend_on_a_small_chunk_throws_and_says_how_to_turn_it_off()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();
            // 0.8 metres across z, so at most 0.4 metres of band fit, and the default is 0.5.
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.BakedChunk("crate", Matrix4x4.Identity, new Vector3(-1f, 0f, -0.4f), new Vector3(1f, 1f, 0.4f)));

            Assert.StartsWith(
                "The baked chunk \"crate\"'s edge blend of 0.5 metres is more than half its box's smaller side across x and z, 0.8 metres. "
                + "0.5 metres is the default; pass edgeBlend: 0 for a hard edge, or a smaller edge blend.",
                error.Message);
            Assert.Equal("edgeBlend", error.ParamName);
            Assert.Empty(view.BakedChunks);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Negative_edge_blend_throws()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.BakedChunk("room", Matrix4x4.Identity, -Vector3.One, Vector3.One, edgeBlend: -0.25f));

        Assert.StartsWith("The baked chunk \"room\"'s edge blend must be 0 metres or more, not -0.25.", error.Message);
        Assert.Empty(view.BakedChunks);
    }

    [Fact]
    public void Edge_blend_that_is_not_a_number_throws()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => view.BakedChunk("room", Matrix4x4.Identity, -Vector3.One, Vector3.One, edgeBlend: float.NaN));

        Assert.StartsWith("The baked chunk \"room\"'s edge blend must be 0 metres or more, not NaN.", error.Message);
        Assert.Empty(view.BakedChunks);
    }

    [Fact]
    public void Baked_file_names()
    {
        Assert.Equal("lighting/courtyard.lightbake", BakeFiles.Space("courtyard"));
        Assert.Equal("lighting/room/00000000000000ab.lightbake", BakeFiles.Chunk("room", 0xAB));
    }
}
