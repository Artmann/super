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

        // The new run took the old one's place, so the next cook shares it instead of running the graph again.
        Cook(src, workers: 1);
        Assert.Equal(2, SharedGraphRuns.Made - before);
    }

    private const string RockGraph = """
        using Super.Materials;

        public sealed class Rock : MaterialGraph
        {
            public override Material Build() => new Material { Color = Image("rock.png").ToGray().GradientMap(Ramp.Of("#000000", "#ffffff")) };
        }
        """;

    /// <summary>A rock material that reads a picture, cooked at 16 pixels, and, unless left out, a model that runs it at 16 too.</summary>
    private string RockFolder(bool withModel = true)
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        File.WriteAllText(Path.Combine(src, "cook.json"), """{ "rules": [{ "match": "**/*.material.*", "material": { "size": 16 } }] }""");
        File.WriteAllText(Path.Combine(src, "materials", "rock.material.cs"), RockGraph);
        PngFile.Write(Path.Combine(src, "materials", "rock.png"), [255, 255, 255, 255], 1, 1);
        if (withModel)
        {
            AddRockModel(src);
        }

        return src;
    }

    private static void AddRockModel(string src)
    {
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "stone", ["a"]);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), """
            { "compressTextures": false, "materials": [{ "match": "stone", "graph": "../materials/rock.material.cs", "graphSize": 16 }] }
            """);
    }

    private static (CookResult Result, MemoryStore Store) Cook(string src, int workers, string cache)
    {
        var store = new MemoryStore();
        var result = new CookPipeline([.. ModelCookers.All, new MaterialCooker()], new CookOptions(Workers: workers, CacheFolder: cache))
            .Cook([new FolderRoot(src)], store);
        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        return (result, store);
    }

    [Fact]
    public void A_material_that_shared_a_models_run_cooks_again_when_an_image_the_graph_read_changed()
    {
        string src = RockFolder();
        string cache = Temp();

        int before = SharedGraphRuns.Made;
        var (_, white) = Cook(src, workers: 1, cache);
        // The model and the material shared one run, so one of them never ran the graph.
        Assert.Equal(1, SharedGraphRuns.Made - before);

        // As in a new process, which has no runs to share.
        SharedGraphRuns.Forget();
        PngFile.Write(Path.Combine(src, "materials", "rock.png"), [0, 0, 0, 255], 1, 1);
        var (_, black) = Cook(src, workers: 1, cache);

        Assert.Equal([255, 255, 255, 255], Color(white, "materials/rock.material.cs"));
        Assert.Equal([0, 0, 0, 255], Color(black, "materials/rock.material.cs"));
    }

    [Fact]
    public void A_run_a_material_made_is_kept_for_a_model_that_cooks_later()
    {
        string src = RockFolder(withModel: false);
        string cache = Temp();
        Cook(src, workers: 1, cache);

        AddRockModel(src);
        SharedGraphRuns.Forget();
        int before = SharedGraphRuns.Made;
        var (result, _) = Cook(src, workers: 1, cache);

        Assert.Equal(0, SharedGraphRuns.Made - before);
        // Only the model cooked: the material came from the cook cache.
        Assert.Equal(1, result.Cooked);
    }

    [Fact]
    public void A_detail_graph_doesnt_run_ahead_when_its_main_graph_fails()
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        File.WriteAllText(Path.Combine(src, "cook.json"), """{ "rules": [{ "match": "**/*.material.*", "cook": false }] }""");
        File.WriteAllText(Path.Combine(src, "materials", "broken.material.cs"), "public sealed class Broken : MaterialGraph {");
        File.WriteAllText(Path.Combine(src, "materials", "grain.material.cs"), FlatGraph.Replace("class Flat", "class Grain"));
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "bark", ["a"]);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), """
            { "compressTextures": false, "materials": [{ "match": "bark", "graph": "../materials/broken.material.cs", "graphSize": 16,
              "detailGraph": "../materials/grain.material.cs", "detailGraphSize": 16 }] }
            """);

        int before = SharedGraphRuns.Made;
        var result = new CookPipeline([.. ModelCookers.All, new MaterialCooker()], new CookOptions(Workers: 1)).Cook([new FolderRoot(src)], new MemoryStore());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.IsError && problem.Message.Contains("broken.material.cs", StringComparison.Ordinal));
        Assert.Equal(0, SharedGraphRuns.Made - before);
    }

    [Fact]
    public void When_several_graphs_run_ahead_fail_the_first_materials_failure_is_the_one_thrown()
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "models"));
        Directory.CreateDirectory(Path.Combine(src, "materials"));
        File.WriteAllText(Path.Combine(src, "cook.json"), """{ "rules": [{ "match": "**/*.material.*", "cook": false }] }""");
        string first = Path.Combine(src, "materials", "first.material.cs");
        string second = Path.Combine(src, "materials", "second.material.cs");
        File.WriteAllText(first, FlatGraph);
        File.WriteAllText(second, FlatGraph);
        TinyGltf.Write(Path.Combine(src, "models"), "a.gltf", "one", ["a", "b"], nodeMaterials: ["one", "two"]);
        File.WriteAllText(Path.Combine(src, "models", "a.gltf.cook.json"), """
            { "compressTextures": false, "materials": [
              { "match": "one", "graph": "../materials/first.material.cs", "graphSize": 16 },
              { "match": "two", "graph": "../materials/second.material.cs", "graphSize": 16 }] }
            """);

        // Both are held open by another program, so both runs fail. Asked one by one, the first material's would fail first.
        IOException error;
        using (File.Open(first, FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(second, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            error = Assert.Throws<IOException>(() =>
                new CookPipeline([.. ModelCookers.All], new CookOptions(Workers: 1)).Cook([new FolderRoot(src)], new MemoryStore()));
        }

        Assert.Contains("first.material.cs", error.Message);
        Assert.DoesNotContain("second.material.cs", error.Message);
    }

    private static byte[] Color(MemoryStore store, string name = "rock.material.cs") =>
        CookedTexture.Read(store.Get(store.Manifest.Assets[name + "#color"].Hash), "rock").Pixels[..4];
}

/// <summary>
/// The runs kept for later sources stay under their cap by dropping the ones kept longest, and never one still being
/// made, which other sources may be waiting for.
/// </summary>
[Collection(SharedGraphRunsCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class SharedGraphRunsCapTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "super-cap-" + Guid.NewGuid().ToString("N"));

    public SharedGraphRunsCapTests()
    {
        Directory.CreateDirectory(_folder);
        SharedGraphRuns.Forget();
        // Each run below is 100 bytes, so eight fit.
        SharedGraphRuns.MaxSharedBytes = 800;
    }

    public void Dispose()
    {
        SharedGraphRuns.MaxSharedBytes = SharedGraphRuns.DefaultMaxSharedBytes;
        SharedGraphRuns.Forget();
        Directory.Delete(_folder, true);
    }

    private CookInput Input() => new(new SourceFile(new FolderRoot(_folder), "a.bin"), [], [], "desktop");

    // 25 bytes in each of its four textures, and nothing read, so it's shared with any source.
    private static SharedGraphRuns.Run NewRun() => new(
        new PackedImages(16, 1f, 0f, new MaterialPixels(new byte[25], new byte[25], new byte[25], new byte[25], [])),
        [],
        new MaterialChecks.MaterialFindings(null, null, false, false));

    [Fact]
    public void Past_the_cap_the_runs_kept_longest_are_dropped_first()
    {
        var input = Input();
        for (int i = 0; i < 9; i++)
        {
            SharedGraphRuns.GetOrMake($"run {i}", input, "", NewRun);
        }

        Assert.Equal(800, SharedGraphRuns.SharedBytes);

        int made = 0;
        SharedGraphRuns.GetOrMake("run 8", input, "", () => { made++; return NewRun(); });
        SharedGraphRuns.GetOrMake("run 1", input, "", () => { made++; return NewRun(); });
        Assert.Equal(0, made);

        SharedGraphRuns.GetOrMake("run 0", input, "", () => { made++; return NewRun(); });
        Assert.Equal(1, made);
        Assert.Equal(800, SharedGraphRuns.SharedBytes);
    }

    [Fact]
    public async Task A_run_still_being_made_isnt_dropped_and_a_source_that_wants_it_waits_for_it()
    {
        var input = Input();
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var slow = NewRun();
        var maker = Task.Run(() => SharedGraphRuns.GetOrMake("slow", input, "", () =>
        {
            started.Set();
            finish.Wait();
            return slow;
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the slow run to start");

        // Twice the cap is kept while the slow run is made.
        for (int i = 0; i < 16; i++)
        {
            SharedGraphRuns.GetOrMake($"run {i}", input, "", NewRun);
        }

        bool madeAgain = false;
        var waiter = Task.Run(() => SharedGraphRuns.GetOrMake("slow", input, "", () =>
        {
            madeAgain = true;
            return NewRun();
        }));
        finish.Set();

        Assert.Same(slow, await waiter);
        Assert.Same(slow, await maker);
        Assert.False(madeAgain);
        Assert.True(SharedGraphRuns.SharedBytes <= 800, $"{SharedGraphRuns.SharedBytes} bytes kept");
    }
}
