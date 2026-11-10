using System.Numerics;
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
        return ModelData.Read(new MemoryStream(store.Get(entry.Hash)), name, hash =>
        {
            // Every texture a model points to must be listed, or committing a folder would prune it.
            Assert.Contains(hash, store.Manifest.Objects);
            return TextureData.Read(new MemoryStream(store.Get(hash)), hash);
        });
    }

    private static float Width(ModelData model)
    {
        return model.Vertices.Max(v => v.Position.X) - model.Vertices.Min(v => v.Position.X);
    }

    private static byte[] Png() => Png([255, 0, 0, 255, 0, 0, 255, 128]);

    /// <summary>A 2x1 PNG of these RGBA bytes.</summary>
    private static byte[] Png(byte[] pixels)
    {
        string path = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            PngFile.Write(path, pixels, 2, 1);
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

    [Theory]
    [InlineData("true", 0.6f)]
    [InlineData("{ \"translucency\": 0.85 }", 0.85f)]
    [InlineData("{ \"translucency\": 0 }", 0f)]
    public void A_foliage_rule_marks_the_material_as_leaves_with_its_translucency(string rule, float translucency)
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "leaf", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"leaf\", \"foliage\": " + rule + " } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var material = Model(store, "a.gltf").Materials[0];
        Assert.Equal(MaterialFlags.Foliage, material.Flags & MaterialFlags.Foliage);
        Assert.Equal(translucency, material.Translucency);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    public void Foliage_false_leaves_the_material_solid(string rule)
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "leaf", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"leaf\", \"foliage\": " + rule + " } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var material = Model(store, "a.gltf").Materials[0];
        Assert.Equal(MaterialFlags.None, material.Flags & MaterialFlags.Foliage);
        Assert.Equal(0f, material.Translucency);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("-0.1")]
    public void Translucency_outside_0_to_1_is_refused(string translucency)
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "leaf", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"leaf\", \"foliage\": { \"translucency\": " + translucency + " } } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Equal($"material leaf: foliage translucency {translucency} must be from 0 to 1.",
            Assert.Single(result.Problems, p => p.IsError).Message);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }

    [Theory]
    [InlineData(", \"wind\": true", 1f)]
    [InlineData(", \"wind\": { \"sway\": 0.08 }", 0.08f)]
    [InlineData(", \"wind\": false", 0f)]
    [InlineData("", 0f)]
    public void A_wind_rule_sets_how_far_the_material_sways(string rule, float sway)
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "grass", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"grass\"" + rule + " } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(sway, Model(store, "a.gltf").Materials[0].Wind);
    }

    [Fact]
    public void A_negative_wind_sway_is_refused()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "grass", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"grass\", \"wind\": { \"sway\": -0.5 } } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Equal("material grass: wind sway -0.5 must be 0 or more.", Assert.Single(result.Problems, p => p.IsError).Message);
        Assert.Same(ContentManifest.Empty, store.Manifest);
    }

    [Fact]
    public void A_foliage_rule_that_is_a_number_is_refused_with_what_it_must_be()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "leaf", ["a"]);
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"materials\": [ { \"match\": \"leaf\", \"foliage\": 0.6 } ] }");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.False(result.Succeeded);
        Assert.Contains("must be true, false or an object, not Number.", Assert.Single(result.Problems, p => p.IsError).Message);
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
    public void Height_and_detail_textures_named_in_settings_reach_the_material()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "wall", ["a"]);
        foreach (string name in new[] { "height.png", "grit.png", "grit_normal.png" })
        {
            File.WriteAllBytes(Path.Combine(src, name), Png());
        }

        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"), """
            { "materials": [ { "match": "wall", "height": "height.png", "heightDepth": 0.03,
              "detailColor": "grit.png", "detailNormal": "grit_normal.png", "detailScale": 6, "detailMask": true } ] }
            """);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "a.gltf");
        var material = model.Materials[0];
        Assert.Equal(3, model.Textures.Length);
        Assert.Equal([0, 1, 2], new[] { material.HeightTexture, material.DetailColorTexture, material.DetailNormalTexture }.Order());
        Assert.Equal(-1, material.BaseColorTexture);
        Assert.Equal(0.03f, material.HeightDepth);
        Assert.Equal(6f, material.DetailScale);
        Assert.Equal(MaterialFlags.DetailMask, material.Flags);
        Assert.All(model.Textures, texture => Assert.Equal(TextureKind.Linear, texture.Kind));
    }

    [Fact]
    public void One_image_used_as_colour_by_one_model_and_as_height_by_another_cooks_both_ways()
    {
        // Models cooked together share decoded images, so this checks the sharing keeps the two apart.
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "wall", ["a"]);
        TinyGltf.Write(src, "b.gltf", "wall", ["b"]);
        File.WriteAllBytes(Path.Combine(src, "shared.png"), Png([200, 255, 0, 255, 100, 255, 0, 128]));
        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"compressTextures\": false, \"materials\": [ { \"match\": \"wall\", \"baseColor\": \"shared.png\" } ] }");
        File.WriteAllText(Path.Combine(src, "b.gltf.cook.json"),
            "{ \"compressTextures\": false, \"materials\": [ { \"match\": \"wall\", \"height\": \"shared.png\" } ] }");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var colour = Assert.Single(Model(store, "a.gltf").Textures);
        var height = Assert.Single(Model(store, "b.gltf").Textures);

        // Red is 200 beside 100. Colour averages in linear light: 0.5776 and 0.1274 average to 0.3525,
        // which is 160 in sRGB. Height averages the bytes: 150. Green and blue are 255 and 0 on both
        // sides, which every average keeps. Alpha averages the bytes either way: 191.5 rounds to 192.
        Assert.Equal((TextureKind.Color, TextureKind.Linear), (colour.Kind, height.Kind));
        Assert.Equal(new byte[] { 160, 255, 0, 192 }, colour.Mips[1]);
        Assert.Equal(new byte[] { 150, 255, 0, 192 }, height.Mips[1]);
    }

    [Fact]
    public void Two_models_using_one_image_the_same_way_share_one_texture_object()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "wall", ["a"]);
        TinyGltf.Write(src, "b.gltf", "wall", ["b"]);
        File.WriteAllBytes(Path.Combine(src, "shared.png"), Png([200, 255, 0, 255, 100, 255, 0, 128]));
        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        foreach (string model in new[] { "a.gltf", "b.gltf" })
        {
            File.WriteAllText(Path.Combine(src, model + ".cook.json"),
                "{ \"compressTextures\": false, \"materials\": [ { \"match\": \"wall\", \"baseColor\": \"shared.png\" } ] }");
        }

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        string hash = Assert.Single(store.Manifest.Objects);
        var texture = TextureData.Read(new MemoryStream(store.Get(hash)), hash);
        Assert.Equal(new byte[] { 200, 255, 0, 255, 100, 255, 0, 128 }, texture.Mips[0]);

        // Both models name the one object rather than holding a copy.
        Assert.All(new[] { "a.gltf", "b.gltf" }, model =>
            Assert.Contains(hash, System.Text.Encoding.UTF8.GetString(store.Get(store.Manifest.Assets[model].Hash))));
        Assert.Equal(texture.Mips[0], Assert.Single(Model(store, "b.gltf").Textures).Mips[0]);
    }

    [Fact]
    public void Two_models_using_one_image_with_compression_on_share_one_BC7_texture_object()
    {
        // Compression is on by default, so this is the sharing most games get.
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "wall", ["a"]);
        TinyGltf.Write(src, "b.gltf", "wall", ["b"]);
        File.WriteAllBytes(Path.Combine(src, "shared.png"), Png([200, 255, 0, 255, 100, 255, 0, 128]));
        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        foreach (string model in new[] { "a.gltf", "b.gltf" })
        {
            File.WriteAllText(Path.Combine(src, model + ".cook.json"),
                "{ \"materials\": [ { \"match\": \"wall\", \"baseColor\": \"shared.png\" } ] }");
        }

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        string hash = Assert.Single(store.Manifest.Objects);
        var texture = TextureData.Read(new MemoryStream(store.Get(hash)), hash);
        Assert.Equal(TextureFormat.Bc7, texture.Format);
        // A 2 by 1 level still takes one 4 by 4 block, which BC7 keeps in 16 bytes.
        Assert.Equal(16, texture.Mips[0].Length);
        Assert.All(new[] { "a.gltf", "b.gltf" }, model =>
            Assert.Contains(hash, System.Text.Encoding.UTF8.GetString(store.Get(store.Manifest.Assets[model].Hash))));
    }

    [Fact]
    public void StandAlone_puts_the_shared_textures_back_inside_the_model()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "wall", ["a"]);
        File.WriteAllBytes(Path.Combine(src, "shared.png"), Png([200, 255, 0, 255, 100, 255, 0, 128]));
        File.WriteAllText(Path.Combine(src, "cook.json"),
            "{ \"rules\": [ { \"match\": \"**/*.png\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "a.gltf.cook.json"),
            "{ \"compressTextures\": false, \"materials\": [ { \"match\": \"wall\", \"baseColor\": \"shared.png\" } ] }");
        var (result, store) = Cook(new FolderRoot(src));
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));

        byte[] alone = ModelCookers.StandAlone(store, store.Manifest.Assets["a.gltf"].Hash, "a.gltf");

        // No resolver is given, so this fails unless every texture is inside.
        var model = ModelData.Read(new MemoryStream(alone), "a.rmodel");
        Assert.Equal(new byte[] { 200, 255, 0, 255, 100, 255, 0, 128 }, Assert.Single(model.Textures).Mips[0]);
    }

    [Fact]
    public void Material_without_height_or_detail_textures_names_none()
    {
        string src = Temp();
        TinyGltf.Write(src, "a.gltf", "wall", ["a"]);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var material = Model(store, "a.gltf").Materials[0];
        Assert.Equal((-1, -1, -1), (material.HeightTexture, material.DetailColorTexture, material.DetailNormalTexture));
        Assert.Equal(0f, material.HeightDepth);
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

    // One flat colour, metalness, roughness and opacity everywhere, on a flat height, so every pixel is the same.
    private const string FlatGraph = """
        using Super.Materials;

        public sealed class Flat : MaterialGraph
        {
            public float Red { get; init; } = 0.2f;

            public override Material Build() => new Material
            {
                Width = 2f,
                Height = Gray.Of(0.2f),
                Color = Rgb.Of(Red, 0.4f, 0.6f),
                Metallic = Gray.Of(1f),
                Roughness = Gray.Of(0.4f),
                Opacity = Gray.Of(0.8f),
            };
        }
        """;

    // The model in models/, the graphs in materials/, as a game lays them out. The graphs aren't cooked by themselves,
    // so only the model's cook runs them. Textures stay uncompressed unless asked, so tests can read their pixels.
    private string GraphFolder(string rule, string graph = FlatGraph, float[]? uvs = null, bool compress = false)
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        File.WriteAllText(Path.Combine(src, "cook.json"), "{ \"rules\": [ { \"match\": \"**/*.material.*\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "materials", "flat.material.cs"), graph);
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", ["a"], uvs: uvs);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), $"{{ \"compressTextures\": {(compress ? "true" : "false")}, \"materials\": [ {rule} ] }}");
        return src;
    }

    private static string ErrorOf(CookResult result)
    {
        Assert.False(result.Succeeded);
        return Assert.Single(result.Problems, p => p.IsError).Message;
    }

    private static void AllPixelsAre(byte[] expected, TextureData texture)
    {
        Assert.Equal((16, 16), (texture.Width, texture.Height));
        byte[] pixels = texture.Mips[0];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(expected, pixels[i..(i + 4)]);
        }
    }

    [Fact]
    public void A_graph_rule_gives_the_material_the_graphs_colour_normal_and_surface()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var material = model.Materials[Assert.Single(model.Parts).Material];
        // Colour, normal, surface and the height for parallax.
        Assert.Equal(4, model.Textures.Length);
        AllPixelsAre([51, 102, 153, 255], model.Textures[material.BaseColorTexture]);
        AllPixelsAre([128, 128, 255, 255], model.Textures[material.NormalTexture]);
        AllPixelsAre([255, 102, 0, 204], model.Textures[material.SurfaceTexture]);
        Assert.Equal(TextureKind.Color, model.Textures[material.BaseColorTexture].Kind);
        Assert.Equal(TextureKind.Linear, model.Textures[material.NormalTexture].Kind);

        // White and 1, since they multiply the textures.
        Assert.Equal(Vector4.One, material.BaseColor);
        Assert.Equal((1f, 1f), (material.Metallic, material.Roughness));
    }

    [Fact]
    public void A_graphs_height_becomes_a_parallax_height_map_as_deep_as_the_graph()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""",
            FlatGraph.Replace("Width = 2f,", "Width = 2f, Depth = 0.05f,"));

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var material = model.Materials[Assert.Single(model.Parts).Material];
        Assert.Equal(0.05f, material.HeightDepth);
        // The flat graph's height is 0.2 everywhere.
        AllPixelsAre([51, 51, 51, 255], model.Textures[material.HeightTexture]);
    }

    [Fact]
    public void A_height_depth_of_0_turns_a_graphs_parallax_off()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16, "heightDepth": 0 }""");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var material = model.Materials[Assert.Single(model.Parts).Material];
        Assert.Equal((-1, 0f), (material.HeightTexture, material.HeightDepth));
    }

    [Fact]
    public void A_detail_graph_gives_the_detail_textures_tiled_by_the_two_graphs_widths()
    {
        string src = GraphFolder("""
            { "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16,
              "detailGraph": "../materials/grain.material.cs", "detailGraphSize": 16 }
            """);
        File.WriteAllText(Path.Combine(src, "materials", "grain.material.cs"),
            FlatGraph.Replace("class Flat", "class Grain").Replace("Width = 2f,", "Width = 0.5f,"));

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var material = model.Materials[Assert.Single(model.Parts).Material];
        // 2 m over 0.5 m.
        Assert.Equal(4f, material.DetailScale);
        // One flat colour is its own average brightness, which is mid grey.
        AllPixelsAre([128, 128, 128, 255], model.Textures[material.DetailColorTexture]);
        AllPixelsAre([128, 128, 255, 255], model.Textures[material.DetailNormalTexture]);
    }

    [Fact]
    public void A_detail_graph_needs_a_main_graph()
    {
        string src = GraphFolder("""{ "match": "bark", "detailGraph": "../materials/flat.material.cs" }""");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.Equal("material bark: detailGraph tiles at a size the main graph's width sets, so it needs a graph.",
            ErrorOf(result));
    }

    [Fact]
    public void World_variation_and_dithering_reach_the_materials_flags()
    {
        string src = GraphFolder("""
            { "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16, "worldVariation": true, "dithered": true }
            """);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var flags = model.Materials[Assert.Single(model.Parts).Material].Flags;
        Assert.Equal(MaterialFlags.WorldVariation | MaterialFlags.Dithered, flags & (MaterialFlags.WorldVariation | MaterialFlags.Dithered));
    }

    [Fact]
    public void A_puddles_rule_reaches_the_materials_flags_and_without_it_the_flag_is_clear()
    {
        string wet = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16, "puddles": true }""");
        string dry = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""");

        var (wetResult, wetStore) = Cook(new FolderRoot(wet));
        var (dryResult, dryStore) = Cook(new FolderRoot(dry));

        Assert.True(wetResult.Succeeded, string.Join("; ", wetResult.Problems));
        Assert.True(dryResult.Succeeded, string.Join("; ", dryResult.Problems));
        var wetModel = Model(wetStore, "models/a.gltf");
        var dryModel = Model(dryStore, "models/a.gltf");
        Assert.Equal(MaterialFlags.Puddles, wetModel.Materials[Assert.Single(wetModel.Parts).Material].Flags & MaterialFlags.Puddles);
        Assert.Equal(MaterialFlags.None, dryModel.Materials[Assert.Single(dryModel.Parts).Material].Flags & MaterialFlags.Puddles);
    }

    [Fact]
    public void Textures_are_block_compressed_by_what_the_material_uses_them_for()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""", compress: true);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var material = model.Materials[Assert.Single(model.Parts).Material];
        Assert.Equal(TextureFormat.Bc7, model.Textures[material.BaseColorTexture].Format);
        Assert.Equal(TextureFormat.Bc7, model.Textures[material.SurfaceTexture].Format);
        Assert.Equal(TextureFormat.Bc5, model.Textures[material.NormalTexture].Format);
        Assert.Equal(TextureFormat.Bc4, model.Textures[material.HeightTexture].Format);
        foreach (var texture in model.Textures)
        {
            // 16, 8 and 4 pixels across are whole blocks; 2 and 1 still take a block each.
            int[] sizes = [16, 8, 4, 2, 1];
            Assert.Equal(sizes.Select(s => TextureData.LevelSize(texture.Format, s, s)), texture.Mips.Select(m => m.Length));
        }
    }

    [Fact]
    public void A_graph_rule_keeps_the_colour_metallic_and_roughness_it_sets()
    {
        string src = GraphFolder("""
            { "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16,
              "color": [0.5, 0.25, 1], "metallic": 0.5, "roughness": 0.75 }
            """);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        var material = model.Materials[Assert.Single(model.Parts).Material];
        Assert.Equal(new Vector4(0.5f, 0.25f, 1f, 1f), material.BaseColor);
        Assert.Equal((0.5f, 0.75f), (material.Metallic, material.Roughness));
    }

    [Fact]
    public void A_graph_material_has_no_detail_textures_and_glass_bends_nothing_unless_its_rule_says()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16, "transparent": true }""");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var material = Model(store, "models/a.gltf").Materials[0];
        // 0 would be the graph's own colour texture, laid over the surface again as detail.
        Assert.Equal((-1, -1), (material.DetailColorTexture, material.DetailNormalTexture));
        // 0 would bend the scene behind glass the wrong way.
        Assert.Equal(1f, material.Ior);
    }

    [Fact]
    public void A_graph_rule_keeps_the_ior_it_sets()
    {
        string src = GraphFolder("""
            { "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16,
              "transparent": true, "ior": 1.5 }
            """);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(1.5f, Model(store, "models/a.gltf").Materials[0].Ior);
    }

    [Fact]
    public void A_graph_variant_rule_runs_its_graph_with_its_values()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/red.material.json", "graphSize": 16 }""");
        File.WriteAllText(Path.Combine(src, "materials", "red.material.json"), """{ "graph": "flat.material.cs", "red": 1 }""");

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store, "models/a.gltf");
        AllPixelsAre([255, 102, 153, 255], model.Textures[model.Materials[Assert.Single(model.Parts).Material].BaseColorTexture]);
    }

    [Fact]
    public void A_graphs_image_is_read_beside_the_graph_not_the_model()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/photo.material.cs", "graphSize": 16 }""");
        File.WriteAllBytes(Path.Combine(src, "materials", "rock.png"), Png());
        File.WriteAllText(Path.Combine(src, "materials", "photo.material.cs"), """
            using Super.Materials;

            public sealed class Photo : MaterialGraph
            {
                public override Material Build() => new Material { Color = Image("rock.png") };
            }
            """);

        var (result, _) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
    }

    [Fact]
    public void A_graph_material_reads_its_uvs_in_metres_of_the_graphs_width()
    {
        // The graph is 2 m wide. The file's v runs down from its top-left origin, so the v up from the bottom is 1 - v.
        // That is divided by 2 and turned back: 1 - (1 - v) / 2. u is just divided by 2.
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""",
            uvs: [0f, 0f, 4f, 1f, 1f, 0.5f]);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var vertices = Model(store, "models/a.gltf").Vertices;
        Vector2 UvAt(float x, float y) => Assert.Single(vertices, v => MathF.Abs(v.Position.X - x) < 1e-4f && MathF.Abs(v.Position.Y - y) < 1e-4f).Uv;

        // The triangle is centred on x, so its corners (0, 0), (1, 0) and (0, 1) stand at x - 0.5.
        Assert.Equal(new Vector2(0f, 0.5f), UvAt(-0.5f, 0f));
        Assert.Equal(new Vector2(2f, 1f), UvAt(0.5f, 0f));
        Assert.Equal(new Vector2(0.5f, 0.75f), UvAt(-0.5f, 1f));
    }

    [Fact]
    public void Without_a_graph_the_uvs_are_the_files_own()
    {
        string src = GraphFolder("""{ "match": "bark", "roughness": 0.5 }""", uvs: [0f, 0f, 4f, 1f, 1f, 0.5f]);

        var (result, store) = Cook(new FolderRoot(src));

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var vertices = Model(store, "models/a.gltf").Vertices;
        Assert.Equal(new Vector2(1f, 0.5f), Assert.Single(vertices, v => v.Position.Y > 0.5f).Uv);
    }

    [Theory]
    [InlineData("baseColor", "bark.png")]
    [InlineData("normal", "bark.png")]
    [InlineData("surface", "bark.png")]
    [InlineData("alpha", "bark.png")]
    public void A_graph_with_a_texture_of_its_own_is_an_error_naming_the_material(string key, string file)
    {
        string src = GraphFolder($$"""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16, "{{key}}": "{{file}}" }""");
        File.WriteAllBytes(Path.Combine(src, "models", file), Png());

        var (result, _) = Cook(new FolderRoot(src));

        Assert.Equal(
            "material bark: graph makes the colour, normal and surface textures, so it can't be used with baseColor, normal, surface or alpha.",
            ErrorOf(result));
    }

    [Fact]
    public void A_missing_graph_is_an_error_naming_the_material_and_the_path()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/nope.material.cs", "graphSize": 16 }""");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.Equal("material bark: materials/nope.material.cs isn't in the source root.", ErrorOf(result));
    }

    [Fact]
    public void A_graph_that_is_not_a_material_file_is_an_error()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.cs", "graphSize": 16 }""");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.Equal("material bark: ../materials/flat.cs isn't a material graph. A graph is a .material.cs or .material.json file.", ErrorOf(result));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(8)]
    [InlineData(8192)]
    public void A_graph_size_that_is_not_a_power_of_two_from_16_to_4096_is_an_error(int size)
    {
        string src = GraphFolder($$"""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": {{size}} }""");

        var (result, _) = Cook(new FolderRoot(src));

        Assert.Equal($"material bark: graphSize must be a power of two from 16 to 4096, not {size}.", ErrorOf(result));
    }

    [Fact]
    public void A_graph_that_throws_is_an_error_naming_the_material_the_graph_and_its_line()
    {
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""", """
            using Super.Materials;

            public sealed class Throws : MaterialGraph
            {
                public override Material Build()
                {
                    throw new InvalidOperationException("no bricks today");
                }
            }
            """);

        var (result, _) = Cook(new FolderRoot(src));

        Assert.Equal("material bark: ../materials/flat.material.cs line 7: no bricks today", ErrorOf(result));
    }

    [Fact]
    public void Changing_a_graph_cooks_the_model_again()
    {
        string cache = Temp();
        string src = GraphFolder("""{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16 }""");

        var first = Cook(new FolderRoot(src), cache);
        var second = Cook(new FolderRoot(src), cache);
        File.WriteAllText(Path.Combine(src, "materials", "flat.material.cs"), FlatGraph.Replace("0.2f;", "1f;"));
        var third = Cook(new FolderRoot(src), cache);

        Assert.True(first.Result.Succeeded, string.Join("; ", first.Result.Problems));
        Assert.Equal((1, 0), (first.Result.Cooked, first.Result.FromCache));
        Assert.Equal((0, 1), (second.Result.Cooked, second.Result.FromCache));
        Assert.Equal((1, 0), (third.Result.Cooked, third.Result.FromCache));
        var model = Model(third.Store, "models/a.gltf");
        AllPixelsAre([255, 102, 153, 255], model.Textures[model.Materials[Assert.Single(model.Parts).Material].BaseColorTexture]);
    }

    [Fact]
    public void Model_cookers_are_the_built_in_ones_and_the_material_and_model_cookers()
    {
        Assert.Equal(
            BuiltInCookers.All.Select(c => c.Id).Append("material").Append("model").Order(StringComparer.Ordinal),
            ModelCookers.All.Select(c => c.Id).Order(StringComparer.Ordinal));
    }
}
