using System.Text;
using System.Text.Json;

namespace Super.Cook.Tests;

/// <summary>
/// The order sources start in: the slowest last time first, from <c>durations.json</c> in the cache folder, and models
/// and big files first when they haven't been timed. The results stay in source order whatever order they start in.
/// </summary>
public sealed class StartOrderTests : IDisposable
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

    /// <summary>Notes the order it's called in, warns with the source's name and adds its bytes.</summary>
    private sealed class NotingCooker(string extension, List<string>? started = null, int startRank = 0) : ICooker
    {
        public List<string> Started { get; } = started ?? [];

        public string Id => "noting" + extension;

        public int Version => 1;

        public IReadOnlyList<string> Extensions { get; } = [extension];

        public int StartRank => startRank;

        public void Cook(CookInput input, CookOutput output)
        {
            lock (Started)
            {
                Started.Add(input.Name);
            }

            output.Warning($"cooked {input.Name}");
            output.Add(ContentKind.Data, input.Bytes);
        }
    }

    // One worker, so the sources start one after another in the order the pipeline picks.
    private static CookResult Cook(string src, string cache, MemoryStore store, params ICooker[] cookers) =>
        new CookPipeline(cookers, new CookOptions(CacheFolder: cache, Workers: 1)).Cook([new FolderRoot(src)], store);

    // Starts with the name, so no two sources have the same bytes and one never comes from the cache for another.
    private static void Write(string src, string name, int bytes) =>
        File.WriteAllBytes(Path.Combine(src, name), Encoding.UTF8.GetBytes((name + new string('x', bytes))[..bytes]));

    private static void Durations(string src, string cache, params (string Name, double Seconds)[] times)
    {
        var json = times.ToDictionary(time => new SourceFile(new FolderRoot(src), time.Name).Describe(), time => time.Seconds);
        File.WriteAllText(Path.Combine(cache, "durations.json"), JsonSerializer.Serialize(json));
    }

    private static Dictionary<string, double> SavedDurations(string src, string cache)
    {
        var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(cache, "durations.json")))!;
        return saved.ToDictionary(pair => Path.GetRelativePath(src, pair.Key), pair => pair.Value);
    }

    [Fact]
    public void Sources_start_from_the_slowest_last_time_and_their_results_stay_in_source_order()
    {
        string src = Temp();
        string cache = Temp();
        foreach (string name in new[] { "a.job", "b.job", "c.job", "d.job" })
        {
            Write(src, name, 10);
        }

        Durations(src, cache, ("a.job", 0.5), ("b.job", 3), ("c.job", 0.1), ("d.job", 7));
        var cooker = new NotingCooker(".job");
        var store = new MemoryStore();

        var result = Cook(src, cache, store, cooker);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(["d.job", "b.job", "a.job", "c.job"], cooker.Started);
        Assert.Equal(["cooked a.job", "cooked b.job", "cooked c.job", "cooked d.job"], result.Problems.Select(p => p.Message));
        Assert.Equal(["a.job", "b.job", "c.job", "d.job"], store.Manifest.Assets.Keys);
        Assert.Equal(["a.job", "b.job", "c.job", "d.job"], result.Report.Files.Select(file => file.Name));
    }

    [Fact]
    public void Sources_never_timed_start_first_by_rank_then_size_and_the_rest_by_time()
    {
        string src = Temp();
        string cache = Temp();
        Write(src, "big.job", 300);
        Write(src, "small.job", 10);
        Write(src, "timed.job", 900);
        Write(src, "model.glb", 20);
        Durations(src, cache, ("timed.job", 60));
        var started = new List<string>();
        var jobs = new NotingCooker(".job", started);
        var models = new NotingCooker(".glb", started, startRank: 1);

        var result = Cook(src, cache, new MemoryStore(), jobs, models);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        // The model is smaller than both jobs but its rank puts it first. The one timed goes last, though it's biggest.
        Assert.Equal(["model.glb", "big.job", "small.job", "timed.job"], started);
        Assert.Equal(["big.job", "model.glb", "small.job", "timed.job"], result.Report.Files.Select(file => file.Name));
    }

    [Fact]
    public void A_cook_keeps_the_time_of_every_source_it_cooked_and_not_of_those_from_the_cache()
    {
        string src = Temp();
        string cache = Temp();
        Write(src, "a.job", 10);
        Write(src, "b.job", 10);
        Assert.True(Cook(src, cache, new MemoryStore(), new NotingCooker(".job")).Succeeded);
        var first = SavedDurations(src, cache);
        Assert.Equal(["a.job", "b.job"], first.Keys.Order());

        // Only b changes, so a comes from the cache and keeps the time it had.
        Durations(src, cache, ("a.job", 42), ("b.job", 42));
        Write(src, "b.job", 20);
        var result = Cook(src, cache, new MemoryStore(), new NotingCooker(".job"));

        Assert.Equal(1, result.FromCache);
        var second = SavedDurations(src, cache);
        Assert.Equal(42, second["a.job"]);
        Assert.NotEqual(42, second["b.job"]);
        Assert.Empty(Directory.GetFiles(cache, "durations.json.*.tmp"));
    }

    [Fact]
    public void A_broken_durations_file_is_ignored_and_written_again()
    {
        string src = Temp();
        string cache = Temp();
        Write(src, "big.job", 300);
        Write(src, "small.job", 10);
        File.WriteAllText(Path.Combine(cache, "durations.json"), "{ \"not\": json");
        var cooker = new NotingCooker(".job");

        var result = Cook(src, cache, new MemoryStore(), cooker);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(["big.job", "small.job"], cooker.Started);
        Assert.Equal(["big.job", "small.job"], SavedDurations(src, cache).Keys.Order());
    }
}
