using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

public sealed class ModelCookerTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    private string Temp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static (CookResult Result, MemoryStore Store) Cook(ISourceRoot root, string? cache = null)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: cache)).Cook([root], store);
        return (result, store);
    }

    private static ModelData Model(MemoryStore store, string name)
    {
        var entry = store.Manifest.Assets[name];
        Assert.Equal(ContentKind.Model, entry.Kind);
        return ModelData.Read(new MemoryStream(store.Get(entry.Hash)), name);
    }

    private static float Width(ModelData model)
    {
        return model.Vertices.Max(v => v.Position.X) - model.Vertices.Min(v => v.Position.X);
    }

    private static byte[] Png()
    {
        string path = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            PngFile.Write(path, [255, 0, 0, 255, 0, 0, 255, 128], 2, 1);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Gltf_cooks_to_rmodel_that_reads_back()
    {
        string src = Temp();
        TinyGltf.Write(src, "pair.gltf", "bark", ["left", "right"]);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "pair.gltf");
        Assert.Equal(6, model.Vertices.Length);
        Assert.Equal(6, model.Indices.Length);
    }

    [Fact]
    public void Parts_cook_each_node_by_name()
    {
        string src = Temp();
        TinyGltf.Write(src, "firs.gltf", "bark", ["fir_tall", "fir_young"]);
        File.WriteAllText(Path.Combine(src, "firs.gltf.cook.json"),
            "{ \"parts\": { \"tall\": { \"node\": \"fir_tall\" }, \"young\": { \"node\": \"fir_young\" } } }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(["firs.gltf#tall", "firs.gltf#young"], store.Manifest.Assets.Keys.Order(StringComparer.Ordinal));
        var tall = Model(store, "firs.gltf#tall");
        var young = Model(store, "firs.gltf#young");
        Assert.Equal(3, tall.Vertices.Length);
        Assert.Equal(3, young.Vertices.Length);

        // fir_tall is the first node, 1 m wide; fir_young the second, 2 m wide.
        Assert.Equal(1f, Width(tall), 3);
        Assert.Equal(2f, Width(young), 3);
    }

    [Fact]
    public void Part_takes_the_settings_beside_parts_unless_it_sets_its_own()
    {
        string src = Temp();
        TinyGltf.Write(src, "firs.gltf", "bark", ["fir_tall", "fir_young"]);
        File.WriteAllText(Path.Combine(src, "firs.gltf.cook.json"),
            "{ \"targetLength\": 10, \"parts\": { \"tall\": { \"node\": \"fir_tall\" }, " +
            "\"young\": { \"node\": \"fir_young\", \"targetLength\": 4 } } }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(10f, Width(Model(store, "firs.gltf#tall")), 3);
        Assert.Equal(4f, Width(Model(store, "firs.gltf#young")), 3);
    }

    [Fact]
    public void Unknown_model_setting_is_an_error()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"), "{ \"targetLenght\": 2 }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Contains("targetLenght", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Unknown_setting_inside_a_part_is_an_error()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"), "{ \"parts\": { \"one\": { \"nod\": \"a\" } } }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Contains("nod", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Node_that_matches_nothing_is_an_error_naming_it()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"), "{ \"node\": \"oak\" }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Equal("No node with meshes has \"oak\" in its name.", Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Ior_rule_reaches_the_cooked_material()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bottle", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"bottle\", \"opacity\": 0.1, \"ior\": 1.45 } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "a.gltf");
        Assert.Equal(1.45f, model.Materials[Assert.Single(model.Parts).Material].Ior);
    }

    [Fact]
    public void Subsurface_rule_reaches_the_cooked_material()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "skin", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"skin\", \"subsurface\": { \"radius\": 0.01, \"color\": [1, 0.4, 0.25], \"transmission\": 0.3 } } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "a.gltf");
        var subsurface = model.Materials[Assert.Single(model.Parts).Material].Subsurface;
        Assert.Equal(0.01f, subsurface.Radius);
        Assert.Equal(new System.Numerics.Vector3(1f, 0.4f, 0.25f), subsurface.Color);
        Assert.Equal(0.3f, subsurface.Transmission);
    }

    [Fact]
    public void Subsurface_with_clearcoat_is_an_error_naming_the_material()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "skin", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"skin\", \"clearcoat\": 1, \"subsurface\": { \"radius\": 0.01 } } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Equal("material skin: subsurface can't be combined with clearcoat, because they share a G-buffer channel.",
            Assert.Single(result.Problems, p => p.IsError).Message);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }

    [Fact]
    public void Missing_texture_named_in_settings_is_an_error_naming_it()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"bark\", \"baseColor\": \"bark.png\" } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Equal("material bark: the texture bark.png isn't beside the model or in a folder under it.",
            Assert.Single(result.Problems, p => p.IsError).Message);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }

    [Fact]
    public void Texture_named_in_settings_is_found_in_a_folder_under_the_model()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        Directory.CreateDirectory(Path.Combine(src, "textures"));
        File.WriteAllBytes(Path.Combine(src, "textures", "bark.png"), Png());
        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"bark\", \"baseColor\": \"bark.png\" } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "a.gltf");
        var texture = Assert.Single(model.Textures);
        Assert.Equal((2, 1), (texture.Width, texture.Height));
        Assert.Equal(0, model.Materials[0].BaseColorTexture);
    }

    [Fact]
    public void Mesh_without_uvs_and_without_textures_cooks_without_warnings()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);

        var (result, _) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Mesh_without_uvs_whose_material_samples_a_texture_warns_naming_the_mesh()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["trunk"]);
        File.WriteAllBytes(Path.Combine(src, "bark.png"), Png());
        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"*.png\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"bark\", \"baseColor\": \"bark.png\" } ] }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var warning = Assert.Single(result.Problems);
        Assert.False(warning.IsError);
        Assert.Equal("mesh 0 (trunk) has no UVs; using a flat tangent frame", warning.Message);
    }

    [Fact]
    public void Changing_the_gltf_buffer_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"], bufferFile: "a.bin");

        var first = Cook(new FolderRoot(src), cache);
        var second = Cook(new FolderRoot(src), cache);
        string bin = Path.Combine(src, "a.bin");
        byte[] buffer = File.ReadAllBytes(bin);
        BitConverter.GetBytes(3f).CopyTo(buffer, 28); // The third corner's y, from 1 to 3.
        File.WriteAllBytes(bin, buffer);
        var third = Cook(new FolderRoot(src), cache);

        Assert.True(first.Result.Succeeded, string.Join("; ", first.Result.Problems));
        Assert.Equal((1, 0), (first.Result.Cooked, first.Result.FromCache));
        Assert.Equal((0, 1), (second.Result.Cooked, second.Result.FromCache));
        Assert.Equal((1, 0), (third.Result.Cooked, third.Result.FromCache));
        Assert.NotEqual(first.Store.Manifest.Assets["a.gltf"].Hash, third.Store.Manifest.Assets["a.gltf"].Hash);
    }

    // A bare name is found by searching; a name with a folder is read by its path.
    [Theory]
    [InlineData("bark.png")]
    [InlineData("textures/bark.png")]
    public void Changing_a_texture_named_in_settings_cooks_again(string named)
    {
        string src = Temp();
        string cache = Temp();
        TinyGltf.Write(src, "a.gltf", "bark", ["a"]);
        Directory.CreateDirectory(Path.Combine(src, "textures"));
        File.WriteAllText(Path.Combine(src, "cook.json"), "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            $"{{ \"materials\": [ {{ \"match\": \"bark\", \"baseColor\": \"{named}\" }} ] }}");
        File.WriteAllBytes(Path.Combine(src, "textures", "bark.png"), Png());

        var first = Cook(new FolderRoot(src), cache);
        var second = Cook(new FolderRoot(src), cache);
        string path = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N") + ".png");
        PngFile.Write(path, [0, 255, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 9, 9, 9, 255], 4, 1);
        File.Move(path, Path.Combine(src, "textures", "bark.png"), overwrite: true);
        var third = Cook(new FolderRoot(src), cache);

        Assert.True(first.Result.Succeeded, string.Join("; ", first.Result.Problems));
        Assert.Equal((1, 0), (first.Result.Cooked, first.Result.FromCache));
        Assert.Equal((0, 1), (second.Result.Cooked, second.Result.FromCache));
        Assert.Equal((1, 0), (third.Result.Cooked, third.Result.FromCache));
        Assert.Equal(4, Assert.Single(Model(third.Store, "a.gltf").Textures).Width);
    }

    [Fact]
    public void Changing_an_obj_material_library_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.obj"),
            "mtllib a.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl red\nf 1 2 3\n");
        File.WriteAllText(Path.Combine(src, "cook.json"), "{ \"rules\": [ { \"match\": \"*.mtl\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.mtl"), "newmtl red\nKd 1 0 0\n");

        var first = Cook(new FolderRoot(src), cache);
        var second = Cook(new FolderRoot(src), cache);
        File.WriteAllText(Path.Combine(src, "a.mtl"), "newmtl red\nKd 0 0 1\n");
        var third = Cook(new FolderRoot(src), cache);

        Assert.True(first.Result.Succeeded, string.Join("; ", first.Result.Problems));
        Assert.Equal((1, 0), (first.Result.Cooked, first.Result.FromCache));
        Assert.Equal((0, 1), (second.Result.Cooked, second.Result.FromCache));
        Assert.Equal((1, 0), (third.Result.Cooked, third.Result.FromCache));
    }

    [Fact]
    public void Memory_source_model_is_an_error()
    {
        var root = new MemoryRoot { ["a.gltf"] = "{}" };

        var (result, _) = Cook(root);

        Assert.False(result.Succeeded);
        Assert.Equal("a.gltf: models can only be cooked from files on disk",
            Assert.Single(result.Problems, p => p.IsError).Message);
    }

    [Fact]
    public void Model_cookers_are_the_built_in_ones_and_the_model_cooker()
    {
        Assert.Equal(
            BuiltInCookers.All.Select(c => c.Id).Append("model").Order(StringComparer.Ordinal),
            ModelCookers.All.Select(c => c.Id).Order(StringComparer.Ordinal));
    }
}
