using System.Text;
using Rendition;

namespace Super.Cook.Tests;

public sealed class CacheTests : IDisposable
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

    private sealed class ProbeCooker(Action<CookInput, CookOutput> cook, int version = 1) : ICooker
    {
        public string Id => "probe";

        public int Version { get; } = version;

        public IReadOnlyList<string> Extensions { get; } = [".probe"];

        public void Cook(CookInput input, CookOutput output)
        {
            cook(input, output);
        }
    }

    private static CookResult Cook(string cache, IReadOnlyList<ISourceRoot> roots, ICookedStore? store = null, params ICooker[] extra)
    {
        var cookers = BuiltInCookers.All.Concat(extra).ToList();
        return new CookPipeline(cookers, new CookOptions(CacheFolder: cache)).Cook(roots, store ?? new MemoryStore());
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

    private static ICooker ReadsExtra(int version = 1)
    {
        return new ProbeCooker((input, output) => output.Add(ContentKind.Data, input.Read("extra.txt")), version);
    }

    [Fact]
    public void Second_cook_comes_from_cache()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllBytes(Path.Combine(src, "a.png"), Png());

        var first = Cook(cache, [new FolderRoot(src)]);
        var second = Cook(cache, [new FolderRoot(src)]);

        Assert.Equal(1, first.Cooked);
        Assert.Equal(0, first.FromCache);
        Assert.Equal(0, second.Cooked);
        Assert.Equal(1, second.FromCache);
    }

    [Fact]
    public void Cached_cook_produces_the_same_manifest()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllBytes(Path.Combine(src, "a.png"), Png());
        File.WriteAllText(Path.Combine(src, "b.json"), "{ \"x\": 1 }");
        var storeA = new MemoryStore();
        var storeB = new MemoryStore();

        Cook(cache, [new FolderRoot(src)], storeA);
        var second = Cook(cache, [new FolderRoot(src)], storeB);

        Assert.Equal(2, second.FromCache);
        Assert.Equal(storeA.Manifest.Assets.OrderBy(a => a.Key, StringComparer.Ordinal), storeB.Manifest.Assets.OrderBy(a => a.Key, StringComparer.Ordinal));
        Assert.Equal(storeA.Get(storeA.Manifest.Assets["a.png"].Hash), storeB.Get(storeB.Manifest.Assets["a.png"].Hash));
    }

    [Fact]
    public void Memory_roots_are_cached_too()
    {
        string cache = Temp();
        var root = new MemoryRoot();
        root["a.json"] = "{ \"x\": 1 }";

        Cook(cache, [root]);
        var second = Cook(cache, [root]);

        Assert.Equal(1, second.FromCache);
    }

    [Fact]
    public void Changing_settings_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllBytes(Path.Combine(src, "a.png"), Png());
        Cook(cache, [new FolderRoot(src)]);
        File.WriteAllText(Path.Combine(src, "a.png.cook.json"), "{ \"linear\": true }");

        var result = Cook(cache, [new FolderRoot(src)]);

        Assert.Equal(1, result.Cooked);
        Assert.Equal(0, result.FromCache);
    }

    [Fact]
    public void Changing_the_source_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");
        Cook(cache, [new FolderRoot(src)]);
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 22 }");

        var result = Cook(cache, [new FolderRoot(src)]);

        Assert.Equal(1, result.Cooked);
    }

    [Fact]
    public void Changing_a_tracked_input_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        File.WriteAllText(Path.Combine(src, "extra.txt"), "one");
        var cooker = ReadsExtra();
        Cook(cache, [new FolderRoot(src)], null, cooker);

        var unchanged = Cook(cache, [new FolderRoot(src)], null, cooker);
        File.WriteAllText(Path.Combine(src, "extra.txt"), "two!");
        var store = new MemoryStore();
        var changed = Cook(cache, [new FolderRoot(src)], store, cooker);

        Assert.Equal(1, unchanged.FromCache);
        Assert.Equal(0, unchanged.Cooked);
        Assert.Equal(1, changed.Cooked);
        Assert.Equal(0, changed.FromCache);
        Assert.Equal("two!", Encoding.UTF8.GetString(store.Get(store.Manifest.Assets["a.probe"].Hash)));
    }

    [Fact]
    public void Changing_a_found_file_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        Directory.CreateDirectory(Path.Combine(src, "deep"));
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        File.WriteAllText(Path.Combine(src, "deep", "hint.dat"), "one");
        var cooker = new ProbeCooker((input, output) => output.Add(ContentKind.Data, [(byte)input.Find("hint.dat")!.Length]));
        Cook(cache, [new FolderRoot(src)], null, cooker);

        var unchanged = Cook(cache, [new FolderRoot(src)], null, cooker);
        File.WriteAllText(Path.Combine(src, "deep", "hint.dat"), "two!");
        var changed = Cook(cache, [new FolderRoot(src)], null, cooker);

        Assert.Equal(1, unchanged.FromCache);
        Assert.Equal(1, changed.Cooked);
    }

    [Fact]
    public void A_tracked_file_that_appears_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        var cooker = new ProbeCooker((input, output) =>
        {
            input.Track("later.dat");
            output.Add(ContentKind.Data, [1]);
        });
        Cook(cache, [new FolderRoot(src)], null, cooker);

        var unchanged = Cook(cache, [new FolderRoot(src)], null, cooker);
        File.WriteAllText(Path.Combine(src, "later.dat"), "here");
        var appeared = Cook(cache, [new FolderRoot(src)], null, cooker);

        Assert.Equal(1, unchanged.FromCache);
        Assert.Equal(1, appeared.Cooked);
    }

    [Fact]
    public void Bumping_cooker_version_cooks_again()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        File.WriteAllText(Path.Combine(src, "extra.txt"), "one");
        Cook(cache, [new FolderRoot(src)], null, ReadsExtra(1));

        var same = Cook(cache, [new FolderRoot(src)], null, ReadsExtra(1));
        var bumped = Cook(cache, [new FolderRoot(src)], null, ReadsExtra(2));

        Assert.Equal(1, same.FromCache);
        Assert.Equal(1, bumped.Cooked);
        Assert.Equal(0, bumped.FromCache);
    }

    [Fact]
    public void Errors_are_not_cached()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "bad.json"), "{ not json");

        var first = Cook(cache, [new FolderRoot(src)]);
        var second = Cook(cache, [new FolderRoot(src)]);

        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(0, first.Cooked);
        Assert.Equal(0, second.FromCache);
        Assert.Single(second.Problems, p => p.IsError);
    }

    [Fact]
    public void Cache_is_shared_across_output_folders()
    {
        string src = Temp();
        string cache = Temp();
        string outA = Temp();
        string outB = Temp();
        File.WriteAllBytes(Path.Combine(src, "a.png"), Png());
        Cook(cache, [new FolderRoot(src)], new FolderStore(outA));
        var storeB = new FolderStore(outB);

        var result = Cook(cache, [new FolderRoot(src)], storeB);

        Assert.Equal(1, result.FromCache);
        Assert.Equal(0, result.Cooked);
        var manifest = ContentManifest.Parse(File.ReadAllBytes(Path.Combine(outB, ContentManifest.FileName)), "test");
        Assert.True(storeB.Has(manifest.Assets["a.png"].Hash));
    }

    [Fact]
    public void Cache_hit_restores_parts()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        var cooker = new ProbeCooker((input, output) =>
        {
            output.AddPart("a", ContentKind.Data, [1]);
            output.AddPart("b", ContentKind.Texture, [2, 2]);
        });
        Cook(cache, [new FolderRoot(src)], null, cooker);
        var store = new MemoryStore();

        var result = Cook(cache, [new FolderRoot(src)], store, cooker);

        Assert.Equal(1, result.FromCache);
        Assert.Equal(ContentKind.Data, store.Manifest.Assets["a.probe#a"].Kind);
        Assert.Equal(ContentKind.Texture, store.Manifest.Assets["a.probe#b"].Kind);
        Assert.Equal(2, store.Manifest.Assets["a.probe#b"].Size);
        Assert.Equal([2, 2], store.Get(store.Manifest.Assets["a.probe#b"].Hash));
    }

    [Fact]
    public void Cache_hit_reports_the_same_warnings()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        var cooker = new ProbeCooker((input, output) =>
        {
            output.Warning("careful", 3);
            output.Warning("no line");
            output.Add(ContentKind.Data, [1]);
        });
        var fresh = Cook(cache, [new FolderRoot(src)], null, cooker);

        var cached = Cook(cache, [new FolderRoot(src)], null, cooker);

        Assert.Equal(1, cached.FromCache);
        Assert.Equal(2, fresh.Problems.Count);
        Assert.Equal(fresh.Problems, cached.Problems);
    }

    [Fact]
    public void A_note_is_not_a_problem_and_a_cache_hit_drops_it()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        var cooker = new ProbeCooker((input, output) =>
        {
            output.Note("made something");
            output.Warning("careful");
            output.Add(ContentKind.Data, [1]);
        });
        var fresh = Cook(cache, [new FolderRoot(src)], null, cooker);

        var cached = Cook(cache, [new FolderRoot(src)], null, cooker);

        string file = Path.Combine(src, "a.probe");
        Assert.True(fresh.Succeeded);
        Assert.Equal([$"{file}: note: made something"], fresh.Notes.Select(note => note.ToString()));
        Assert.Equal([$"{file}: warning SUPER001: careful"], fresh.Problems.Select(problem => problem.ToString()));
        Assert.Equal(1, cached.FromCache);
        Assert.Empty(cached.Notes);
        Assert.Equal(fresh.Problems, cached.Problems);
    }

    [Fact]
    public void A_corrupt_record_is_a_miss_not_an_error()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");
        Cook(cache, [new FolderRoot(src)]);
        foreach (string record in Directory.GetFiles(Path.Combine(cache, "records")))
        {
            File.WriteAllText(record, "{ this is not json");
        }

        var result = Cook(cache, [new FolderRoot(src)]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Cooked);
        Assert.Equal(0, result.FromCache);
    }

    [Fact]
    public void A_missing_cached_object_is_a_miss()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");
        Cook(cache, [new FolderRoot(src)]);
        foreach (string file in Directory.GetFiles(Path.Combine(cache, "objects"), "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        var result = Cook(cache, [new FolderRoot(src)]);

        Assert.Equal(1, result.Cooked);
    }

    [Fact]
    public void A_corrupt_cached_object_is_a_miss_and_never_reaches_the_store()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");
        Cook(cache, [new FolderRoot(src)]);
        foreach (string file in Directory.GetFiles(Path.Combine(cache, "objects"), "*", SearchOption.AllDirectories))
        {
            File.WriteAllText(file, "garbage");
        }

        var store = new MemoryStore();
        var result = Cook(cache, [new FolderRoot(src)], store);

        Assert.Equal(1, result.Cooked);
        Assert.Equal("{ \"x\": 1 }", Encoding.UTF8.GetString(store.Get(store.Manifest.Assets["a.json"].Hash)));
    }

    [Fact]
    public void A_damaged_hashes_file_is_an_empty_memo_not_an_error()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.json"), "{ \"x\": 1 }");
        File.WriteAllText(Path.Combine(cache, "hashes.json"), "{ \"k\": \"a\", \"k\": \"b\" }");

        var first = Cook(cache, [new FolderRoot(src)]);
        var second = Cook(cache, [new FolderRoot(src)]);

        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Cooked);
        Assert.Equal(1, second.FromCache);
    }

    [Fact]
    public void No_cache_folder_means_no_caching()
    {
        var root = new MemoryRoot();
        root["a.json"] = "{ }";
        var pipeline = new CookPipeline(BuiltInCookers.All, new CookOptions());

        pipeline.Cook([root], new MemoryStore());
        var second = pipeline.Cook([root], new MemoryStore());

        Assert.Equal(1, second.Cooked);
        Assert.Equal(0, second.FromCache);
    }

    private static CookInput InputFor(MemoryRoot root, string name)
    {
        return new CookInput(new SourceFile(root, name), root.Read(name), new System.Text.Json.Nodes.JsonObject(), "desktop");
    }

    [Fact]
    public void Read_records_the_normalised_path_once()
    {
        var root = new MemoryRoot();
        root["items/axe.probe"] = "x";
        root["items/a.png"] = "p";
        root["shared/b.png"] = "q";
        var input = InputFor(root, "items/axe.probe");

        input.Read("./a.png");
        input.Read("a.png");
        input.Read("sub/../a.png");
        input.Read("../shared/b.png");
        input.Read("..\\shared\\b.png");

        Assert.Equal(["a.png", "../shared/b.png"], input.Inputs);
    }

    [Fact]
    public void Find_and_Track_record_normalised_paths_too()
    {
        var root = new MemoryRoot();
        root["items/axe.probe"] = "x";
        root["items/deep/hint.dat"] = "h";
        var input = InputFor(root, "items/axe.probe");

        input.Find("hint.dat");
        input.Track("./deep/hint.dat");
        input.Track("deep\\hint.dat");

        Assert.Equal(["deep/hint.dat"], input.Inputs);
    }

    [Fact]
    public void Read_of_a_missing_file_records_nothing()
    {
        var root = new MemoryRoot();
        root["a.probe"] = "x";
        var input = InputFor(root, "a.probe");

        var problem = Assert.Throws<FileNotFoundException>(() => input.Read("nope.png"));

        Assert.Equal("nope.png isn't in the source root.", problem.Message);
        Assert.Empty(input.Inputs);
    }

    [Fact]
    public void Read_with_the_wrong_case_fails_even_where_the_disk_ignores_case()
    {
        string src = Temp();
        Directory.CreateDirectory(Path.Combine(src, "items"));
        File.WriteAllText(Path.Combine(src, "items", "axe.probe"), "x");
        File.WriteAllText(Path.Combine(src, "items", "Rock.png"), "p");
        var root = new FolderRoot(src);
        var input = new CookInput(new SourceFile(root, "items/axe.probe"), root.Read("items/axe.probe"), new System.Text.Json.Nodes.JsonObject(), "desktop");

        var problem = Assert.Throws<FileNotFoundException>(() => input.Read("rock.png"));

        Assert.Equal("items/rock.png isn't in the source root, but items/Rock.png is. Names are case-sensitive.", problem.Message);
        Assert.Empty(input.Inputs);
        Assert.Equal("p"u8.ToArray(), input.Read("Rock.png"));
    }

    [Fact]
    public void Read_of_a_file_not_in_the_root_names_it()
    {
        string src = Temp();
        File.WriteAllText(Path.Combine(src, "axe.probe"), "x");
        var root = new FolderRoot(src);
        var input = new CookInput(new SourceFile(root, "axe.probe"), root.Read("axe.probe"), new System.Text.Json.Nodes.JsonObject(), "desktop");

        var problem = Assert.Throws<FileNotFoundException>(() => input.Read("textures/rock.png"));

        Assert.Equal("textures/rock.png isn't in the source root.", problem.Message);
    }

    [Fact]
    public void Track_records_a_file_that_does_not_exist()
    {
        var root = new MemoryRoot();
        root["a.probe"] = "x";
        var input = InputFor(root, "a.probe");

        input.Track("later.dat");

        Assert.Equal(["later.dat"], input.Inputs);
    }

    [Fact]
    public void A_record_lists_its_inputs_in_name_order_whatever_order_they_were_read_in()
    {
        string src = Temp();
        string cache = Temp();
        File.WriteAllText(Path.Combine(src, "a.probe"), "x");
        File.WriteAllText(Path.Combine(src, "b.txt"), "b");
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var cooker = new ProbeCooker((input, output) =>
        {
            input.Read("b.txt");
            input.Read("a.txt");
            output.Add(ContentKind.Data, [1]);
        });

        Cook(cache, [new FolderRoot(src)], null, cooker);

        string record = Assert.Single(Directory.GetFiles(Path.Combine(cache, "records")));
        var names = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(record))!["inputs"]!.AsArray()
            .Select(item => item!["name"]!.GetValue<string>())
            .ToList();
        Assert.Equal(["a.txt", "b.txt"], names);
    }
}
