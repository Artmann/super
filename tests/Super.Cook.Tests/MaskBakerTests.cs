using System.Globalization;
using System.Numerics;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class MaskBakerTests : IDisposable
{
    private const int Size = MaskBaker.DefaultSize;

    // Each face of the cube is an island 300 pixels square in the second UV set, in a grid of three
    // by two with 40 pixels between them, so a pixel's distance from an island's edge is easy to count.
    private const int Island = 300;

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private static readonly Vector3[] FaceNormals =
    [
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
    ];

    private static (int X, int Y) IslandCorner(int face) => (10 + face % 3 * 340, 10 + face / 3 * 340);

    // A cube size metres across standing on y = 0, centred on x and z, plus offset. Each face is two triangles wound
    // counter-clockwise from outside, and has an island of its own.
    private static MeshData Cube(Vector3 offset = default, float size = 1f)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        for (int face = 0; face < 6; face++)
        {
            var normal = FaceNormals[face];
            var up = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitZ : Vector3.UnitY;
            var across = Vector3.Cross(up, normal); // across × up is the normal, so the winding faces out.
            var centre = (new Vector3(0f, 0.5f, 0f) + normal * 0.5f) * size + offset;
            var (x, y) = IslandCorner(face);
            int first = positions.Count;
            foreach (var (s, t) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                positions.Add(centre + (across * s + up * t) * (0.5f * size));
                uvs.Add(new Vector2(x + (s + 1f) / 2f * Island, y + (t + 1f) / 2f * Island) / Size);
            }

            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return new MeshData(positions.ToArray(), uvs.ToArray(), indices.ToArray());
    }

    // The pixel at (dx, dy) from the corner of a face's island, from 0 to 1 in each channel.
    private static Vector4 Pixel(MaskImage image, int face, int dx, int dy)
    {
        var (x, y) = IslandCorner(face);
        int i = ((y + dy) * image.Size + x + dx) * 4;
        return new Vector4(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2], image.Rgba[i + 3]) / 255f;
    }

    private static int Face(Vector3 normal) => Array.IndexOf(FaceNormals, normal);

    [Fact]
    public void A_cube_bakes_high_curvature_along_its_edges_and_none_on_its_faces()
    {
        // A 25 cm block, the size of a crate's corner post or a plinth stone: each 300-pixel island spans 250 mm.
        var image = MaskBaker.Bake(Cube(size: 0.25f), Size);

        for (int face = 0; face < 6; face++)
        {
            // Flat in the middle of each face.
            Assert.InRange(Pixel(image, face, Island / 2, Island / 2).X, 0.47f, 0.53f);

            // The edge's wear is a band about two centimetres wide: 30.5 pixels in is 25.4 mm from the edge, and flat
            // already.
            Assert.InRange(Pixel(image, face, 30, Island / 2).X, 0.47f, 0.53f);

            // 12.5 pixels in is 10.4 mm from the edge, which is 0.5 + 0.5 * (1 - 10.4 / 25) = 0.79. The nearest of the
            // sixteen rays may run up to 11.25 degrees off square, which reads the edge up to 2% further away.
            Assert.InRange(Pixel(image, face, 12, Island / 2).X, 0.77f, 0.80f);

            // Two and a half pixels in from each of the island's four edges is about 2 mm from the cube's edge.
            Assert.True(Pixel(image, face, 2, Island / 2).X > 0.9f, $"face {face}, left edge");
            Assert.True(Pixel(image, face, Island - 3, Island / 2).X > 0.9f, $"face {face}, right edge");
            Assert.True(Pixel(image, face, Island / 2, 2).X > 0.9f, $"face {face}, bottom edge");
            Assert.True(Pixel(image, face, Island / 2, Island - 3).X > 0.9f, $"face {face}, top edge");
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(15f)]
    public void A_square_split_into_two_triangles_is_flat_along_its_diagonal_even_folded_a_little(float fold)
    {
        // A 20 cm square lying flat, split along the diagonal from (0, 0) to (1, 1). The corner at (1, 0), 0.141 m from
        // the diagonal, is bent down by the fold, as the triangles of a rough stone face are. One island covers it.
        float drop = 0.2f * MathF.Tan(fold * MathF.PI / 180f) / MathF.Sqrt(2f);
        var mesh = new MeshData(
            [new(0f, 0f, 0f), new(0.2f, -drop, 0f), new(0.2f, 0f, -0.2f), new(0f, 0f, -0.2f)],
            [new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.3f, 0.3f), new(0.1f, 0.3f)],
            [0, 1, 2, 0, 2, 3]);

        var image = MaskBaker.Bake(mesh, Size);

        // Along the diagonal, away from the square's own edges.
        for (int step = 60; step <= 140; step += 20)
        {
            int i = ((102 + step) * Size + 102 + step) * 4;
            Assert.InRange(image.Rgba[i] / 255f, 0.47f, 0.53f);
        }
    }

    [Fact]
    public void Worn_edges_come_and_go_along_a_long_edge_but_change_slowly()
    {
        // The breakup along the top of a 1 m bar, a millimetre at a time: a patch every BreakupSize or so, so some of
        // the edge wears fully and some not at all, and going from one to the other takes at least a centimetre.
        var values = Enumerable.Range(0, 1001).Select(i => MaskBaker.Breakup(new Vector3(i * 0.001f, 0.1f, 0f))).ToList();

        Assert.True(values.Min() < 0.1f, $"least {values.Min()}");
        Assert.True(values.Max() > 0.9f, $"most {values.Max()}");
        for (int i = 1; i < values.Count; i++)
        {
            Assert.True(MathF.Abs(values[i] - values[i - 1]) < 0.1f, $"{values[i - 1]} to {values[i]} at {i} mm");
        }
    }

    [Fact]
    public void The_breakup_is_baked_into_alpha_by_position()
    {
        var mesh = Cube(size: 0.25f);
        var image = MaskBaker.Bake(mesh, Size);

        // The middle of the top face is at (0, 0.25, 0).
        float expected = MaskBaker.Breakup(new Vector3(0f, 0.25f, 0f));
        float baked = Pixel(image, Face(Vector3.UnitY), Island / 2, Island / 2).W;
        Assert.InRange(baked, expected - 0.01f, expected + 0.01f);
    }

    [Fact]
    public void A_cube_inside_a_bigger_open_box_is_occluded_on_the_side_facing_the_wall()
    {
        // A floor and four walls 2 m high with no lid. The wall at x = 0.7 stands 0.2 m from the cube's +x face;
        // the one at x = -5.3 is 4.8 m from its -x face.
        var box = OpenBox(min: new Vector3(-5.3f, 0f, -3f), max: new Vector3(0.7f, 2f, 3f));

        var image = MaskBaker.Bake(Cube(), Size, box);

        float facingWall = Pixel(image, Face(Vector3.UnitX), Island / 2, Island / 2).Y;
        float free = Pixel(image, Face(-Vector3.UnitX), Island / 2, Island / 2).Y;
        Assert.True(facingWall <= free - 0.3f, $"facing the wall {facingWall}, free {free}");
        // Nothing within reach of the free side's middle, so it sees all of the sky.
        Assert.InRange(free, 0.95f, 1f);
    }

    [Fact]
    public void Without_the_box_both_sides_of_the_cube_see_the_whole_sky()
    {
        var image = MaskBaker.Bake(Cube(), Size);

        Assert.InRange(Pixel(image, Face(Vector3.UnitX), Island / 2, Island / 2).Y, 0.95f, 1f);
        Assert.InRange(Pixel(image, Face(-Vector3.UnitX), Island / 2, Island / 2).Y, 0.95f, 1f);
    }

    [Fact]
    public void Height_is_zero_at_the_foot_and_one_from_thirty_centimetres_up()
    {
        // A 1 m cube raised 2 m, so the height is measured from the cube's own foot, not from y = 0.
        var image = MaskBaker.Bake(Cube(new Vector3(0f, 2f, 0f)), Size);

        Assert.InRange(Pixel(image, Face(-Vector3.UnitY), Island / 2, Island / 2).Z, 0f, 0.01f);
        Assert.InRange(Pixel(image, Face(Vector3.UnitY), Island / 2, Island / 2).Z, 0.99f, 1f);
        // v runs up a side's island with y: 45.5 pixels of 300 up a 1 m side is 0.1517 m, and 0.1517 / 0.3 = 0.506.
        Assert.InRange(Pixel(image, Face(Vector3.UnitX), Island / 2, 45).Z, 0.49f, 0.52f);
        // 0.6 m up is past 0.3 m, so it's as clean as the top.
        Assert.InRange(Pixel(image, Face(Vector3.UnitX), Island / 2, 180).Z, 0.99f, 1f);
    }

    [Fact]
    public void A_mesh_shorter_than_thirty_centimetres_spans_its_own_height()
    {
        // A 10 cm cube raised 2 m: halfway up its side, at 0.05 m of 0.1 m, is 0.5, where over 0.3 m it would be 0.17.
        var image = MaskBaker.Bake(Cube(new Vector3(0f, 2f, 0f), size: 0.1f), Size);

        Assert.InRange(Pixel(image, Face(-Vector3.UnitY), Island / 2, Island / 2).Z, 0f, 0.01f);
        Assert.InRange(Pixel(image, Face(Vector3.UnitX), Island / 2, Island / 2).Z, 0.49f, 0.52f);
        Assert.InRange(Pixel(image, Face(Vector3.UnitY), Island / 2, Island / 2).Z, 0.99f, 1f);
    }

    [Fact]
    public void Each_mesh_measures_height_from_its_own_foot()
    {
        // A 1 m cube on the ground is mesh 0, and a 20 cm upright square from y = 5 to 5.2 is mesh 1, with an island
        // of its own 300 pixels square at (10, 700). Over the two together its middle would read 1.
        var cube = Cube();
        var square = new[] { new Vector3(3f, 5f, 0f), new Vector3(3.2f, 5f, 0f), new Vector3(3.2f, 5.2f, 0f), new Vector3(3f, 5.2f, 0f) };
        var squareUvs = new[] { new Vector2(10, 700), new Vector2(310, 700), new Vector2(310, 1000), new Vector2(10, 1000) }
            .Select(uv => uv / Size);
        int first = cube.Positions.Length;
        var mesh = new MeshData(
            [.. cube.Positions, .. square],
            [.. cube.Uvs, .. squareUvs],
            [.. cube.Indices, first, first + 1, first + 2, first, first + 2, first + 3],
            [.. Enumerable.Repeat(0, cube.Indices.Length / 3), 1, 1]);

        var image = MaskBaker.Bake(mesh, Size);

        int middle = ((700 + 150) * Size + 10 + 150) * 4;
        Assert.InRange(image.Rgba[middle + 2] / 255f, 0.49f, 0.52f);
        // The cube keeps its own: 0.1517 m up its side.
        Assert.InRange(Pixel(image, Face(Vector3.UnitX), Island / 2, 45).Z, 0.49f, 0.52f);
    }

    [Fact]
    public void Pixels_no_triangle_covers_read_as_clean_and_the_island_bleeds_into_them()
    {
        var image = MaskBaker.Bake(Cube(), Size);

        // The image's corner is far from any island, and reads as flat, open and high, which neither wears nor dirties.
        Assert.Equal((128, 255, 255), (image.Rgba[0], image.Rgba[1], image.Rgba[2]));
        // Two pixels outside the top face's left edge takes the island's colour, for filtering: the edge's curvature,
        // where the background is flat.
        var bled = Pixel(image, Face(Vector3.UnitY), -2, Island / 2);
        Assert.True(bled.X > 0.8f, $"red {bled.X}");
        Assert.True(bled.Y > 0.5f, $"green {bled.Y}");
    }

    [Fact]
    public void A_cube_with_islands_that_do_not_overlap_has_no_problem()
    {
        Assert.Null(MaskBaker.Problem(Cube(), Size));
    }

    [Fact]
    public void Two_triangles_on_the_same_texels_overlap()
    {
        var mesh = new MeshData(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new(0f, 0f, 1f), new(1f, 0f, 1f), new(0f, 1f, 1f)],
            [new(0.1f, 0.1f), new(0.5f, 0.1f), new(0.1f, 0.5f), new(0.2f, 0.2f), new(0.6f, 0.2f), new(0.2f, 0.6f)],
            [0, 1, 2, 3, 4, 5]);

        string? problem = MaskBaker.Problem(mesh, Size);

        Assert.NotNull(problem);
        Assert.Contains("overlap", problem);
    }

    [Fact]
    public void A_uv_outside_zero_to_one_is_a_problem_written_with_invariant_numbers()
    {
        var mesh = new MeshData([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [new(0.5f, 0.5f), new(1.5f, 0.5f), new(0.5f, 0.9f)], [0, 1, 2]);
        var before = CultureInfo.CurrentCulture;
        string? problem;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            problem = MaskBaker.Problem(mesh, Size);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        Assert.NotNull(problem);
        Assert.Contains("outside 0 to 1, to (1.5, 0.5)", problem);
    }

    private static MeshData OpenBox(Vector3 min, Vector3 max)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int first = positions.Count;
            positions.AddRange([a, b, c, d]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        Quad(new(min.X, min.Y, min.Z), new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, min.Y, min.Z));
        Quad(new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(max.X, max.Y, min.Z));
        Quad(new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), new(min.X, min.Y, max.Z));
        Quad(new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z));
        Quad(new(min.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z), new(max.X, min.Y, max.Z));
        return new MeshData(positions.ToArray(), new Vector2[positions.Count], indices.ToArray());
    }

    // The cooker side: a material with bakeMasks reads its mesh's second UV set.

    private string Folder(string rule, float[]? uvs2, string[]? nodes = null)
    {
        string src = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(src);
        Directory.CreateDirectory(Path.Combine(src, "models"));
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", nodes ?? ["a"], uvs2: uvs2);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), $"{{ \"compressTextures\": false, \"materials\": [ {rule} ] }}");
        return src;
    }

    private static (CookResult Result, MemoryStore Store) Cook(string src)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions()).Cook([new FolderRoot(src)], store);
        return (result, store);
    }

    private static string ErrorOf(CookResult result)
    {
        Assert.False(result.Succeeded);
        return Assert.Single(result.Problems, p => p.IsError).Message;
    }

    private const string WearAndDirt = """
        { "match": "bark", "bakeMasks": true,
          "wear": { "color": [1, 0.5, 0], "roughness": 0.4, "amount": 0.6 },
          "dirt": { "color": [0, 0, 0.0404], "amount": 0.7 } }
        """;

    [Fact]
    public void A_material_with_bakeMasks_gets_a_mask_texture_and_its_wear_and_dirt()
    {
        string src = Folder(WearAndDirt, uvs2: [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f]);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = ModelData.Read(new MemoryStream(ModelCookers.StandAlone(store, store.Manifest.Assets["models/a.gltf"].Hash, "models/a.gltf")), "models/a.gltf");
        var mask = model.MaskOf(Assert.Single(model.Parts).Material);
        Assert.NotNull(mask);
        Assert.Equal(MaskBaker.DefaultSize, model.Textures[mask.Texture].Width);
        Assert.Equal(TextureKind.Linear, model.Textures[mask.Texture].Kind);
        // The colours are sRGB and come out linear: 0.5 is (0.555 / 1.055) ^ 2.4 = 0.21404, and below 0.04045 the
        // curve is a straight line, so 0.0404 is 0.0404 / 12.92 = 0.003127.
        Assert.True(Vector3.Distance(new Vector3(1f, 0.21404f, 0f), mask.Wear.Color) < 1e-4f, $"wear {mask.Wear.Color}");
        Assert.Equal((0.4f, 0.6f), (mask.Wear.Roughness, mask.Wear.Amount));
        Assert.True(Vector3.Distance(new Vector3(0f, 0f, 0.003127f), mask.Dirt.Color) < 1e-5f, $"dirt {mask.Dirt.Color}");
        // Without a roughness of its own, dirt keeps the surface's.
        Assert.Equal((-1f, 0.7f), (mask.Dirt.Roughness, mask.Dirt.Amount));
        // Each corner keeps the file's own second UVs, as it keeps the first, in the same top-left convention the
        // mask is baked in. Assimp turns v over and back, which costs a little precision.
        Vector2 Uv2At(float x, float y) =>
            Assert.Single(model.Vertices, v => MathF.Abs(v.Position.X - x) < 1e-4f && MathF.Abs(v.Position.Y - y) < 1e-4f).Uv2;
        // The triangle is centred on x, so its corners (0, 0), (1, 0) and (0, 1) stand at x - 0.5.
        Assert.True(Vector2.Distance(new Vector2(0.1f, 0.1f), Uv2At(-0.5f, 0f)) < 1e-6f);
        Assert.True(Vector2.Distance(new Vector2(0.9f, 0.1f), Uv2At(0.5f, 0f)) < 1e-6f);
        Assert.True(Vector2.Distance(new Vector2(0.1f, 0.9f), Uv2At(-0.5f, 1f)) < 1e-6f);
    }

    [Fact]
    public void Overlapping_second_uvs_are_replaced_before_the_masks_bake()
    {
        // Two triangles share the same second UVs. Their first corner, (0.1, 0.1), is 51.2 texels into the 512-texel
        // page each way, so texel (51, 51) is the first both touch.
        string src = Folder(WearAndDirt, uvs2: [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f], nodes: ["a", "b"]);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Empty(result.Problems);
        Assert.Equal(
            ["made a second UV set for lighting: Triangles 0 and 1 overlap at texel (51, 51)."],
            result.Notes.Select(note => note.Message));
        var model = ModelData.Read(new MemoryStream(ModelCookers.StandAlone(store, store.Manifest.Assets["models/a.gltf"].Hash, "models/a.gltf")), "models/a.gltf");
        Assert.NotNull(model.MaskOf(model.Parts[0].Material));
    }

    [Fact]
    public void BakeMasks_on_a_transparent_material_warns_and_bakes_nothing()
    {
        string src = Folder("""
            { "match": "bark", "transparent": true, "bakeMasks": true, "dirt": { "color": [0, 0, 0], "amount": 0.5 } }
            """, uvs2: [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f]);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var warning = Assert.Single(result.Problems);
        Assert.False(warning.IsError);
        Assert.Contains("material bark: transparent materials don't show wear and dirt, so bakeMasks is ignored.", warning.Message);
        var model = ModelData.Read(new MemoryStream(ModelCookers.StandAlone(store, store.Manifest.Assets["models/a.gltf"].Hash, "models/a.gltf")), "models/a.gltf");
        Assert.Null(model.MaskOf(Assert.Single(model.Parts).Material));
    }

    [Fact]
    public void Wear_without_bakeMasks_fails_the_cook_because_there_would_be_nothing_to_wear_by()
    {
        string src = Folder("""{ "match": "bark", "wear": { "color": [1, 1, 1], "amount": 0.5 } }""", uvs2: [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f]);

        string error = ErrorOf(Cook(src).Result);

        Assert.Contains("material bark", error);
        Assert.Contains("wear and dirt need bakeMasks", error);
    }

    [Fact]
    public void A_layer_amount_above_one_fails_the_cook()
    {
        string src = Folder("""
            { "match": "bark", "bakeMasks": true, "dirt": { "color": [0, 0, 0], "amount": 1.5 } }
            """, uvs2: [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f]);

        string error = ErrorOf(Cook(src).Result);

        Assert.Contains("material bark", error);
        Assert.Contains("dirt amount must be from 0 to 1", error);
    }

    [Fact]
    public void A_layer_color_above_one_fails_the_cook_because_it_is_srgb()
    {
        string src = Folder("""
            { "match": "bark", "bakeMasks": true, "wear": { "color": [0.5, 2, 0.5], "amount": 0.5 } }
            """, uvs2: [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f]);

        string error = ErrorOf(Cook(src).Result);

        Assert.Contains("material bark: wear color is a colour as a picker shows it, each number from 0 to 1, not 2.", error);
    }
}
