using System.Numerics;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class ModelShaderRuleTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    // The model in models/, a texture in textures/ and graphs in materials/, as a game lays them out. Textures stay
    // uncompressed, so tests can read their pixels.
    private string Folder(string rule, string? graph = null)
    {
        string src = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(src);
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        Directory.CreateDirectory(Path.Combine(src, "textures"));
        File.WriteAllText(Path.Combine(src, "cook.json"), "{ \"rules\": [ { \"match\": \"**/*.material.*\", \"cook\": false } ] }");
        PngFile.Write(Path.Combine(src, "textures", "ripples.png"), [255, 0, 0, 255, 0, 0, 255, 128], 2, 1);
        if (graph is not null)
        {
            File.WriteAllText(Path.Combine(src, "materials", "cutout.material.cs"), graph);
        }

        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), $"{{ \"compressTextures\": false, \"materials\": [ {rule} ] }}");
        return src;
    }

    private static (CookResult Result, MemoryStore Store) Cook(string src)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions()).Cook([new FolderRoot(src)], store);
        return (result, store);
    }

    private static ModelData Model(MemoryStore store)
    {
        var entry = store.Manifest.Assets["models/a.gltf"];
        return ModelData.Read(new MemoryStream(store.Get(entry.Hash)), "models/a.gltf");
    }

    private static string ErrorOf(CookResult result)
    {
        Assert.False(result.Succeeded);
        return Assert.Single(result.Problems, p => p.IsError).Message;
    }

    [Fact]
    public void A_shader_rule_names_the_shader_in_the_cooked_material()
    {
        string src = Folder("""
            { "match": "bark", "shader": "puddles",
              "shaderParameters": [[1, 2, 3, 4], [0.5]], "shaderTextures": ["../textures/ripples.png"] }
            """);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        var shader = model.ShaderOf(Assert.Single(model.Parts).Material);
        Assert.NotNull(shader);
        Assert.Equal("puddles", shader.Name);
        // A parameter with fewer than four numbers is filled out with zeros.
        Assert.Equal([new Vector4(1f, 2f, 3f, 4f), new Vector4(0.5f, 0f, 0f, 0f)], shader.Parameters);
        // The texture's path from the model becomes its name in the cooked content.
        Assert.Equal(["textures/ripples.png"], shader.Textures);
    }

    [Fact]
    public void A_material_without_a_shader_rule_has_no_shader()
    {
        string src = Folder("""{ "match": "bark", "roughness": 0.5 }""");

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        Assert.Null(model.ShaderOf(Assert.Single(model.Parts).Material));
    }

    [Fact]
    public void More_than_eight_parameters_is_refused()
    {
        string src = Folder("""
            { "match": "bark", "shader": "puddles", "shaderParameters": [[0], [1], [2], [3], [4], [5], [6], [7], [8]] }
            """);

        var (result, _) = Cook(src);

        Assert.Equal("material bark: shaderParameters has 9 entries, but a surface shader takes at most 8.", ErrorOf(result));
    }

    [Fact]
    public void A_fifth_parameter_component_is_refused()
    {
        string src = Folder("""{ "match": "bark", "shader": "puddles", "shaderParameters": [[1, 2, 3, 4, 5]] }""");

        var (result, _) = Cook(src);

        Assert.Equal("material bark: shaderParameters[0] has 5 numbers, but a parameter holds at most 4.", ErrorOf(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../puddles")]
    [InlineData("water/puddles")]
    [InlineData("water\\puddles")]
    [InlineData("..")]
    public void A_shader_name_that_isnt_a_plain_file_name_is_refused(string name)
    {
        string json = name.Replace("\\", "\\\\");
        string src = Folder($$"""{ "match": "bark", "shader": "{{json}}" }""");

        var (result, _) = Cook(src);

        Assert.Equal(
            $"material bark: shader \"{name}\" must be the file name of a shader in the shaders folder, such as \"puddles\", with no /, \\ or \"..\".",
            ErrorOf(result));
    }

    [Fact]
    public void More_than_four_shader_textures_is_refused()
    {
        string src = Folder("""
            { "match": "bark", "shader": "puddles", "shaderTextures": [
              "../textures/ripples.png", "../textures/ripples.png", "../textures/ripples.png",
              "../textures/ripples.png", "../textures/ripples.png"] }
            """);

        var (result, _) = Cook(src);

        Assert.Equal("material bark: shaderTextures has 5 entries, but a surface shader takes at most 4.", ErrorOf(result));
    }

    [Fact]
    public void A_shader_texture_that_is_not_in_the_source_root_is_refused()
    {
        string src = Folder("""{ "match": "bark", "shader": "puddles", "shaderTextures": ["../textures/nope.png"] }""");

        var (result, _) = Cook(src);

        Assert.Equal("material bark: shaderTextures[0] ../textures/nope.png isn't in the source root.", ErrorOf(result));
    }

    [Fact]
    public void Shader_parameters_without_a_shader_are_refused()
    {
        string src = Folder("""{ "match": "bark", "shaderParameters": [[1]] }""");

        var (result, _) = Cook(src);

        Assert.Equal("material bark: shaderParameters and shaderTextures are read by a shader, so they need one.", ErrorOf(result));
    }

    [Fact]
    public void A_transparent_material_with_a_shader_is_refused()
    {
        string src = Folder("""{ "match": "bark", "shader": "puddles", "transparent": true }""");

        var (result, _) = Cook(src);

        Assert.Equal("material bark: transparent materials can't use a surface shader yet.", ErrorOf(result));
    }

    // Opacity is a quarter on the left half and whole on the right, so a cutoff of a half has something to cut and
    // something to keep.
    private const string CutoutGraph = """
        using Super.Materials;

        public sealed class Cutout : MaterialGraph
        {
            public override Material Build() => new Material
            {
                Width = 2f,
                Color = Rgb.Of(0.2f, 0.4f, 0.6f),
                Roughness = Gray.Of(0.4f),
                Opacity = Pixel.Gray(p => p.Uv.X < 0.5f ? 0.25f : 1f),
            };
        }
        """;

    [Fact]
    public void A_graph_with_alpha_cutoff_writes_its_opacity_into_the_colour_alpha()
    {
        string src = Folder("""
            { "match": "bark", "graph": "../materials/cutout.material.cs", "graphSize": 16, "alphaCutoff": 0.5 }
            """, CutoutGraph);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        var material = model.Materials[Assert.Single(model.Parts).Material];
        byte[] color = model.Textures[material.BaseColorTexture].Mips[0];
        byte[] surface = model.Textures[material.SurfaceTexture].Mips[0];
        for (int pixel = 0; pixel < 16 * 16; pixel++)
        {
            int x = pixel % 16;

            // 0.25 of 255 is 63.75, rounded to 64.
            Assert.Equal(x < 8 ? 64 : 255, color[pixel * 4 + 3]);

            // The opacity moved, so the surface's alpha is whole and it isn't counted twice.
            Assert.Equal(255, surface[pixel * 4 + 3]);
        }

        // The colour itself is the graph's.
        Assert.Equal([51, 102, 153], color[0..3]);
    }

    [Fact]
    public void A_graph_without_alpha_cutoff_keeps_its_opacity_in_the_surface_alpha()
    {
        string src = Folder("""{ "match": "bark", "graph": "../materials/cutout.material.cs", "graphSize": 16 }""", CutoutGraph);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        var material = model.Materials[Assert.Single(model.Parts).Material];
        byte[] color = model.Textures[material.BaseColorTexture].Mips[0];
        byte[] surface = model.Textures[material.SurfaceTexture].Mips[0];
        Assert.Equal(255, color[3]);
        Assert.Equal(64, surface[3]);
    }
}
