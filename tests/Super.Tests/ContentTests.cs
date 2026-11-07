using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class ContentTests
{
    private static readonly Vertex[] Triangle =
    [
        new(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero),
        new(new Vector3(1f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitX),
        new(new Vector3(0f, 0f, 1f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitY),
    ];

    private static readonly uint[] Indices = [0, 1, 2];

    private static GeneratedMesh Mesh(Surface surface) => new(Triangle, Indices, surface);

    [Fact]
    public void Mesh_with_unnamed_texture_throws_naming_the_field()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ArgumentException>(() =>
            content.Mesh(Mesh(new Surface { ColorTexture = new TextureRef(7) })));

        Assert.Contains("ColorTexture", problem.Message);
    }

    [Fact]
    public void Mesh_with_a_shader_texture_that_is_really_a_shader_throws_with_the_index()
    {
        var content = new Content(new MemoryContent());
        var shader = content.Shader("terrain");

        var problem = Assert.Throws<ArgumentException>(() =>
            content.Mesh(Mesh(new Surface { ShaderTextures = [new TextureRef(shader.Index)] })));

        Assert.Contains("ShaderTextures[0]", problem.Message);
    }

    [Fact]
    public void Texture_array_with_an_unnamed_layer_throws_with_the_index()
    {
        var content = new Content(new MemoryContent());
        var first = content.Texture(new GeneratedTexture(1, [255, 255, 255, 255]));

        var problem = Assert.Throws<ArgumentException>(() => content.TextureArray(first, new TextureRef(9)));

        Assert.Contains("layers[1]", problem.Message);
    }

    [Fact]
    public void Naming_the_same_file_twice_gives_the_same_ref()
    {
        var content = new Content(Textures("textures/grass.png", "textures/dirt.png"));

        Assert.Equal(content.Texture("textures/grass.png"), content.Texture("textures/grass.png"));
        Assert.NotEqual(content.Texture("textures/grass.png"), content.Texture("textures/dirt.png"));
        Assert.Equal(content.Shader("terrain"), content.Shader("terrain"));
    }

    [Fact]
    public void A_texture_name_that_is_not_cooked_stops_at_once()
    {
        var content = new Content(Textures("textures/grass.png"));

        var problem = Assert.Throws<ContentException>(() => content.Texture("textures/Grass.png"));

        Assert.Equal("textures/Grass.png isn't in the cooked content. Check the name, including its case and extension.", problem.Message);
        Assert.Empty(content.Items);
    }

    /// <summary>Content holding a small PNG under each name.</summary>
    private static MemoryContent Textures(params string[] names)
    {
        var files = new MemoryContent();
        foreach (string name in names)
        {
            files.Add(name, Ui.ImageTests.Png(2, 2));
        }

        return files;
    }

    private const string SmallSize = """{ "rules": [{ "match": "*.material.*", "material": { "size": 16 } }] }""";

    private const string Plain = """
        using Super.Materials;

        public sealed class Plain : MaterialGraph
        {
            public override Material Build() => new Material { Width = 2f };
        }
        """;

    [Fact]
    public void Surface_from_a_graph_has_its_textures_and_width()
    {
        var files = new MemoryContent();
        files["cook.json"] = SmallSize;
        files["plain.material.cs"] = Plain;
        var content = new Content(files);

        var surface = content.Surface("plain.material.cs");

        Assert.Equal(content.Texture("plain.material.cs#color"), surface.ColorTexture);
        Assert.Equal(content.Texture("plain.material.cs#normal"), surface.NormalTexture);
        Assert.Equal(content.Texture("plain.material.cs#surface"), surface.SurfaceTexture);
        Assert.Equal(2f, surface.TileWidth);
        Assert.Equal((Vector3.One, 1f, 1f), (surface.Color, surface.Roughness, surface.Metallic));
    }

    [Fact]
    public void Surface_of_a_texture_explains_what_a_material_is()
    {
        var files = Textures("a.png");
        var content = new Content(files);

        var problem = Assert.Throws<ContentException>(() => content.Surface("a.png"));

        Assert.Contains("a.png is not a cooked material. Materials are .material.cs and .material.json files.", problem.Message);
    }

    [Fact]
    public void Surface_of_a_missing_name_gives_the_usual_missing_message()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ContentException>(() => content.Surface("nope.material.cs"));

        Assert.Contains("nope.material.cs isn't in the cooked content. Check the name, including its case and extension.", problem.Message);
    }

    [Fact]
    public void IsReady_is_true_without_a_window()
    {
        var content = new Content(new MemoryContent());
        var model = content.Mesh(Mesh(new Surface()));

        Assert.True(content.IsReady(model));
        Assert.True(content.IsReady(ModelRef.Cube));
    }

    [Fact]
    public void Unloaded_model_cannot_be_drawn()
    {
        var content = new Content(new MemoryContent());
        var view = new View { Content = content };
        var model = content.Mesh(Mesh(new Surface()));
        view.Begin(0f);
        view.Model(1, model, Matrix4x4.Identity);

        content.Unload(model);

        var problem = Assert.Throws<InvalidOperationException>(() => view.Model(1, model, Matrix4x4.Identity));
        Assert.Contains(model.Index.ToString(), problem.Message);
        Assert.False(content.IsReady(model));
        Assert.Throws<ArgumentException>(() => content.Unload(model));
    }

    [Fact]
    public void Unloaded_texture_cannot_be_named_by_a_mesh_and_naming_it_again_makes_a_new_one()
    {
        var content = new Content(Textures("textures/splat.png"));
        var texture = content.Texture("textures/splat.png");

        content.Unload(texture);

        var problem = Assert.Throws<ArgumentException>(() => content.Mesh(Mesh(new Surface { NormalTexture = texture })));
        Assert.Contains("NormalTexture", problem.Message);
        Assert.NotEqual(texture, content.Texture("textures/splat.png"));
    }

    [Theory]
    [InlineData(nameof(Surface.HeightTexture))]
    [InlineData(nameof(Surface.DetailColorTexture))]
    [InlineData(nameof(Surface.DetailNormalTexture))]
    public void Mesh_naming_an_unloaded_height_or_detail_texture_says_which(string field)
    {
        var content = new Content(Textures("textures/splat.png"));
        var texture = content.Texture("textures/splat.png");
        content.Unload(texture);

        var problem = Assert.Throws<ArgumentException>(() => content.Mesh(Mesh(Naming(field, texture))));

        Assert.Equal($"{field} names {texture.Index}, which was unloaded. (Parameter '{field}')", problem.Message);
    }

    [Theory]
    [InlineData(nameof(Surface.HeightTexture))]
    [InlineData(nameof(Surface.DetailColorTexture))]
    [InlineData(nameof(Surface.DetailNormalTexture))]
    public void Height_or_detail_texture_a_live_mesh_names_cannot_be_unloaded(string field)
    {
        var content = new Content(Textures("textures/splat.png"));
        var texture = content.Texture("textures/splat.png");
        var model = content.Mesh(Mesh(Naming(field, texture)));

        var problem = Assert.Throws<InvalidOperationException>(() => content.Unload(texture));

        Assert.Equal($"Texture {texture.Index} is still named by model {model.Index}. Unload the model first.", problem.Message);
    }

    private static Surface Naming(string field, TextureRef texture) => field switch
    {
        nameof(Surface.HeightTexture) => new Surface { HeightTexture = texture, HeightDepth = 0.02f },
        nameof(Surface.DetailColorTexture) => new Surface { DetailColorTexture = texture },
        nameof(Surface.DetailNormalTexture) => new Surface { DetailNormalTexture = texture },
        _ => throw new ArgumentException($"No surface field {field}.", nameof(field)),
    };

    [Fact]
    public void Texture_a_live_mesh_names_cannot_be_unloaded_until_the_mesh_is()
    {
        var content = new Content(Textures("textures/splat.png"));
        var texture = content.Texture("textures/splat.png");
        var model = content.Mesh(Mesh(new Surface { ShaderTextures = [texture] }));

        var problem = Assert.Throws<InvalidOperationException>(() => content.Unload(texture));
        Assert.Contains(model.Index.ToString(), problem.Message);

        content.Unload(model);
        content.Unload(texture);

        Assert.True(content.IsUnloaded(texture.Index));
    }

    [Fact]
    public void Unloaded_items_let_go_of_their_data()
    {
        var content = new Content(Textures("textures/splat.png"));
        var texture = content.Texture("textures/splat.png");
        var model = content.Mesh(Mesh(new Surface { ColorTexture = texture }));

        content.Unload(model);
        content.Unload(texture);

        Assert.IsType<UnloadedItem>(content.Items[model.Index - 1]);
        Assert.IsType<UnloadedItem>(content.Items[texture.Index - 1]);
        Assert.Throws<ArgumentException>(() => content.Unload(texture));
    }

    [Fact]
    public void Texture_from_png_in_memory_decodes()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(folder, "tiny.png");
        byte[] pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            pixels[i * 4] = (byte)(i * 16);
            pixels[i * 4 + 1] = 40;
            pixels[i * 4 + 2] = 200;
            pixels[i * 4 + 3] = 255;
        }

        PngFile.Write(path, pixels, 4, 4);
        var files = new MemoryContent { ["textures/cook.json"] = "{ \"rules\": [{ \"match\": \"*.png\", \"texture\": { \"linear\": true } }] }" };
        files.Add("textures/tiny.png", File.ReadAllBytes(path));
        Directory.Delete(folder, recursive: true);
        var content = new Content(files);
        var texture = content.Texture("textures/tiny.png");

        var data = SceneRenderer.LoadTexture((TextureItem)content.Items[texture.Index - 1]);

        Assert.Equal(4, data.Width);
        Assert.Equal(4, data.Height);
        Assert.Equal(TextureKind.Linear, data.Kind);
        Assert.Equal(3, data.Mips.Length);
        Assert.Equal(pixels, data.Mips[0]);
        Assert.Equal(4, data.Mips[2].Length);
    }

    [Fact]
    public void Legacy_generated_mesh_still_works()
    {
        var content = new Content(new MemoryContent());
        var texture = new GeneratedTexture(2, new byte[16]);

        var mesh = new GeneratedMesh(Triangle, Indices, texture, 0.9f);
        var model = content.Mesh(mesh);

        Assert.Equal(0.9f, mesh.Surface.Roughness);
        Assert.Equal(Vector3.One, mesh.Surface.Color);
        Assert.Same(texture, mesh.OwnedTexture);
        Assert.True(mesh.Simplify);
        Assert.True(content.IsReady(model));
    }

    [Fact]
    public void From_pixels_builds_a_full_mip_chain_and_keeps_normals_unit_length()
    {
        byte[] pixels = new byte[2 * 2 * 4];
        for (int i = 0; i < 4; i++)
        {
            // Two normals leaning left, two leaning right: the average points straight up.
            pixels[i * 4] = (byte)(i < 2 ? 0 : 255);
            pixels[i * 4 + 1] = 128;
            pixels[i * 4 + 2] = 255;
            pixels[i * 4 + 3] = 255;
        }

        var data = TextureData.FromPixels(2, 2, TextureKind.Linear, pixels, normalMap: true);

        Assert.Equal(2, data.Mips.Length);
        Assert.Equal(4, data.Mips[1].Length);
        Assert.InRange(data.Mips[1][2], 250, 255);
    }
}
