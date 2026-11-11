using Rendition;
using Super.Cook.Materials;
using Super.Cook.Models;

namespace Super.Cook.Tests;

/// <summary>The tests below count and switch off graph runs shared by the whole process, so nothing else may run beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedGraphRunsCollection
{
    public const string Name = "Shared graph runs";
}

/// <summary>
/// A graph that several sources run at one size runs once, in the cook and across cooks in the same process, and
/// sharing changes no byte of what's cooked, with one worker or many.
/// </summary>
[Collection(SharedGraphRunsCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class SharedGraphRunsTests : IDisposable
{
    private readonly List<string> _folders = [];

    public SharedGraphRunsTests()
    {
        SharedGraphRuns.Forget();
    }

    public void Dispose()
    {
        SharedGraphRuns.Enabled = true;
        SharedGraphRuns.Forget();
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

    // A flat colour with a seam of noise, so its pixels depend on the size it runs at.
    private const string FlatGraph = """
        using Super.Materials;

        public sealed class Flat : MaterialGraph
        {
            public float Red { get; init; } = 0.2f;

            public override Material Build() => new Material
            {
                Width = 2f,
                Height = Noise.Perlin(scale: 4, octaves: 2).Remap(0.2f, 0.4f),
                Color = Rgb.Of(Red, 0.4f, 0.6f),
                Roughness = Gray.Of(0.4f),
            };
        }
        """;

    /// <summary>
    /// Two models and the graphs they use, which are also cooked as materials of their own at 16 pixels, and a variant.
    /// Model a runs both graphs at 16, as the materials do, and model b runs the flat one at 32.
    /// </summary>
    private string SharedFolder()
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        File.WriteAllText(Path.Combine(src, "cook.json"), """{ "rules": [{ "match": "**/*.material.*", "material": { "size": 16 } }] }""");
        File.WriteAllText(Path.Combine(src, "materials", "flat.material.cs"), FlatGraph);
        File.WriteAllText(Path.Combine(src, "materials", "grain.material.cs"),
            FlatGraph.Replace("class Flat", "class Grain").Replace("Width = 2f,", "Width = 0.5f,"));
        File.WriteAllText(Path.Combine(src, "materials", "red.material.json"), """{ "graph": "flat.material.cs", "Red": 0.8 }""");
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), """
            { "compressTextures": false, "materials": [{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 16,
              "detailGraph": "../materials/grain.material.cs", "detailGraphSize": 16 }] }
            """);
        TinyGltf.Write(Path.Combine(src, "models"), "b.gltf", "bark", ["b"]);
        File.WriteAllText(Path.Combine(src, "models", "b.gltf.cook.json"), """
            { "compressTextures": false, "materials": [{ "match": "bark", "graph": "../materials/flat.material.cs", "graphSize": 32 }] }
            """);
        return src;
    }

    private static (CookResult Result, MemoryStore Store) Cook(string src, int workers)
    {
        var store = new MemoryStore();
        var result = new CookPipeline([.. ModelCookers.All, new MaterialCooker()], new CookOptions(Workers: workers)).Cook([new FolderRoot(src)], store);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return (result, store);
    }

    private static List<(string Name, string Hash)> Assets(MemoryStore store) =>
        [.. store.Manifest.Assets.Select(pair => (pair.Key, pair.Value.Hash)).Order()];

    [Fact]
    public void A_graph_two_sources_run_at_the_same_size_runs_once()
    {
        string src = SharedFolder();

        int before = SharedGraphRuns.Made;
        Cook(src, workers: 4);

        // flat at 16 for its material and model a, grain at 16 for its material and a's detail, the variant, and flat at 32 for b.
        Assert.Equal(4, SharedGraphRuns.Made - before);
    }

    [Fact]
    public void Without_sharing_every_source_runs_its_own()
    {
        string src = SharedFolder();
        SharedGraphRuns.Enabled = false;

        int before = SharedGraphRuns.Made;
        Cook(src, workers: 4);

        Assert.Equal(6, SharedGraphRuns.Made - before);
    }

    [Fact]
    public void Sharing_and_workers_change_no_byte()
    {
        string src = SharedFolder();

        SharedGraphRuns.Enabled = false;
        var (_, alone) = Cook(src, workers: 1);
        SharedGraphRuns.Enabled = true;
        SharedGraphRuns.Forget();
        var (_, shared) = Cook(src, workers: 8);

        Assert.Equal(Assets(alone), Assets(shared));
        Assert.Equal(alone.Manifest.Objects.Order(), shared.Manifest.Objects.Order());
        Assert.Contains(Assets(shared), asset => asset.Name == "models/b.gltf");
    }

    [Fact]
    public void A_later_cook_reuses_a_run_and_runs_again_when_an_image_the_graph_read_changed()
    {
        string src = Temp();
        File.WriteAllText(Path.Combine(src, "cook.json"), """{ "rules": [{ "match": "*.material.*", "material": { "size": 16 } }] }""");
        File.WriteAllText(Path.Combine(src, "rock.material.cs"), """
            using Super.Materials;

            public sealed class Rock : MaterialGraph
            {
                public override Material Build() => new Material { Color = Image("rock.png").ToGray().GradientMap(Ramp.Of("#000000", "#ffffff")) };
            }
            """);
        PngFile.Write(Path.Combine(src, "rock.png"), [255, 255, 255, 255], 1, 1);

        int before = SharedGraphRuns.Made;
        var (_, white) = Cook(src, workers: 1);
        Cook(src, workers: 1);
        Assert.Equal(1, SharedGraphRuns.Made - before);

        PngFile.Write(Path.Combine(src, "rock.png"), [0, 0, 0, 255], 1, 1);
        var (_, black) = Cook(src, workers: 1);

        Assert.Equal(2, SharedGraphRuns.Made - before);
        Assert.Equal([255, 255, 255, 255], Color(white));
        Assert.Equal([0, 0, 0, 255], Color(black));
    }

    private static byte[] Color(MemoryStore store) =>
        CookedTexture.Read(store.Get(store.Manifest.Assets["rock.material.cs#color"].Hash), "rock").Pixels[..4];
}
