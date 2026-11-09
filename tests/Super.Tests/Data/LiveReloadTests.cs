using Super.Testing;

namespace Super.Tests;

public class LiveReloadTests
{
    public record Item(string Id, int Power);

    public record Recipe(string Id, Ref<Item> Makes);

    internal static string Axe(int power) => $"{{ \"id\": \"axe\", \"power\": {power} }}";

    private sealed class Flaky(MemoryContent inner) : IContentFiles
    {
        public bool Locked { get; set; }

        public ContentKind? KindOf(string name) => inner.KindOf(name);

        public IReadOnlyList<string>? List(string folder) => inner.List(folder);

        public byte[] Read(string name) => Locked ? throw new IOException("In use.") : inner.Read(name);
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private TimeSpan _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(_now);
            }
        }

        private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _due;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }

            public void FireIfDue(TimeSpan now)
            {
                if (_due is { } due && due <= now)
                {
                    _due = null;
                    callback(state);
                }
            }

            public void Dispose() => _due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Stands in for super-cook in watch mode, and remembers how it was started.</summary>
    internal sealed class FakeCook : ICookProcess
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A <see cref="Content.StartCook"/> that records every start instead of running the tool.</summary>
    internal sealed class CookStarts
    {
        public List<(DevSettings Dev, string Out, FakeCook Cook)> Calls { get; } = [];

        public ICookProcess Start(DevSettings dev, string outFolder)
        {
            var cook = new FakeCook();
            Calls.Add((dev, outFolder, cook));
            return cook;
        }
    }

    /// <summary>
    /// A game's folders on disk, as a build leaves them: sources, the cooked content folder, and
    /// optionally content.dev.json beside it.
    /// </summary>
    internal sealed class TempGame : IDisposable
    {
        public TempGame(bool dev, params string[] extraRoots)
        {
            Root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Sources = Path.Combine(Root, "sources");
            Cooked = Path.Combine(Root, "content");
            Directory.CreateDirectory(Path.Combine(Sources, "items"));
            File.WriteAllText(Path.Combine(Sources, "items", "axe.json"), Axe(1));
            if (dev)
            {
                string[] roots = [Sources, .. extraRoots.Select(root => Path.Combine(Root, root))];
                foreach (string root in roots)
                {
                    Directory.CreateDirectory(root);
                }

                new DevSettings(roots, Path.Combine(Root, "super-cook.dll"), "desktop").Write(Path.Combine(Root, DevSettings.FileName));
            }

            Folder = Cook();
        }

        public string Root { get; }

        public string Sources { get; }

        public string Cooked { get; }

        public CookedFolder Folder { get; }

        /// <summary>Saves a source file, as an editor would.</summary>
        public void Save(string name, string text) => File.WriteAllText(Path.Combine(Sources, name), text);

        /// <summary>Cooks the sources into the cooked folder, as watch mode does after a save.</summary>
        public CookedFolder Cook() => CookedTemp.Cook(Sources, Cooked);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    internal static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.True(condition(), "Timed out waiting.");
    }

    internal class Armory(Content content) : IGame
    {
        public Content Content => content;

        public Catalog<Item> Items { get; } = content.LoadAll<Item>("items");

        public void Load()
        {
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void A_reload_reaches_whoever_holds_the_catalog()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe(1) };
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");

        files["items/axe.json"] = Axe(2);
        Assert.True(content.Reload(TextWriter.Null));

        Assert.Equal(2, items["axe"].Power);
    }

    [Fact]
    public void A_reload_picks_up_new_and_removed_files()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe(1), ["items/saw.json"] = "{ \"id\": \"saw\", \"power\": 1 }" };
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");

        files.Remove("items/saw.json");
        files["items/hoe.json"] = "{ \"id\": \"hoe\", \"power\": 1 }";
        content.Reload(TextWriter.Null);

        Assert.False(items.Contains("saw"));
        Assert.True(items.Contains("hoe"));
    }

    [Fact]
    public void A_reload_tells_a_game_that_copied_something_out()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe(1) };
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");
        int copied = items["axe"].Power;
        items.OnChange(() => copied = items["axe"].Power);

        files["items/axe.json"] = Axe(5);
        content.Reload(TextWriter.Null);

        Assert.Equal(5, copied);
    }

    [Fact]
    public void A_mistake_keeps_the_old_data_and_says_what_went_wrong()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe(1) };
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");
        var log = new StringWriter();

        files["items/axe.json"] = "{ \"id\": \"axe\", \"power\": \"lots\" }";
        Assert.False(content.Reload(log));

        Assert.Equal(1, items["axe"].Power);
        Assert.Contains("items/axe.json:1: power must be a number", log.ToString());
    }

    [Fact]
    public void A_mistake_in_one_catalog_holds_back_all_of_them()
    {
        var files = new MemoryContent
        {
            ["items/axe.json"] = Axe(1),
            ["recipes/chop.json"] = "{ \"id\": \"chop\", \"makes\": \"axe\" }",
        };
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");
        content.LoadAll<Recipe>("recipes");

        files["items/axe.json"] = "{ \"id\": \"hatchet\", \"power\": 9 }";
        Assert.False(content.Reload(TextWriter.Null));

        Assert.True(items.Contains("axe"));
        Assert.False(items.Contains("hatchet"));
    }

    [Fact]
    public void A_file_still_being_saved_is_tried_again()
    {
        var memory = new MemoryContent { ["items/axe.json"] = Axe(1) };
        var files = new Flaky(memory);
        var content = new Content(files);
        var items = content.LoadAll<Item>("items");

        memory["items/axe.json"] = Axe(2);
        files.Locked = true;
        Assert.False(content.Reload(TextWriter.Null));
        Assert.True(content.ReloadWanted);

        files.Locked = false;
        Assert.True(content.Reload(TextWriter.Null));
        Assert.False(content.ReloadWanted);
        Assert.Equal(2, items["axe"].Power);
    }

    [Fact]
    public void Changes_land_between_ticks()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe(1) };
        using var run = HeadlessGame.Create<Armory>(1, services => services.AddContentFiles(files));
        run.Ticks(1);

        files["items/axe.json"] = Axe(2);
        run.ContentChanged();
        Assert.Equal(1, run.Game.Items["axe"].Power);

        run.Ticks(1);
        Assert.Equal(2, run.Game.Items["axe"].Power);
    }

    [Fact]
    public void A_reload_reads_the_manifest_again_after_a_cook()
    {
        using var game = new TempGame(dev: false);
        var content = new Content(game.Folder);
        var items = content.LoadAll<Item>("items");

        game.Save("items/axe.json", Axe(2));
        game.Cook();
        Assert.True(content.Reload(TextWriter.Null));

        Assert.Equal(2, items["axe"].Power);
    }

    [Fact]
    public void GoLive_starts_cook_with_dev_roots()
    {
        using var game = new TempGame(dev: true, "art");
        var starts = new CookStarts();
        var content = new Content(game.Folder) { StartCook = starts.Start };

        content.GoLive();
        try
        {
            var call = Assert.Single(starts.Calls);
            Assert.Equal([game.Sources, Path.Combine(game.Root, "art")], call.Dev.Roots);
            Assert.Equal(Path.Combine(game.Root, "super-cook.dll"), call.Dev.Tool);
            Assert.Equal("desktop", call.Dev.Target);
            Assert.Equal(Path.GetFullPath(game.Cooked), call.Out);
            Assert.True(content.IsLive);
        }
        finally
        {
            content.StopLive();
        }
    }

    [Fact]
    public void GoLive_writes_each_catalogs_schema_into_the_first_root_only()
    {
        using var game = new TempGame(dev: true, "art");
        var content = new Content(game.Folder) { StartCook = new CookStarts().Start };
        content.LoadAll<Item>("items");

        content.GoLive();
        content.StopLive();

        string schema = Path.Combine(game.Sources, ".schemas", "items.schema.json");
        Assert.Equal(content.SchemaText(typeof(Item)), File.ReadAllText(schema));
        Assert.False(Directory.Exists(Path.Combine(game.Root, "art", ".schemas")));
    }

    [Fact]
    public void No_dev_file_means_no_cook_and_no_watch()
    {
        using var game = new TempGame(dev: false);
        var starts = new CookStarts();
        var content = new Content(game.Folder) { StartCook = starts.Start };

        content.GoLive();

        Assert.Empty(starts.Calls);
        Assert.False(content.IsLive);
    }

    [Fact]
    public void StopLive_disposes_the_cook()
    {
        using var game = new TempGame(dev: true);
        var starts = new CookStarts();
        var content = new Content(game.Folder) { StartCook = starts.Start };
        content.GoLive();
        var cook = Assert.Single(starts.Calls).Cook;
        Assert.False(cook.Disposed);

        content.StopLive();

        Assert.True(cook.Disposed);
        Assert.False(content.IsLive);
    }

    [Fact]
    public void Going_live_twice_starts_one_cook()
    {
        using var game = new TempGame(dev: true);
        var starts = new CookStarts();
        var content = new Content(game.Folder) { StartCook = starts.Start };

        content.GoLive();
        content.GoLive();
        content.StopLive();

        Assert.Single(starts.Calls);
    }

    [Fact]
    public void Headless_runs_do_not_notice_files_changing()
    {
        using var game = new TempGame(dev: true);
        var starts = new CookStarts();
        using var run = HeadlessGame.Create<Armory>(1, services => services.AddContentFiles(game.Folder));
        run.Game.Content.StartCook = starts.Start;

        game.Save("items/axe.json", Axe(2));
        game.Cook();
        run.Ticks(5);

        // With content.dev.json there, only GoLive stands between this run and a cook and a watcher.
        Assert.Empty(starts.Calls);
        Assert.False(run.Game.Content.IsLive);
        Assert.Equal(1, run.Game.Items["axe"].Power);
    }

    [Fact]
    public void Memory_content_never_goes_live()
    {
        var starts = new CookStarts();
        var content = new Content(new MemoryContent()) { StartCook = starts.Start };

        content.GoLive();

        Assert.Empty(starts.Calls);
        Assert.False(content.IsLive);
    }

    [Fact]
    public void The_cook_runs_the_tool_in_watch_mode_over_every_root()
    {
        var dev = new DevSettings(["/game/content", "/art/content"], "/tools/super-cook.dll", "desktop");

        var arguments = CookProcess.Arguments(dev, "/game/bin/content");

        Assert.Equal(
            ["/tools/super-cook.dll", "--watch", "--content", "/game/content", "--content", "/art/content", "--out", "/game/bin/content", "--target", "desktop"],
            arguments);
    }

    [Fact]
    public void The_cook_passes_each_roots_globs_right_after_that_root()
    {
        var only = new Dictionary<string, string[]> { ["/art/content"] = ["props/**", "fox/**"] };
        var dev = new DevSettings(["/game/content", "/art/content"], "/tools/super-cook.dll", "desktop", only);

        var arguments = CookProcess.Arguments(dev, "/game/bin/content");

        Assert.Equal(
            ["/tools/super-cook.dll", "--watch", "--content", "/game/content", "--content", "/art/content", "--only", "props/**", "--only", "fox/**",
                "--out", "/game/bin/content", "--target", "desktop"],
            arguments);
    }

    [Fact]
    public void A_burst_of_manifest_writes_asks_once_after_the_folder_goes_quiet()
    {
        var clock = new FakeClock();
        var seen = 0;
        using var watcher = new ContentWatcher(Path.GetTempPath(), ContentManifest.FileName, () => seen++, TimeSpan.FromMilliseconds(100), clock);

        watcher.Saw("content.manifest");
        clock.Advance(TimeSpan.FromMilliseconds(60));
        watcher.Saw("content.manifest");
        clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(0, seen);

        clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.Equal(1, seen);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, seen);
    }

    [Fact]
    public void Only_files_the_filter_names_ask_for_a_reload()
    {
        var clock = new FakeClock();
        var seen = 0;
        using var watcher = new ContentWatcher(Path.GetTempPath(), ContentManifest.FileName, () => seen++, TimeSpan.FromMilliseconds(100), clock);

        watcher.Saw("items/axe.json");
        watcher.Saw("content.manifest.0123abcd.tmp");
        watcher.Saw("objects/ab/abcdef");
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, seen);

        watcher.Saw("content.manifest");
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, seen);
    }
}

/// <summary>Real file watchers, real time and folders the whole process shares, so these run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveReloadCollection
{
    public const string Name = "Live reload";
}

/// <summary>The content folder beside the test program is shared by the whole process, so these run alone.</summary>
[Collection(LiveReloadCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class BesideProgramTests
{
    [Fact]
    public void A_folder_shipped_beside_the_program_never_goes_live()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "content");
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, DevSettings.FileName)), "The test needs no content.dev.json beside the program.");
        bool made = !Directory.Exists(beside);
        Directory.CreateDirectory(beside);
        try
        {
            var starts = new LiveReloadTests.CookStarts();
            var content = new Content(new CookedFolder(beside)) { StartCook = starts.Start };

            content.GoLive();

            Assert.Empty(starts.Calls);
            Assert.False(content.IsLive);
        }
        finally
        {
            if (made)
            {
                Directory.Delete(beside, recursive: true);
            }
        }
    }
}

/// <summary>Live reload against real folders, with a real watcher and the cook done in the test.</summary>
[Collection(LiveReloadCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class LiveReloadWatchTests
{
    [Fact]
    public void Manifest_change_reloads_catalog()
    {
        using var game = new LiveReloadTests.TempGame(dev: true);
        var starts = new LiveReloadTests.CookStarts();
        using var run = HeadlessGame.Create<LiveReloadTests.Armory>(1, services => services.AddContentFiles(game.Folder));
        run.Game.Content.StartCook = starts.Start;
        run.Game.Content.GoLive();
        bool changed = false;
        run.Game.Items.OnChange(() => changed = true);

        game.Save("items/axe.json", LiveReloadTests.Axe(2));
        game.Cook();
        LiveReloadTests.WaitFor(() =>
        {
            run.Ticks(1);
            return run.Game.Items["axe"].Power == 2;
        });

        Assert.True(changed);
        Assert.Single(starts.Calls);
    }

    [Fact]
    public void A_real_watcher_hears_the_first_manifest_moved_into_place()
    {
        // With no manifest yet, the move is only a rename: nothing named content.manifest is created, changed or deleted.
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        string manifest = Path.Combine(root, ContentManifest.FileName);
        try
        {
            var seen = 0;
            using var watcher = new ContentWatcher(root, ContentManifest.FileName, () => Interlocked.Increment(ref seen), TimeSpan.FromMilliseconds(50));
            string temp = manifest + ".0123abcd.tmp";
            File.WriteAllText(temp, "new");
            File.Move(temp, manifest, overwrite: true);

            LiveReloadTests.WaitFor(() => Volatile.Read(ref seen) >= 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
