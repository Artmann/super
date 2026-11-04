using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Super.Cook.Tests;

/// <summary>The tests below watch real folders and wait on real time, so nothing else may run beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileWatchingCollection
{
    public const string Name = "File watching";
}

public sealed class WatcherPathTests
{
    [Theory]
    [InlineData("items/axe.json", true)]
    [InlineData(@"items\axe.json", true)]
    [InlineData("cook.json", true)]
    [InlineData(".schemas/item.json", false)]
    [InlineData(@"items\.axe.json.swp", false)]
    [InlineData("items/.git/HEAD", false)]
    public void Only_changes_outside_dot_files_and_dot_folders_matter(string relative, bool matters)
    {
        Assert.Equal(matters, CookWatcher.Matters(relative));
    }
}

[Collection(FileWatchingCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class WatcherTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(50);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "super-watch-" + Guid.NewGuid().ToString("N"));
    private readonly string _content;
    private readonly string _out;
    private readonly LockedLog _log = new();
    private readonly CookPipeline _pipeline = new(BuiltInCookers.All, new CookOptions());
    private readonly FolderStore _store;

    public WatcherTests()
    {
        _content = Path.Combine(_folder, "content");
        _out = Path.Combine(_folder, "out");
        Directory.CreateDirectory(Path.Combine(_content, "items"));
        File.WriteAllText(Path.Combine(_content, "items", "axe.json"), """{ "name": "Axe" }""");
        _store = new FolderStore(_out);
        Assert.True(_pipeline.Cook([new FolderRoot(_content)], _store).Succeeded);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    private CookWatcher Watch()
    {
        return new CookWatcher(_pipeline, [new FolderRoot(_content)], _store, _log, Quiet);
    }

    private string ManifestPath => Path.Combine(_out, ContentManifest.FileName);

    private ContentManifest Manifest()
    {
        return ContentManifest.Parse(File.ReadAllBytes(ManifestPath), ManifestPath);
    }

    private static int Count(string text, string part)
    {
        return text.Split(part).Length - 1;
    }

    private static string Hash(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Polls for at most 10 seconds. The watcher works on its own thread, so there is nothing to wait on but the result.</summary>
    private void Until(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!Try(done))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail($"Waited 10 s for {what}. The log says:\n{_log.Text}");
            }

            Thread.Sleep(20);
        }
    }

    /// <summary>The manifest may be in the middle of being replaced when it is read. That counts as not done yet.</summary>
    private static bool Try(Func<bool> done)
    {
        try
        {
            return done();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void Saving_a_file_recooks_it()
    {
        using var watcher = Watch();
        string changed = """{ "name": "Sharp axe" }""";
        Assert.NotEqual(Hash(changed), Manifest().Assets["items/axe.json"].Hash);

        File.WriteAllText(Path.Combine(_content, "items", "axe.json"), changed);

        Until(() => Manifest().Assets["items/axe.json"].Hash == Hash(changed), "the manifest to name the new axe.json");
        Until(() => _log.Text.Contains("Cooked."), "the log to say it cooked");
    }

    [Fact]
    public void Bad_save_keeps_old_manifest_and_logs()
    {
        using var watcher = Watch();
        byte[] before = File.ReadAllBytes(ManifestPath);
        string axe = Path.Combine(_content, "items", "axe.json");

        File.WriteAllText(axe, "{ nope");

        Until(() => _log.Text.Contains("Not cooked: 1 errors."), "the log to say it didn't cook");
        Assert.Contains($"{axe}(1): error SUPER001: ", _log.Text);
        Assert.Equal(before, File.ReadAllBytes(ManifestPath));
    }

    [Fact]
    public void Locked_file_retries_after_quiet()
    {
        using var watcher = Watch();
        string axe = Path.Combine(_content, "items", "axe.json");

        // The axe is held open by another program, so a cook started by a new sword can't read it.
        // Letting go of a file changes nothing on disk, so only a retry can finish the cook.
        using (File.Open(axe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            File.WriteAllText(Path.Combine(_content, "items", "sword.json"), """{ "name": "Sword" }""");
            // Saving one file makes a change event or two, so a cook or two. Only retries keep coming while nothing changes.
            Until(() => Count(_log.Text, "Trying again") >= 5, "the watcher to try again five times");
            Assert.False(Manifest().Assets.ContainsKey("items/sword.json"));
        }

        Until(() => Manifest().Assets.ContainsKey("items/sword.json"), "the retry to cook the sword");
    }

    /// <summary>A log the watcher's thread can write while the test reads it.</summary>
    private sealed class LockedLog : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }
    }
}
