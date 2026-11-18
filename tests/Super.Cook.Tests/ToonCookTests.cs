using System.Numerics;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>A material rule's <c>toon</c> and <c>outline</c>, and the section of the model file toon settings live in.</summary>
public sealed class ToonCookTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_toon_rule_cooks_the_steps_the_rim_and_the_ramp()
    {
        string src = Folder("{ \"match\": \"bark\", \"toon\": { \"steps\": 2, \"ramp\": \"ramp.png\", \"rim\": [0.1, 0.2, 0.3] } }");

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        var toon = Assert.IsType<ModelMaterialToon>(model.ToonOf(0));
        Assert.Equal(2, toon.Steps);
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), toon.Rim);
        Assert.Equal(TextureKind.Linear, model.Textures[toon.Ramp].Kind);
        Assert.Equal([10, 120, 250, 255], model.Textures[toon.Ramp].Mips[0].Where((_, i) => i % 4 == 0));
    }

    [Fact]
    public void Toon_written_true_has_three_steps_no_ramp_and_no_rim()
    {
        string src = Folder("{ \"match\": \"bark\", \"toon\": true }");

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(new ModelMaterialToon(3, -1, Vector3.Zero), Model(store).ToonOf(0));
    }

    [Fact]
    public void An_outline_rule_sets_the_materials_flag_and_a_model_without_rules_has_neither()
    {
        string outlined = Folder("{ \"match\": \"bark\", \"outline\": true }");
        string plain = Folder(null);

        var (outlinedResult, outlinedStore) = Cook(outlined);
        var (plainResult, plainStore) = Cook(plain);

        Assert.True(outlinedResult.Succeeded, string.Join("; ", outlinedResult.Problems));
        Assert.True(plainResult.Succeeded, string.Join("; ", plainResult.Problems));
        Assert.Equal(MaterialFlags.Outline, Model(outlinedStore).Materials[0].Flags & MaterialFlags.Outline);
        Assert.Empty(Model(outlinedStore).MaterialToon);
        Assert.Equal(MaterialFlags.None, Model(plainStore).Materials[0].Flags & MaterialFlags.Outline);
    }

    [Theory]
    [InlineData("{ \"steps\": 0 }", "material bark: toon steps 0 must be from 1 to 16.")]
    [InlineData("{ \"rim\": [1, 2] }", "material bark: toon rim must be three numbers, red, green and blue, not 2.")]
    [InlineData("{ \"rim\": [0, -1, 0] }", "material bark: toon rim <0, -1, 0> must be 0 or more in each channel.")]
    [InlineData("{ \"ramp\": \"gone.png\" }", "material bark: the texture gone.png isn't beside the model or in a folder under it.")]
    public void Bad_toon_settings_stop_the_cook(string toon, string message)
    {
        string src = Folder($"{{ \"match\": \"bark\", \"toon\": {toon} }}");

        var (result, _) = Cook(src);

        Assert.False(result.Succeeded);
        Assert.Equal(message, Assert.Single(result.Problems, problem => problem.IsError).Message);
    }

    [Fact]
    public void Toon_settings_come_back_from_the_file_and_only_add_to_its_end()
    {
        ModelData Make(ModelMaterialToon?[] toon) => new()
        {
            Vertices = [],
            Indices = [],
            Parts = [],
            Materials = [new ModelMaterial { BaseColorTexture = -1, NormalTexture = -1, SurfaceTexture = -1, EmissiveTexture = -1 }, default],
            Textures = [TextureData.FromPixels(1, 1, TextureKind.Linear, [255, 255, 255, 255])],
            MaterialToon = toon,
        };
        byte[] without = Make([]).ToBytes();
        byte[] with = Make([null, new ModelMaterialToon(4, 0, new Vector3(0.5f, 0.25f, 0f))]).ToBytes();

        var read = ModelFile.Read(new MemoryStream(with), "a.model");

        Assert.Null(read.ToonOf(0));
        Assert.Equal(new ModelMaterialToon(4, 0, new Vector3(0.5f, 0.25f, 0f)), read.ToonOf(1));
        Assert.Equal(without, with[..without.Length]);
        Assert.Empty(ModelFile.Read(new MemoryStream(without), "a.model").MaterialToon);
    }

    private string Folder(string? rule)
    {
        string src = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(src);
        Directory.CreateDirectory(src);
        TinyGltf.WriteQuads(src, "quads.gltf", "bark", uvs2: null);
        string path = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N") + ".png");
        PngFile.Write(path, [10, 0, 0, 255, 120, 0, 0, 255, 250, 0, 0, 255, 255, 0, 0, 255], 4, 1);
        File.Move(path, Path.Combine(src, "ramp.png"));
        string rules = rule is null ? "" : $", \"materials\": [ {rule} ]";
        File.WriteAllText(Path.Combine(src, "quads.gltf.cook.json"), $"{{ \"compressTextures\": false{rules} }}");
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
        var entry = store.Manifest.Assets["quads.gltf"];
        return ModelFile.Read(new MemoryStream(store.Get(entry.Hash)), "quads.gltf",
            hash => TextureData.Read(new MemoryStream(store.Get(hash)), hash));
    }
}
