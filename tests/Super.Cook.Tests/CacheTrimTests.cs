using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Super.Cook.Tests;

/// <summary>
/// Trimming the cook cache to its limit: least recently used first, never what the running cook used or another cook
/// may be writing, never a file the cache didn't write, and a file that's gone is a miss, never an error.
/// </summary>
public sealed class CacheTrimTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

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
        string folder = Path.Combine(Path.GetTempPath(), "super-trim-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes a file of <paramref name="size"/> bytes in the cache, last used <paramref name="hoursAgo"/> hours before <see cref="Now"/>.</summary>
    private static string File(string cache, string name, int size, double hoursAgo)
    {
        string path = Path.Combine(cache, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        System.IO.File.SetLastWriteTimeUtc(path, Now.AddHours(-hoursAgo));
        return path;
    }

    // Names as the cache writes them, from a label, so each test's files differ.

    private static string Hash(string label) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(label)));

    private static string ObjectName(string label)
    {
        string hash = Hash(label);
        return $"objects/{hash[..2]}/{hash}";
    }

    private static string Record(string label) => $"records/{Hash(label)}.json";

    private static string Step(string step, string label)
    {
        string hash = Hash(label);
        return $"steps/{step}/{hash[..2]}/{hash}";
    }

    private static string TempOf(string name, string label) => $"{name}.{Hash(label)[..32]}.tmp";

    private static CacheUsage Usage(params string[] used)
    {
        var usage = new CacheUsage(Now);
        foreach (string path in used)
        {
            usage.Wrote(path);
        }

        return usage;
    }

    [Fact]
    public void Deletes_the_least_recently_used_first_until_under_the_limit()
    {
        string cache = Temp();
        string oldest = File(cache, ObjectName("oldest"), 100, 30);
        string older = File(cache, Step("graph-run", "older"), 100, 20);
        string newer = File(cache, Record("newer"), 100, 10);
        string newest = File(cache, ObjectName("newest"), 100, 5);

        var trim = CacheFolder.Trim(cache, 250, Usage(), Now);

        Assert.False(System.IO.File.Exists(oldest));
        Assert.False(System.IO.File.Exists(older));
        Assert.True(System.IO.File.Exists(newer));
        Assert.True(System.IO.File.Exists(newest));
        Assert.Equal(new CacheTrim(200, 250, 2, 200, 0), trim);
    }

    [Fact]
    public void Keeps_everything_under_the_limit()
    {
        string cache = Temp();
        string a = File(cache, ObjectName("a"), 100, 30);
        string b = File(cache, Record("b"), 100, 20);

        var trim = CacheFolder.Trim(cache, 200, Usage(), Now);

        Assert.True(System.IO.File.Exists(a));
        Assert.True(System.IO.File.Exists(b));
        Assert.Equal(0, trim.Deleted);
        Assert.Equal(200, trim.Size);
    }

    [Fact]
    public void Never_deletes_what_this_cook_used_however_old()
    {
        string cache = Temp();
        string used = File(cache, ObjectName("used"), 100, 50);
        string unused = File(cache, ObjectName("unused"), 100, 1.5);

        var trim = CacheFolder.Trim(cache, 100, Usage(used), Now);

        Assert.True(System.IO.File.Exists(used));
        Assert.False(System.IO.File.Exists(unused));
        Assert.Equal(100, trim.Used);
        Assert.Null(trim.Warning());
    }

    [Fact]
    public void Never_deletes_what_was_written_after_the_cook_started()
    {
        // Another cook sharing the cache may have just written it and be about to use it.
        string cache = Temp();
        string another = File(cache, ObjectName("another"), 100, -0.01);
        string old = File(cache, ObjectName("old"), 100, 5);

        var trim = CacheFolder.Trim(cache, 1, Usage(), Now);

        Assert.True(System.IO.File.Exists(another));
        Assert.False(System.IO.File.Exists(old));
        Assert.Equal(100, trim.Size);
    }

    [Fact]
    public void At_the_same_time_a_record_goes_before_the_objects_and_steps_it_names()
    {
        // Compared directly, since the listing finds records before objects anyway, being a folder less deep.
        var at = Now.AddHours(-5);
        var record = new CacheFolder.Entry("r", "records", 10, at, false);
        var objectFile = new CacheFolder.Entry("o", "objects", 10, at, false);
        var step = new CacheFolder.Entry("s", "steps/mask", 10, at, false);
        var olderObject = objectFile with { LastWriteUtc = at.AddSeconds(-1) };

        List<CacheFolder.Entry> order = [objectFile, step, record, olderObject];
        order.Sort(CacheFolder.EvictionOrder);

        Assert.Equal([olderObject, record], order[..2]);
    }

    [Fact]
    public void Keeps_the_hash_index()
    {
        string cache = Temp();
        string hashes = File(cache, "hashes.json", 100, 50);

        var trim = CacheFolder.Trim(cache, 1, Usage(), Now);

        Assert.True(System.IO.File.Exists(hashes));
        Assert.Equal(100, trim.Size);
    }

    /// <summary>Files the cache doesn't write, in each of its folders, such as if --cache named a folder that holds more.</summary>
    private static List<string> Foreign(string cache, double hoursAgo)
    {
        string hash = Hash("foreign");
        return
        [
            File(cache, "mine.txt", 7, hoursAgo),
            File(cache, "notes/mine.txt", 7, hoursAgo),
            File(cache, "records/mine.txt", 7, hoursAgo),
            File(cache, $"records/{hash.ToUpperInvariant()}.json", 7, hoursAgo),
            File(cache, "objects/mine.txt", 7, hoursAgo),
            File(cache, $"objects/{hash[..2]}/mine.png", 7, hoursAgo),
            File(cache, $"objects/{(hash[..2] == "00" ? "ff" : "00")}/{hash}", 7, hoursAgo),
            File(cache, $"objects/{hash[..2]}/{hash}.bak", 7, hoursAgo),
            File(cache, "steps/mine.txt", 7, hoursAgo),
            File(cache, "steps/mask/mine.txt", 7, hoursAgo),
            File(cache, $"steps/mask/{hash[..2]}/mine.png", 7, hoursAgo),
            File(cache, $"steps/my steps/{hash[..2]}/{hash}", 7, hoursAgo),
            File(cache, $"objects/{hash[..2]}/mine.tmp", 7, hoursAgo),
            File(cache, $"objects/{hash[..2]}/{hash}.mine.tmp", 7, hoursAgo),
            File(cache, "hashes.json.bak", 7, hoursAgo),
        ];
    }

    [Fact]
    public void Trimming_never_deletes_or_counts_a_file_the_cache_didnt_write()
    {
        string cache = Temp();
        var foreign = Foreign(cache, 50);
        string ours = File(cache, ObjectName("ours"), 100, 50);

        var trim = CacheFolder.Trim(cache, 1, Usage(), Now);

        Assert.All(foreign, path => Assert.True(System.IO.File.Exists(path), path));
        Assert.False(System.IO.File.Exists(ours));
        Assert.Equal(new CacheTrim(0, 1, 1, 100, 0), trim);
    }

    [Fact]
    public void Deletes_temp_files_older_than_an_hour_and_keeps_younger_ones()
    {
        // A young one may be a write still going on in another cook.
        string cache = Temp();
        string stale = File(cache, TempOf(ObjectName("x"), "stale"), 100, 1.5);
        string young = File(cache, TempOf(ObjectName("y"), "young"), 100, 0.5);
        string staleIndex = File(cache, TempOf("hashes.json", "index"), 10, 1.5);

        var trim = CacheFolder.Trim(cache, 0, Usage(), Now);

        Assert.False(System.IO.File.Exists(stale));
        Assert.False(System.IO.File.Exists(staleIndex));
        Assert.True(System.IO.File.Exists(young));
        Assert.Equal(2, trim.Deleted);
        Assert.Equal(110, trim.Freed);
    }

    [Fact]
    public void No_limit_deletes_nothing_but_stale_temp_files()
    {
        string cache = Temp();
        string old = File(cache, ObjectName("old"), 1000, 5000);

        var trim = CacheFolder.Trim(cache, 0, Usage(), Now);

        Assert.True(System.IO.File.Exists(old));
        Assert.Equal(0, trim.Deleted);
        Assert.Equal("Cache 1000 B, no limit.", trim.Describe());
    }

    [Fact]
    public void A_cook_that_uses_more_than_the_limit_keeps_it_and_says_how_to_raise_it()
    {
        string cache = Temp();
        string a = File(cache, ObjectName("a"), 2048, 50);
        string b = File(cache, Record("b"), 1024, 50);

        var trim = CacheFolder.Trim(cache, 1024, Usage(a, b), Now);

        Assert.True(System.IO.File.Exists(a));
        Assert.True(System.IO.File.Exists(b));
        Assert.True(trim.CookAloneOverLimit);
        Assert.Equal(
            "This cook uses 3.0 KB of the cook cache, more than its limit of 1.0 KB, so the cache is kept over it. "
                + "Raise the limit with SUPER_CACHE_SIZE or --cache-limit, such as --cache-limit 8GB.",
            trim.Warning());
    }

    [Fact]
    public void A_cache_that_isnt_there_trims_to_nothing()
    {
        string cache = Path.Combine(Temp(), "gone");

        var trim = CacheFolder.Trim(cache, 1, Usage(), Now);

        Assert.Equal(new CacheTrim(0, 1, 0, 0, 0), trim);
    }

    [Fact]
    public void Trimming_outside_a_cook_keeps_a_file_used_ten_minutes_ago_that_looks_an_hour_old()
    {
        // A cook that started ten minutes ago didn't touch it, since it was then under an hour old.
        string cache = Temp();
        string usedByRunningCook = File(cache, ObjectName("running"), 100, 65 / 60.0);
        string old = File(cache, ObjectName("old"), 100, 2.5);

        var trim = CacheFolder.TrimNow(cache, 1, Now);

        Assert.True(System.IO.File.Exists(usedByRunningCook));
        Assert.False(System.IO.File.Exists(old));
        Assert.Equal(100, trim.Size);
    }

    [Fact]
    public void Using_a_file_that_vanished_is_not_an_error()
    {
        var usage = new CacheUsage(Now);

        usage.Used(Path.Combine(Temp(), "gone"), Now.AddDays(-1));
    }

    [Fact]
    public void Using_a_stale_file_marks_it_used_now_and_a_fresh_one_is_left_alone()
    {
        // The last-write time is the last use. A file used within the hour isn't written again, so a warm cook within the
        // hour writes nothing.
        string cache = Temp();
        string stale = File(cache, ObjectName("stale"), 1, 2);
        string fresh = File(cache, ObjectName("fresh"), 1, 0.5);
        var usage = new CacheUsage(Now);

        usage.Used(stale, Now.AddHours(-2));
        usage.Used(fresh, Now.AddHours(-0.5));

        Assert.Equal(Now, System.IO.File.GetLastWriteTimeUtc(stale));
        Assert.Equal(Now.AddHours(-0.5), System.IO.File.GetLastWriteTimeUtc(fresh));
        Assert.True(usage.Contains(stale));
        Assert.True(usage.Contains(fresh));
    }

    [Fact]
    public void Lists_the_size_of_each_part_and_files_the_cache_didnt_write_apart()
    {
        string cache = Temp();
        File(cache, ObjectName("a"), 300, 1);
        File(cache, ObjectName("b"), 200, 1);
        File(cache, Step("graph-run", "c"), 400, 1);
        File(cache, Record("d"), 10, 1);
        File(cache, "hashes.json", 5, 1);
        File(cache, "objects/mine.txt", 7, 1);

        var parts = CacheFolder.Parts(cache);

        Assert.Equal(
            [("objects", 500L, 2), ("steps/graph-run", 400L, 1), ("records", 10L, 1), (CacheFolder.NotTheCaches, 7L, 1), ("other", 5L, 1)],
            parts);
    }

    [Fact]
    public void Clearing_deletes_the_caches_files_and_the_folders_left_empty()
    {
        string cache = Temp();
        File(cache, ObjectName("a"), 300, 1);
        File(cache, Step("graph-run", "c"), 400, 1);
        File(cache, Record("d"), 10, 1);
        File(cache, "hashes.json", 5, 1);
        File(cache, TempOf(Record("e"), "temp"), 3, 0);

        var (files, bytes) = CacheFolder.Clear(cache);

        Assert.Equal((5, 718L), (files, bytes));
        Assert.Empty(Directory.GetFileSystemEntries(cache));
    }

    [Fact]
    public void Clearing_leaves_every_file_the_cache_didnt_write_and_the_folders_that_hold_them()
    {
        string cache = Temp();
        var foreign = Foreign(cache, 1);
        string ours = File(cache, ObjectName("ours"), 100, 1);
        string oursInForeignFolder = File(cache, ObjectName("foreign"), 100, 1);
        string emptied = Path.GetDirectoryName(ours)!;

        var (files, bytes) = CacheFolder.Clear(cache);

        Assert.Equal((2, 200L), (files, bytes));
        Assert.Equal(foreign.Order(StringComparer.Ordinal), Directory.GetFiles(cache, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.False(System.IO.File.Exists(oursInForeignFolder));
        Assert.False(Directory.Exists(emptied));
    }

    [Fact]
    public void A_content_folder_is_neither_cleared_nor_trimmed()
    {
        // Its objects are named as the cache's are, so the names alone can't protect it.
        string content = Temp();
        File(content, ContentManifest.FileName, 10, 50);
        string objectFile = File(content, ObjectName("texture"), 100, 50);
        string expected = $"{content} holds cooked content, with a content.manifest, not a cook cache, so it isn't cleared or trimmed. "
            + "Point --cache or SUPER_CACHE at the cache's own folder.";

        var clear = Assert.Throws<InvalidOperationException>(() => CacheFolder.Clear(content));
        var trim = Assert.Throws<InvalidOperationException>(() => CacheFolder.Trim(content, 1));

        Assert.Equal(expected, clear.Message);
        Assert.Equal(expected, trim.Message);
        Assert.True(System.IO.File.Exists(objectFile));
    }

    // Through a cook.

    private static CookResult Cook(string cache, string src, long? limit = null)
    {
        var options = new CookOptions(CacheFolder: cache) { CacheLimit = limit };
        return new CookPipeline(BuiltInCookers.All, options).Cook([new FolderRoot(src)], new MemoryStore());
    }

    /// <summary>Makes every file in the cache look last used <paramref name="hours"/> hours ago.</summary>
    private static void Age(string cache, double hours)
    {
        foreach (string path in Directory.GetFiles(cache, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hours));
        }
    }

    private static string[] Records(string cache) => Directory.GetFiles(Path.Combine(cache, "records"));

    [Fact]
    public void A_cook_over_the_limit_deletes_what_only_older_cooks_used_and_keeps_its_own()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        System.IO.File.WriteAllText(Path.Combine(src, "b.json"), """{ "b": 2 }""");
        Cook(cache, src);
        string[] both = Records(cache);
        System.IO.File.Delete(Path.Combine(src, "b.json"));
        Age(cache, 2);

        var result = Cook(cache, src, limit: 1);

        Assert.Equal(1, result.FromCache);
        string kept = Assert.Single(Records(cache));
        Assert.Contains(kept, both);
        var trim = Assert.IsType<CacheTrim>(result.Report.Trim);
        Assert.True(trim.Deleted >= 2, $"deleted {trim.Deleted}");

        // What the cook used is still a hit.
        var again = Cook(cache, src, limit: 1);
        Assert.Equal(1, again.FromCache);
        Assert.Equal(kept, Assert.Single(Records(cache)));
    }

    [Fact]
    public void A_cook_that_needs_more_than_the_limit_warns_once_and_keeps_its_files()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");

        var result = Cook(cache, src, limit: 1);

        var warning = Assert.Single(result.Problems);
        Assert.False(warning.IsError);
        Assert.StartsWith("This cook uses ", warning.Message);
        Assert.EndsWith("Raise the limit with SUPER_CACHE_SIZE or --cache-limit, such as --cache-limit 8GB.", warning.Message);
        Assert.Single(Records(cache));
        Assert.Equal(1, Cook(cache, src, limit: 1).FromCache);
    }

    [Fact]
    public void A_cook_whose_cache_is_a_content_folder_warns_and_trims_nothing()
    {
        string src = Temp();
        string content = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        File(content, ContentManifest.FileName, 10, 50);
        string objectFile = File(content, ObjectName("texture"), 100, 50);

        var result = Cook(content, src, limit: 1);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Cooked);
        var warning = Assert.Single(result.Problems);
        Assert.False(warning.IsError);
        Assert.Equal(
            $"{content} holds cooked content, with a content.manifest, not a cook cache, so it isn't cleared or trimmed. "
                + "Point --cache or SUPER_CACHE at the cache's own folder.",
            warning.Message);
        Assert.True(System.IO.File.Exists(objectFile));
    }

    [Fact]
    public void The_trim_shows_in_the_timings()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");

        var result = Cook(cache, src, limit: 1L << 30);

        Assert.Contains(result.Report.Steps, step => step.Step == "trim cache");
        Assert.Contains(result.Report.Summary(), line => line.StartsWith("Cache ", StringComparison.Ordinal) && line.EndsWith(" of 1.0 GB.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_warm_cook_within_the_hour_writes_nothing_to_the_cache()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        Cook(cache, src);
        Age(cache, 0.5);
        var before = Directory.GetFiles(cache, "*", SearchOption.AllDirectories).ToDictionary(path => path, System.IO.File.GetLastWriteTimeUtc);

        Assert.Equal(1, Cook(cache, src).FromCache);

        var after = Directory.GetFiles(cache, "*", SearchOption.AllDirectories).ToDictionary(path => path, System.IO.File.GetLastWriteTimeUtc);
        Assert.Equal(before, after);
    }

    [Fact]
    public void A_hit_marks_its_record_and_objects_used_now()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        Cook(cache, src);
        Age(cache, 2);
        DateTime started = DateTime.UtcNow;

        Assert.Equal(1, Cook(cache, src).FromCache);

        string[] used = [.. Records(cache), .. Directory.GetFiles(Path.Combine(cache, "objects"), "*", SearchOption.AllDirectories)];
        Assert.Equal(2, used.Length);
        Assert.All(used, path => Assert.True(System.IO.File.GetLastWriteTimeUtc(path) >= started.AddSeconds(-1), path));
    }

    [Fact]
    public void An_object_deleted_under_the_cache_is_a_miss_and_cooks_again()
    {
        // As when another cook trims the cache while this one runs.
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        Cook(cache, src);
        foreach (string path in Directory.GetFiles(Path.Combine(cache, "objects"), "*", SearchOption.AllDirectories))
        {
            System.IO.File.Delete(path);
        }

        var result = Cook(cache, src);

        Assert.True(result.Succeeded, string.Join("; ", result.Problems));
        Assert.Equal(1, result.Cooked);
        Assert.StartsWith("cached object gone", Assert.Single(result.Report.Files).Miss);
    }

    [Fact]
    public void A_real_cook_writes_only_names_that_clearing_deletes()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        Cook(cache, src);
        Assert.NotEmpty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));

        CacheFolder.Clear(cache);

        Assert.Empty(Directory.GetFileSystemEntries(cache));
    }

    [Fact]
    public void The_hash_index_forgets_files_that_are_gone_or_changed()
    {
        string src = Temp();
        string cache = Temp();
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 1 }""");
        System.IO.File.WriteAllText(Path.Combine(src, "b.json"), """{ "b": 2 }""");
        Cook(cache, src);
        Assert.Equal(2, Index(cache).Count(key => key.Contains(src, StringComparison.OrdinalIgnoreCase)));

        System.IO.File.Delete(Path.Combine(src, "b.json"));
        System.IO.File.WriteAllText(Path.Combine(src, "a.json"), """{ "a": 22 }""");
        Cook(cache, src);

        string key = Assert.Single(Index(cache), key => key.Contains(src, StringComparison.OrdinalIgnoreCase));
        var info = new FileInfo(Path.Combine(src, "a.json"));
        Assert.Equal($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", key);
    }

    private static List<string> Index(string cache)
    {
        var node = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(cache, "hashes.json")))!.AsObject();
        return [.. node.Select(pair => pair.Key)];
    }
}
