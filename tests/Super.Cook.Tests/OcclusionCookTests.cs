using System.Text.Json.Nodes;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>
/// A glTF material's occlusion texture, which Assimp files as its lightmap, and the sidecar rules that change it. The
/// model is two quads whose second UV set either fits a lightmap, so the cook keeps it, or has charts that touch, so
/// the cook makes a new one.
/// </summary>
public sealed class OcclusionCookTests : IDisposable
{
    // Each quad in its own corner of 0 to 1, far apart: a set the cook keeps.
    private static readonly float[] KeptUvs2 =
    [
        0.1f, 0.1f, 0.45f, 0.1f, 0.45f, 0.45f, 0.1f, 0.45f,
        0.55f, 0.1f, 0.9f, 0.1f, 0.9f, 0.45f, 0.55f, 0.45f,
    ];

    // The quads one texel apart at 512, under the two-texel gutter: a set the cook replaces.
    private static readonly float[] TouchingUvs2 =
    [
        0.1f, 0.1f, 0.45f, 0.1f, 0.45f, 0.45f, 0.1f, 0.45f,
        232.5f / 512f, 0.1f, 232.5f / 512f + 0.35f, 0.1f, 232.5f / 512f + 0.35f, 0.45f, 232.5f / 512f, 0.45f,
    ];

    // A 4 by 4 image whose red falls from 240 on the left to 30 on the right, so it can't be mistaken for white.
    private static readonly byte[] Occlusion = MakeOcclusion();

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void An_occlusion_texture_on_texcoord_1_is_read_through_the_second_uv_set()
    {
        string src = Folder(KeptUvs2, texCoord: 1);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Empty(result.Problems);
        var model = Model(store);
        int occlusion = model.OcclusionOf(0);
        Assert.True(occlusion >= 0, "The material has no occlusion texture.");
        Assert.Equal(MaterialFlags.OcclusionOnUv2, model.Materials[0].Flags & MaterialFlags.OcclusionOnUv2);
        Assert.Equal(TextureKind.Linear, model.Textures[occlusion].Kind);
        Assert.Equal(Red(Occlusion), Red(model.Textures[occlusion].Mips[0]));
        // Laid out on the second set's islands, so it mustn't wrap round to the far side at its edges.
        Assert.Equal(TextureEdges.Clamp, model.EdgesOf(occlusion));
    }

    [Fact]
    public void An_occlusion_texture_on_texcoord_0_is_read_through_the_first_uv_set_and_repeats()
    {
        string src = Folder(KeptUvs2, texCoord: 0);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        int occlusion = model.OcclusionOf(0);
        Assert.True(occlusion >= 0, "The material has no occlusion texture.");
        Assert.Equal(MaterialFlags.None, model.Materials[0].Flags & MaterialFlags.OcclusionOnUv2);
        Assert.Equal(Red(Occlusion), Red(model.Textures[occlusion].Mips[0]));
        Assert.Equal(TextureEdges.Repeat, model.EdgesOf(occlusion));
    }

    [Fact]
    public void An_occlusion_texture_on_a_second_uv_set_the_cook_makes_again_is_left_out_with_a_warning()
    {
        string src = Folder(TouchingUvs2, texCoord: 1);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var warning = Assert.Single(result.Problems);
        Assert.False(warning.IsError);
        Assert.Equal(
            "material bark: its occlusion texture is on the second UV set, which the cook made again for lighting, so it's left out. Give the model a second UV set that fits a lightmap, or put the occlusion on the first.",
            warning.Message);
        var model = Model(store);
        Assert.Empty(model.MaterialOcclusion);
        Assert.Equal(MaterialFlags.None, model.Materials[0].Flags & MaterialFlags.OcclusionOnUv2);
        // Nothing else uses the image, so it isn't cooked at all.
        Assert.Empty(model.Textures);
    }

    [Fact]
    public void An_occlusion_texture_on_texcoord_2_is_left_out_with_a_warning()
    {
        string src = Folder(KeptUvs2, texCoord: 2);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var warning = Assert.Single(result.Problems);
        Assert.Equal(
            "material bark: its occlusion texture is on TEXCOORD_2, and the cook keeps only the first two UV sets, so it's left out.",
            warning.Message);
        Assert.Empty(Model(store).MaterialOcclusion);
    }

    [Fact]
    public void A_rule_can_move_the_files_occlusion_texture_to_the_other_uv_set()
    {
        string src = Folder(KeptUvs2, texCoord: 0, rule: "{ \"match\": \"bark\", \"occlusionUv\": 1 }");

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        Assert.True(model.OcclusionOf(0) >= 0, "The material has no occlusion texture.");
        Assert.Equal(MaterialFlags.OcclusionOnUv2, model.Materials[0].Flags & MaterialFlags.OcclusionOnUv2);
    }

    [Fact]
    public void A_rule_names_an_occlusion_texture_read_through_the_first_uv_set_unless_it_says_otherwise()
    {
        string first = Folder(KeptUvs2, texCoord: null, rule: "{ \"match\": \"bark\", \"occlusion\": \"ao.png\" }");
        string second = Folder(KeptUvs2, texCoord: null, rule: "{ \"match\": \"bark\", \"occlusion\": \"ao.png\", \"occlusionUv\": 1 }");

        var (firstResult, firstStore) = Cook(first);
        var (secondResult, secondStore) = Cook(second);

        Assert.True(firstResult.Succeeded, string.Join("; ", firstResult.Problems));
        Assert.True(secondResult.Succeeded, string.Join("; ", secondResult.Problems));
        var onFirst = Model(firstStore);
        var onSecond = Model(secondStore);
        Assert.Equal(Red(Occlusion), Red(onFirst.Textures[onFirst.OcclusionOf(0)].Mips[0]));
        Assert.Equal(MaterialFlags.None, onFirst.Materials[0].Flags & MaterialFlags.OcclusionOnUv2);
        Assert.Equal(Red(Occlusion), Red(onSecond.Textures[onSecond.OcclusionOf(0)].Mips[0]));
        Assert.Equal(MaterialFlags.OcclusionOnUv2, onSecond.Materials[0].Flags & MaterialFlags.OcclusionOnUv2);
    }

    [Fact]
    public void A_missing_occlusion_texture_a_rule_names_stops_the_cook()
    {
        string src = Folder(KeptUvs2, texCoord: null, rule: "{ \"match\": \"bark\", \"occlusion\": \"gone.png\" }");

        var (result, _) = Cook(src);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Problems, problem => problem.IsError);
        Assert.Equal("material bark: the texture gone.png isn't beside the model or in a folder under it.", error.Message);
    }

    [Fact]
    public void NoOcclusionMap_drops_the_files_occlusion_texture()
    {
        string src = Folder(KeptUvs2, texCoord: 0, rule: "{ \"match\": \"bark\", \"noOcclusionMap\": true }");

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        Assert.Empty(model.MaterialOcclusion);
        Assert.Empty(model.Textures);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void An_occlusionUv_other_than_0_or_1_stops_the_cook(int uv)
    {
        string src = Folder(KeptUvs2, texCoord: 0, rule: $"{{ \"match\": \"bark\", \"occlusionUv\": {uv} }}");

        var (result, _) = Cook(src);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Problems, problem => problem.IsError);
        Assert.Equal($"material bark: occlusionUv must be 0 or 1, not {uv}.", error.Message);
    }

    [Fact]
    public void A_compressed_occlusion_texture_is_bc4()
    {
        string src = Folder(KeptUvs2, texCoord: 1, compress: true);

        var (result, store) = Cook(src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        var model = Model(store);
        Assert.Equal(TextureFormat.Bc4, model.Textures[model.OcclusionOf(0)].Format);
    }

    /// <summary>
    /// The quads with <c>ao.png</c> beside them, linked as the material's occlusion texture on <paramref name="texCoord"/>,
    /// or not linked when it's null, and a sidecar with <paramref name="rule"/> when there is one.
    /// </summary>
    private string Folder(float[] uvs2, int? texCoord, string? rule = null, bool compress = false)
    {
        string src = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N"));
        _folders.Add(src);
        Directory.CreateDirectory(src);
        TinyGltf.WriteQuads(src, "quads.gltf", "bark", uvs2);
        File.WriteAllBytes(Path.Combine(src, "ao.png"), Png(Occlusion));
        if (texCoord is { } set)
        {
            string path = Path.Combine(src, "quads.gltf");
            var gltf = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            gltf["images"] = new JsonArray(new JsonObject { ["uri"] = "ao.png" });
            gltf["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
            gltf["materials"]![0]!["occlusionTexture"] = new JsonObject { ["index"] = 0, ["texCoord"] = set };
            File.WriteAllText(path, gltf.ToJsonString());
        }

        string rules = rule is null ? "" : $", \"materials\": [ {rule} ]";
        File.WriteAllText(Path.Combine(src, "quads.gltf.cook.json"), $"{{ \"compressTextures\": {(compress ? "true" : "false")}{rules} }}");
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

    private static byte[] MakeOcclusion()
    {
        var pixels = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int at = (y * 4 + x) * 4;
                pixels[at] = (byte)(240 - x * 70);
                pixels[at + 1] = 255;
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] Red(byte[] pixels) => [.. pixels.Where((_, i) => i % 4 == 0)];

    private static byte[] Png(byte[] pixels)
    {
        string path = Path.Combine(Path.GetTempPath(), "super-cook-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            PngFile.Write(path, pixels, 4, 4);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
