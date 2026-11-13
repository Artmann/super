using Microsoft.Extensions.Time.Testing;
using Rendition;

namespace Super.Tests;

public sealed class BakeCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bake-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Least_recently_read_bake_goes_first()
    {
        var cache = new BakeCache(_folder, _time, capacity: 30);
        Write(cache, "a.lightbake", 10, at: 1);
        Write(cache, "b.lightbake", 10, at: 2);
        Write(cache, "c.lightbake", 10, at: 3);
        _time.SetUtcNow(At(4));
        Assert.NotNull(cache.Read("a.lightbake"));

        Write(cache, "d.lightbake", 10, at: 5);

        Assert.Null(cache.Read("b.lightbake"));
        Assert.NotNull(cache.Read("a.lightbake"));
        Assert.NotNull(cache.Read("c.lightbake"));
        Assert.NotNull(cache.Read("d.lightbake"));
        Assert.Equal(30, cache.Size);
    }

    [Fact]
    public void Reading_gives_the_bytes_that_were_written()
    {
        var cache = new BakeCache(_folder, _time, capacity: 100);
        cache.Write("lighting/house/00000000000000ab.lightbake", [1, 2, 3]);

        Assert.Equal([1, 2, 3], cache.Read("lighting/house/00000000000000ab.lightbake"));
        // A cache made afterwards, as in the next session, has nothing in memory and reads the file.
        Assert.Equal([1, 2, 3], new BakeCache(_folder, _time, capacity: 100).Read("lighting/house/00000000000000ab.lightbake"));
        Assert.Null(cache.Read("lighting/house/missing.lightbake"));
    }

    [Fact]
    public void Writing_a_file_again_replaces_it_without_counting_it_twice()
    {
        var cache = new BakeCache(_folder, _time, capacity: 100);
        cache.Write("a.lightbake", new byte[10]);
        cache.Write("a.lightbake", [7, 7]);

        Assert.Equal([7, 7], cache.Read("a.lightbake"));
        Assert.Equal(2, cache.Size);
    }

    [Fact]
    public void Leftover_temp_files_do_not_count_and_are_not_read()
    {
        var cache = new BakeCache(_folder, _time, capacity: 100);
        cache.Write("a.lightbake", [1]);
        File.WriteAllBytes(Path.Combine(_folder, "b.lightbake.tmp"), new byte[50]);

        Assert.Equal(1, cache.Size);
        Assert.Null(cache.Read("b.lightbake"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Cache_never_passes_its_capacity(int seed)
    {
        const long Capacity = 100;
        var random = new Random(seed);
        var cache = new BakeCache(_folder, _time, Capacity);

        for (int i = 0; i < 40; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            string file = $"f{random.Next(8)}.lightbake";
            if (random.Next(3) == 0)
            {
                cache.Read(file);
            }
            else
            {
                cache.Write(file, new byte[random.Next(1, 60)]);
            }

            Assert.InRange(cache.Size, 0, Capacity);
        }
    }

    [Fact]
    public void A_bake_just_written_is_read_from_memory_once_whatever_happens_to_the_file()
    {
        var cache = new BakeCache(_folder, _time, capacity: 100);
        cache.Write("a.lightbake", [1, 2, 3]);
        File.Delete(Path.Combine(_folder, "a.lightbake"));

        Assert.Equal([1, 2, 3], cache.Read("a.lightbake"));
        Assert.Null(cache.Read("a.lightbake"));
    }

    [Fact]
    public void Only_the_last_few_bakes_written_are_held_in_memory()
    {
        var cache = new BakeCache(_folder, _time, capacity: 1000);
        for (int i = 0; i < 6; i++)
        {
            cache.Write($"f{i}.lightbake", [(byte)i]);
        }

        foreach (string file in Directory.GetFiles(_folder))
        {
            File.Delete(file);
        }

        Assert.Null(cache.Read("f0.lightbake"));
        Assert.Null(cache.Read("f1.lightbake"));
        Assert.Equal([5], cache.Read("f5.lightbake"));
    }

    [Fact]
    public async Task Reading_finishes_while_a_write_is_stuck_before_its_move()
    {
        var cache = new BakeCache(_folder, _time, capacity: 100);
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "old.lightbake"), [9, 9]);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache.BeforeMove = () =>
        {
            reached.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "The test never let the write go.");
        };
        var write = Task.Run(() => cache.Write("new.lightbake", [1, 2, 3]));
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "The write never reached its move.");

            var read = Task.Run(() => cache.Read("old.lightbake"));

            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal([9, 9], await read);
        }
        finally
        {
            release.Set();
            await write;
        }

        Assert.Equal([1, 2, 3], cache.Read("new.lightbake"));
    }

    [Fact]
    public void A_bake_read_from_memory_counts_as_used_for_the_next_trim()
    {
        var cache = new BakeCache(_folder, _time, capacity: 20);
        Write(cache, "a.lightbake", 10, at: 1);
        Write(cache, "b.lightbake", 10, at: 2);
        _time.SetUtcNow(At(3));
        Assert.NotNull(cache.Read("a.lightbake"));

        Write(cache, "c.lightbake", 10, at: 4);

        Assert.True(File.Exists(Path.Combine(_folder, "a.lightbake")));
        Assert.False(File.Exists(Path.Combine(_folder, "b.lightbake")));
    }

    [Fact]
    public void The_bake_just_written_is_never_the_one_trimmed_even_on_a_tie()
    {
        var cache = new BakeCache(_folder, _time, capacity: 10);
        Write(cache, "z.lightbake", 6, at: 1);

        // The same time and an earlier name: by time and name alone, the new one would go first.
        Write(cache, "a.lightbake", 6, at: 1);

        Assert.True(File.Exists(Path.Combine(_folder, "a.lightbake")));
        Assert.False(File.Exists(Path.Combine(_folder, "z.lightbake")));
    }

    [Fact]
    public void A_bake_is_read_even_when_its_use_time_cant_be_set()
    {
        var cache = new BakeCache(_folder, _time, capacity: 100);
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "a.lightbake");
        File.WriteAllBytes(path, [4, 5]);
        // The file goes between being read and being marked used, so setting its time fails.
        cache.AfterRead = () => File.Delete(path);

        Assert.Equal([4, 5], cache.Read("a.lightbake"));
    }

    [Fact]
    public void Temp_files_left_by_a_crash_are_swept_by_the_first_write_but_a_recent_one_is_kept()
    {
        Directory.CreateDirectory(_folder);
        string old = Path.Combine(_folder, "x.lightbake.0123.tmp");
        string recent = Path.Combine(_folder, "y.lightbake.4567.tmp");
        File.WriteAllBytes(old, new byte[50]);
        File.WriteAllBytes(recent, new byte[50]);
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        var cache = new BakeCache(_folder, _time, capacity: 100);

        cache.Write("a.lightbake", [1]);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void A_folder_with_a_trailing_separator_takes_names()
    {
        var cache = new BakeCache(_folder + Path.DirectorySeparatorChar, _time, capacity: 100);

        cache.Write("lighting/a.lightbake", [1, 2]);

        Assert.Equal([1, 2], cache.Read("lighting/a.lightbake"));
    }

    [Fact]
    public void Bake_larger_than_the_capacity_is_not_kept()
    {
        var cache = new BakeCache(_folder, _time, capacity: 10);
        Write(cache, "small.lightbake", 6, at: 1);

        Write(cache, "big.lightbake", 11, at: 2);

        Assert.Null(cache.Read("big.lightbake"));
        Assert.NotNull(cache.Read("small.lightbake"));
        Assert.Equal(6, cache.Size);
    }

    [Fact]
    public void A_name_outside_the_folder_is_refused()
    {
        var cache = new BakeCache(_folder, _time, capacity: 10);

        var error = Assert.Throws<ArgumentException>(() => cache.Write("../escape.lightbake", [1]));

        Assert.StartsWith("The bake name '../escape.lightbake' must stay inside the cache folder.", error.Message);
    }

    [Fact]
    public void Folder_for_each_platform()
    {
        Assert.Equal(
            @"C:\Users\ada\AppData\Local\reef\Bakes",
            BakeCache.FolderFor("reef", UserDataPlatform.Windows, Env(("LOCALAPPDATA", @"C:\Users\ada\AppData\Local"))));
        Assert.Equal(
            "/Users/ada/Library/Caches/reef/Bakes",
            BakeCache.FolderFor("reef", UserDataPlatform.MacOS, Env(("HOME", "/Users/ada"))));
        Assert.Equal(
            "/data/cache/reef/bakes",
            BakeCache.FolderFor("reef", UserDataPlatform.Linux, Env(("HOME", "/home/ada"), ("XDG_CACHE_HOME", "/data/cache/"))));
        Assert.Equal(
            "/home/ada/.cache/reef/bakes",
            BakeCache.FolderFor("reef", UserDataPlatform.Linux, Env(("HOME", "/home/ada"))));
    }

    [Fact]
    public void A_missing_local_app_data_says_which_variable_is_missing()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => BakeCache.FolderFor("reef", UserDataPlatform.Windows, Env(("APPDATA", @"C:\x"))));

        Assert.Contains("LOCALAPPDATA", error.Message);
    }

    [Fact]
    public void Damaged_cached_bake_is_baked_again()
    {
        var cache = new BakeCache(_folder, _time, capacity: 1_000_000);
        var log = new StringWriter();
        var store = new LightBakeStore(cache.Read, log);
        var inputs = new BakeInputs(11UL, 22UL, 33UL, 44UL);
        const string Damaged = "lighting/yard/00000000000000aa.lightbake";
        const string Other = "lighting/yard/00000000000000bb.lightbake";
        cache.Write(Damaged, [1, 2, 3, 4, 5]);
        cache.Write(Other, Bake(inputs));

        // Read twice: it is unreadable and logged once.
        Assert.False(store.IsFresh(Damaged, inputs));
        Assert.False(store.IsFresh(Damaged, inputs));
        Assert.True(store.IsFresh(Other, inputs));
        string[] lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        Assert.StartsWith($"The bake {Damaged} can't be read", line);

        // The bake is made again and written over it, and forgetting the one file reads it afresh.
        cache.Write(Damaged, Bake(inputs));
        Assert.False(store.IsFresh(Damaged, inputs));
        store.Forget(Damaged);

        Assert.True(store.IsFresh(Damaged, inputs));
        Assert.Single(log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    private static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private void Write(BakeCache cache, string file, int size, int at)
    {
        _time.SetUtcNow(At(at));
        cache.Write(file, new byte[size]);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    private static byte[] Bake(BakeInputs inputs) => new LightBakeFile(
        inputs, BakeQuality.Preview,
        [(7UL, new LightmapRect(0, new System.Numerics.Vector2(0.5f), new System.Numerics.Vector2(0.25f)))],
        [new LightBakePage(1, 1, LightBakePageFormat.Rgba16f, [1, 2, 3, 4, 5, 6, 7, 8])],
        [], [], null, null, null, [], new LightBakeReport([], 0f, 64, TimeSpan.FromSeconds(1))).ToBytes();
}
