using System.Security.Cryptography;
using BCnEncoder.Encoder;
using Rendition;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>
/// The step cache inside the cook cache, and the slow steps that keep their results in it: graph runs,
/// baked masks and compressed textures. Each must give the same result from the cache as from running,
/// miss when an input changes and hit when none does.
/// </summary>
public sealed class StepCacheTests : IDisposable
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
        string folder = Path.Combine(Path.GetTempPath(), "super-steps-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // The store itself.

    [Fact]
    public void A_kept_result_is_found_by_its_key_and_only_by_it()
    {
        var steps = new StepCache(Temp());

        steps.Keep("graph-run", "a", [1, 2, 3]);

        Assert.Equal([1, 2, 3], steps.Find("graph-run", "a"));
        Assert.Null(steps.Find("graph-run", "b"));
        Assert.Null(steps.Find("mask", "a"));
    }

    [Fact]
    public void A_damaged_entry_is_a_miss()
    {
        string folder = Temp();
        var steps = new StepCache(folder);
        steps.Keep("graph-run", "a", [1, 2, 3]);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(folder, "steps", "graph-run"), "*", SearchOption.AllDirectories));

        byte[] bytes = File.ReadAllBytes(entry);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(entry, bytes);

        Assert.Null(steps.Find("graph-run", "a"));
    }

    [Fact]
    public void A_cut_short_entry_is_a_miss()
    {
        string folder = Temp();
        var steps = new StepCache(folder);
        steps.Keep("graph-run", "a", [1, 2, 3]);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(folder, "steps", "graph-run"), "*", SearchOption.AllDirectories));

        File.WriteAllBytes(entry, [1, 2]);

        Assert.Null(steps.Find("graph-run", "a"));
    }

    [Fact]
    public void A_step_name_that_isnt_letters_digits_and_dashes_is_refused()
    {
        var steps = new StepCache(Temp());

        var error = Assert.Throws<ArgumentException>(() => steps.Keep("../up", "a", [1]));

        Assert.StartsWith("A step's name is letters, digits and dashes, not \"../up\".", error.Message);
    }

    // Graph runs and masks, through a model's cook.

    private const string FlatGraph = """
        using Super.Materials;

        public sealed class Flat : MaterialGraph
        {
            public float Red { get; init; } = 0.2f;

            public override Material Build() => new Material
            {
                Width = 2f,
                Color = Image("tint.png").Multiply(Rgb.Of(Red, 0.4f, 0.6f)),
                Roughness = Gray.Of(0.4f),
            };
        }
        """;

    private const string WearAndDirt = """
        "bakeMasks": true,
        "wear": { "color": [1, 0.5, 0], "roughness": 0.4, "amount": 0.6 },
        "dirt": { "color": [0, 0, 0], "amount": 0.7 }
        """;

    private static readonly float[] Uvs2 = [0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.9f];

    // A model whose one material is a graph that reads a picture, as a game lays them out.
    private string Folder(string rule, float[]? uvs2 = null)
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        File.WriteAllText(Path.Combine(src, "cook.json"), "{ \"rules\": [ { \"match\": \"**/*.material.*\", \"cook\": false } ] }");
        File.WriteAllText(Path.Combine(src, "materials", "flat.material.cs"), FlatGraph);
        File.WriteAllText(Path.Combine(src, "materials", "red.material.json"), """{ "graph": "flat.material.cs", "red": 0.9 }""");
        File.WriteAllBytes(Path.Combine(src, "materials", "tint.png"), Png([255, 255, 255, 255, 255, 255, 255, 255]));
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", ["a"], uvs2: uvs2);
        Rule(src, rule);
        return src;
    }

    private static void Rule(string src, string rule)
    {
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), $"{{ \"compressTextures\": false, \"materials\": [ {{ \"match\": \"bark\", {rule} }} ] }}");
    }

    private static byte[] Png(byte[] pixels)
    {
        string path = Path.Combine(Path.GetTempPath(), "super-steps-" + Guid.NewGuid().ToString("N") + ".png");
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

    private static (CookResult Result, MemoryStore Store) Cook(string src, string cache)
    {
        var store = new MemoryStore();
        var result = new CookPipeline(ModelCookers.All, new CookOptions(CacheFolder: cache)).Cook([new FolderRoot(src)], store);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return (result, store);
    }

    /// <summary>Forgets every cooked source but keeps the steps, so the next cook cooks the model again from the same inputs.</summary>
    private static void ForgetCookedSources(string cache)
    {
        foreach (string entry in Directory.GetFileSystemEntries(cache))
        {
            if (Path.GetFileName(entry) == "steps")
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static IReadOnlyList<string> PhasesOf(CookResult result)
    {
        var model = Assert.Single(result.Report.Files, file => file.Name == "models/a.gltf");
        Assert.Equal(CookOutcome.Cooked, model.Outcome);
        return [.. model.Phases.Select(phase => phase.Phase)];
    }

    private static ModelData Model(MemoryStore store)
    {
        return ModelData.Read(new MemoryStream(store.Get(store.Manifest.Assets["models/a.gltf"].Hash)), "models/a.gltf",
            hash => TextureData.Read(new MemoryStream(store.Get(hash)), hash));
    }

    private static byte[] Color(MemoryStore store)
    {
        var model = Model(store);
        return model.Textures[model.Materials[Assert.Single(model.Parts).Material].BaseColorTexture].Mips[0];
    }

    private const string Graph = "\"graph\": \"../materials/flat.material.cs\", \"graphSize\": 16";

    [Fact]
    public void An_unchanged_graph_is_taken_from_the_step_cache_and_cooks_the_same_model()
    {
        string src = Folder(Graph);
        string cache = Temp();
        var (first, firstStore) = Cook(src, cache);
        ForgetCookedSources(cache);

        var (second, secondStore) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(first));
        Assert.Contains("recall graph runs", PhasesOf(second));
        Assert.DoesNotContain("run graphs", PhasesOf(second));
        Assert.Equal(firstStore.Manifest.Assets["models/a.gltf"].Hash, secondStore.Manifest.Assets["models/a.gltf"].Hash);
    }

    [Fact]
    public void A_kept_graph_run_that_doesnt_read_runs_the_graph_again()
    {
        string src = Folder(Graph);
        string cache = Temp();
        var (_, firstStore) = Cook(src, cache);
        ForgetCookedSources(cache);
        // An intact entry, as its hash says, whose first image path has a length no string has: one image, then five bytes
        // that each say another follows.
        byte[] payload = [1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff, 0xff];
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(cache, "steps", "graph-run"), "*", SearchOption.AllDirectories));
        File.WriteAllBytes(entry, [.. payload, .. SHA256.HashData(payload)]);

        var (second, secondStore) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(second));
        Assert.Equal(firstStore.Manifest.Assets["models/a.gltf"].Hash, secondStore.Manifest.Assets["models/a.gltf"].Hash);
    }

    [Fact]
    public void A_kept_graph_run_is_compressed_and_gives_back_every_pixel()
    {
        // A picture of different pixels, so each channel's differences aren't all zero.
        string src = Folder(Graph);
        File.WriteAllBytes(Path.Combine(src, "materials", "tint.png"), Png([250, 30, 90, 255, 10, 200, 140, 255]));
        string cache = Temp();
        var (_, firstStore) = Cook(src, cache);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(cache, "steps", "graph-run"), "*", SearchOption.AllDirectories));
        ForgetCookedSources(cache);

        var (second, secondStore) = Cook(src, cache);

        Assert.DoesNotContain("run graphs", PhasesOf(second));
        Assert.Equal(firstStore.Manifest.Assets["models/a.gltf"].Hash, secondStore.Manifest.Assets["models/a.gltf"].Hash);
        Assert.Equal(Color(firstStore), Color(secondStore));
        // Four 16 by 16 images of four bytes a pixel, three of them whole and the height's red, would be 3328 bytes.
        Assert.True(new FileInfo(entry).Length < 1500, $"{new FileInfo(entry).Length} bytes");
    }

    [Fact]
    public void A_kept_graph_run_with_damaged_pixels_runs_the_graph_again()
    {
        string src = Folder(Graph);
        string cache = Temp();
        var (_, firstStore) = Cook(src, cache);
        ForgetCookedSources(cache);
        // Intact as its hash says, but the compressed pixels end too soon.
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(cache, "steps", "graph-run"), "*", SearchOption.AllDirectories));
        byte[] payload = File.ReadAllBytes(entry)[..^32][..^8];
        File.WriteAllBytes(entry, [.. payload, .. SHA256.HashData(payload)]);

        var (second, secondStore) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(second));
        Assert.Equal(firstStore.Manifest.Assets["models/a.gltf"].Hash, secondStore.Manifest.Assets["models/a.gltf"].Hash);
    }

    [Fact]
    public void A_kept_graph_run_with_garbled_pixels_runs_the_graph_again()
    {
        string src = Folder(Graph);
        string cache = Temp();
        var (_, firstStore) = Cook(src, cache);
        ForgetCookedSources(cache);
        // Intact as its hash says, but the last bytes of the compressed pixels aren't Brotli.
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(cache, "steps", "graph-run"), "*", SearchOption.AllDirectories));
        byte[] payload = File.ReadAllBytes(entry)[..^32];
        for (int i = payload.Length - 12; i < payload.Length; i++)
        {
            payload[i] = 0xff;
        }

        File.WriteAllBytes(entry, [.. payload, .. SHA256.HashData(payload)]);

        var (second, secondStore) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(second));
        Assert.Equal(firstStore.Manifest.Assets["models/a.gltf"].Hash, secondStore.Manifest.Assets["models/a.gltf"].Hash);
    }

    [Fact]
    public void Editing_the_graph_runs_it_again()
    {
        string src = Folder(Graph);
        string cache = Temp();
        var (_, before) = Cook(src, cache);
        File.WriteAllText(Path.Combine(src, "materials", "flat.material.cs"), FlatGraph.Replace("= 0.2f;", "= 0.7f;"));

        var (result, after) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(result));
        // White times 0.2 and 0.7 red, in sRGB.
        Assert.Equal(51, Color(before)[0]);
        Assert.Equal(179, Color(after)[0]);
    }

    [Fact]
    public void Changing_a_picture_the_graph_reads_runs_it_again()
    {
        string src = Folder(Graph);
        string cache = Temp();
        var (_, before) = Cook(src, cache);
        File.WriteAllBytes(Path.Combine(src, "materials", "tint.png"), Png([0, 0, 0, 255, 0, 0, 0, 255]));

        var (result, after) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(result));
        Assert.Equal(51, Color(before)[0]);
        Assert.Equal(0, Color(after)[0]);
    }

    [Fact]
    public void Changing_the_graph_size_runs_it_again()
    {
        string src = Folder(Graph);
        string cache = Temp();
        Cook(src, cache);
        Rule(src, Graph.Replace("16", "32"));

        var (result, store) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(result));
        Assert.Equal(32, Model(store).Textures[0].Width);
    }

    [Fact]
    public void Changing_a_variants_values_runs_its_graph_again()
    {
        string src = Folder("\"graph\": \"../materials/red.material.json\", \"graphSize\": 16");
        string cache = Temp();
        var (_, before) = Cook(src, cache);
        File.WriteAllText(Path.Combine(src, "materials", "red.material.json"), """{ "graph": "flat.material.cs", "red": 0.7 }""");

        var (result, after) = Cook(src, cache);

        Assert.Contains("run graphs", PhasesOf(result));
        Assert.Equal(230, Color(before)[0]);
        Assert.Equal(179, Color(after)[0]);
    }

    [Fact]
    public void Unchanged_masks_are_taken_from_the_step_cache_and_cook_the_same_model()
    {
        string src = Folder(WearAndDirt, Uvs2);
        string cache = Temp();
        var (first, firstStore) = Cook(src, cache);
        ForgetCookedSources(cache);

        var (second, secondStore) = Cook(src, cache);

        Assert.Contains("bake masks", PhasesOf(first));
        Assert.Contains("recall masks", PhasesOf(second));
        Assert.DoesNotContain("bake masks", PhasesOf(second));
        Assert.Equal(firstStore.Manifest.Assets["models/a.gltf"].Hash, secondStore.Manifest.Assets["models/a.gltf"].Hash);
    }

    [Fact]
    public void Masks_are_baked_again_when_the_second_uvs_change()
    {
        string src = Folder(WearAndDirt, Uvs2);
        string cache = Temp();
        Cook(src, cache);
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", ["a"], uvs2: [0.2f, 0.2f, 0.8f, 0.2f, 0.2f, 0.8f]);

        var (result, _) = Cook(src, cache);

        Assert.Contains("bake masks", PhasesOf(result));
    }

    // Compressed textures.

    private static TextureData Texture(byte seed) => new()
    {
        Width = 8,
        Height = 8,
        Kind = TextureKind.Color,
        Mips = [Enumerable.Range(0, 8 * 8 * 4).Select(i => (byte)(i * 7 + seed)).ToArray()],
    };

    private CookInput Input(string cache)
    {
        string src = Temp();
        File.WriteAllText(Path.Combine(src, "a.bin"), "");
        return new CookInput(new SourceFile(new FolderRoot(src), "a.bin"), [], [], "desktop", new StepCache(cache));
    }

    private static string[] TextureEntries(string cache) =>
        Directory.GetFiles(Path.Combine(cache, "steps", "compressed-texture"), "*", SearchOption.AllDirectories);

    private static string ObjectFile(string cache, string hash) => Path.Combine(cache, "objects", hash[..2], hash);

    /// <summary>The hash of the object a compressed texture's entry names.</summary>
    private static string Named(string entry) => System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(entry)[..^32]);

    /// <summary>
    /// Points every kept compressed texture at a new object whose blocks are all <paramref name="value"/>, so a texture
    /// taken from the cache shows it. Returns the new objects' files.
    /// </summary>
    private static List<string> Doctor(string cache, byte value)
    {
        var doctoredObjects = new List<string>();
        foreach (string entry in TextureEntries(cache))
        {
            var kept = TextureData.Read(new MemoryStream(File.ReadAllBytes(ObjectFile(cache, Named(entry)))), entry);
            byte[] doctored = new TextureData
            {
                Width = kept.Width, Height = kept.Height, Kind = kept.Kind, Format = kept.Format,
                Mips = [.. kept.Mips.Select(mip => Enumerable.Repeat(value, mip.Length).ToArray())],
            }.ToBytes();
            string hash = Convert.ToHexStringLower(SHA256.HashData(doctored));
            Directory.CreateDirectory(Path.GetDirectoryName(ObjectFile(cache, hash))!);
            File.WriteAllBytes(ObjectFile(cache, hash), doctored);
            byte[] payload = System.Text.Encoding.ASCII.GetBytes(hash);
            File.WriteAllBytes(entry, [.. payload, .. SHA256.HashData(payload)]);
            doctoredObjects.Add(ObjectFile(cache, hash));
        }

        return doctoredObjects;
    }

    [Fact]
    public void A_compressed_texture_is_kept_once_as_an_object_and_its_entry_only_names_it()
    {
        string cache = Temp();
        TextureData[] first = [Texture(1)];
        TextureCompression.Compress(first, [], masks: [0], input: Input(cache));

        string entry = Assert.Single(TextureEntries(cache));
        Assert.Equal(64 + 32, new FileInfo(entry).Length);
        byte[] kept = File.ReadAllBytes(ObjectFile(cache, Named(entry)));
        Assert.Equal(first[0].ToBytes(), kept);

        TextureData[] second = [Texture(1)];
        TextureCompression.Compress(second, [], masks: [0], input: Input(cache));

        Assert.Equal(first[0].Mips, second[0].Mips);
        Assert.Equal(TextureFormat.Bc7, second[0].Format);
        Assert.Equal(TextureKind.Color, second[0].Kind);
    }

    [Fact]
    public void A_compressed_texture_whose_object_was_trimmed_is_compressed_again()
    {
        string cache = Temp();
        TextureData[] first = [Texture(1)];
        TextureCompression.Compress(first, [], masks: [0], input: Input(cache));
        File.Delete(Assert.Single(Doctor(cache, 0xab)));

        TextureData[] second = [Texture(1)];
        TextureCompression.Compress(second, [], masks: [0], input: Input(cache));

        Assert.Equal(first[0].Mips, second[0].Mips);
    }

    [Fact]
    public void A_compressed_texture_whose_object_is_damaged_is_compressed_again()
    {
        string cache = Temp();
        TextureData[] first = [Texture(1)];
        TextureCompression.Compress(first, [], masks: [0], input: Input(cache));
        string doctored = Assert.Single(Doctor(cache, 0xab));
        byte[] bytes = File.ReadAllBytes(doctored);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(doctored, bytes);

        TextureData[] second = [Texture(1)];
        TextureCompression.Compress(second, [], masks: [0], input: Input(cache));

        Assert.Equal(first[0].Mips, second[0].Mips);
    }

    [Fact]
    public void A_texture_with_the_same_pixels_and_format_is_taken_from_the_step_cache()
    {
        string cache = Temp();
        TextureData[] first = [Texture(1)];
        TextureCompression.Compress(first, [], masks: [0], input: Input(cache));
        Doctor(cache, 0xab);

        // A new texture with the same pixels, so the memo of this process doesn't have it.
        TextureData[] second = [Texture(1)];
        TextureCompression.Compress(second, [], masks: [0], input: Input(cache));

        Assert.Equal(TextureFormat.Bc7, second[0].Format);
        Assert.All(second[0].Mips[0], b => Assert.Equal(0xab, b));
        Assert.Contains(first[0].Mips[0], b => b != 0xab);
    }

    [Fact]
    public void A_texture_with_other_pixels_is_compressed_again()
    {
        string cache = Temp();
        TextureCompression.Compress([Texture(1)], [], masks: [0], input: Input(cache));
        Doctor(cache, 0xab);

        TextureData[] other = [Texture(2)];
        TextureCompression.Compress(other, [], masks: [0], input: Input(cache));

        Assert.Equal(TextureFormat.Bc7, other[0].Format);
        Assert.Contains(other[0].Mips[0], b => b != 0xab);
    }

    [Fact]
    public void A_texture_wanted_in_another_format_is_compressed_again()
    {
        string cache = Temp();
        TextureCompression.Compress([Texture(1)], [], masks: [0], input: Input(cache));
        Doctor(cache, 0xab);

        TextureData[] normal = [Texture(1)];
        TextureCompression.Compress(normal, [new ModelMaterial
        {
            BaseColorTexture = -1, NormalTexture = 0, SurfaceTexture = -1, EmissiveTexture = -1, HeightTexture = -1,
            DetailColorTexture = -1, DetailNormalTexture = -1,
        }], input: Input(cache));

        Assert.Equal(TextureFormat.Bc5, normal[0].Format);
        Assert.Contains(normal[0].Mips[0], b => b != 0xab);
    }

    [Fact]
    public void The_key_is_named_by_the_bc7_encoder_version_and_the_bcn_quality()
    {
        var texture = Texture(1);
        string key = TextureCompression.Key(texture, TextureFormat.Bc7);

        Assert.Equal(TextureCompression.Key(texture, TextureFormat.Bc7, TextureCompression.EncoderVersionFor(Bc7Encoder.Version, CompressionQuality.Fast)), key);
        Assert.NotEqual(key, TextureCompression.Key(texture, TextureFormat.Bc7, TextureCompression.EncoderVersionFor(Bc7Encoder.Version + 1, CompressionQuality.Fast)));
        Assert.NotEqual(key, TextureCompression.Key(texture, TextureFormat.Bc7, TextureCompression.EncoderVersionFor(Bc7Encoder.Version, CompressionQuality.Balanced)));
    }

    [Fact]
    public void A_cut_outs_key_is_named_by_its_cutoff()
    {
        // A cut-out is compressed to keep its coverage at its cutoff, so the same pixels at another cutoff, or with none,
        // are compressed differently and must not share an entry.
        var texture = Texture(1);
        string plain = TextureCompression.Key(texture, TextureFormat.Bc7);
        string half = TextureCompression.Key(texture, TextureFormat.Bc7, cutoff: 0.5f);

        Assert.NotEqual(plain, half);
        Assert.NotEqual(half, TextureCompression.Key(texture, TextureFormat.Bc7, cutoff: 0.25f));
        Assert.Equal(half, TextureCompression.Key(texture, TextureFormat.Bc7, cutoff: 0.5f));
    }
}
