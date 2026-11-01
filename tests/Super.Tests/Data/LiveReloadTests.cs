using Super.Testing;

namespace Super.Tests;

public class LiveReloadTests
{
    public record Item(string Id, int Power);

    public record Recipe(string Id, Ref<Item> Makes);

    private static string Axe(int power) => $"{{ \"id\": \"axe\", \"power\": {power} }}";

    private sealed class Flaky(MemoryContent inner) : IContentFiles
    {
        public bool Locked { get; set; }

        public IReadOnlyList<string>? List(string folder) => inner.List(folder);

        public byte[] Read(string path) => Locked ? throw new IOException("In use.") : inner.Read(path);

        public bool HasModel(string path) => inner.HasModel(path);
    }

    private class Armory(Content content) : IGame
    {
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
    public void Headless_runs_do_not_notice_files_changing()
    {
        var files = new MemoryContent { ["items/axe.json"] = Axe(1) };
        using var run = HeadlessGame.Create<Armory>(1, services => services.AddContentFiles(files));

        files["items/axe.json"] = Axe(2);
        run.Ticks(5);

        Assert.Equal(1, run.Game.Items["axe"].Power);
    }

    [Fact]
    public void Only_a_folder_in_the_project_counts_as_source()
    {
        Assert.True(new ContentFolder(Path.Combine(Path.GetTempPath(), "game", "content")).IsSource);
        Assert.False(new ContentFolder(Path.Combine(AppContext.BaseDirectory, "content")).IsSource);
    }

    [Fact]
    public void Memory_content_never_goes_live()
    {
        var content = new Content(new MemoryContent());
        content.GoLive();
        Assert.False(content.IsLive);
    }

    [Fact]
    public void Saving_a_file_asks_for_a_reload_once_it_goes_quiet()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "items"));
        try
        {
            var seen = 0;
            using var watcher = new ContentWatcher(root, () => Interlocked.Increment(ref seen), TimeSpan.FromMilliseconds(50));
            File.WriteAllText(Path.Combine(root, "items", "axe.json"), "{}");
            File.WriteAllText(Path.Combine(root, "items", "axe.json"), "{ }");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref seen) == 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }

            Thread.Sleep(200);
            Assert.Equal(1, Volatile.Read(ref seen));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Schemas_being_written_do_not_ask_for_a_reload()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, ".schemas"));
        Directory.CreateDirectory(Path.Combine(root, "items"));
        try
        {
            var seen = 0;
            using var watcher = new ContentWatcher(root, () => Interlocked.Increment(ref seen), TimeSpan.FromMilliseconds(10));
            File.WriteAllText(Path.Combine(root, ".schemas", "items.schema.json"), "{}");
            File.WriteAllText(Path.Combine(root, "items", "notes.txt"), "not data");
            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref seen));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
