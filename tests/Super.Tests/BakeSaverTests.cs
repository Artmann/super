using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class BakeSaverTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bake-saver-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _log = new();
    private readonly StringWriter _errors = new();
    private readonly ConcurrentQueue<string> _written = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Space_bake_is_written_to_the_first_source_root()
    {
        string root = Path.Combine(_folder, "content");
        var saver = new BakeSaver(root, null, _written.Enqueue, _log, _errors);
        byte[] bytes = Bake(12.3);

        saver.Done(new BakeDone("lighting/house-garden.lightbake", "house-garden", default, BakeQuality.Preview, bytes));

        string path = Path.Combine(root, "lighting", "house-garden.lightbake");
        Assert.True(Poll(() => File.Exists(path)), "the space's bake was never written");
        Assert.True(saver.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.Combine(root, "lighting")));
        Assert.Equal($"Baked house-garden (Preview) in 12.3 s: {path}{Environment.NewLine}", _log.ToString());
        Assert.Empty(_written);
        Assert.Equal("", _errors.ToString());
    }

    [Fact]
    public void Chunk_bake_goes_to_the_cache()
    {
        var cache = new BakeCache(Path.Combine(_folder, "cache"), TimeProvider.System);
        var saver = new BakeSaver(Path.Combine(_folder, "content"), cache, _written.Enqueue, _log, _errors);
        byte[] bytes = Bake(2);
        string file = "lighting/cottage/00000000000000ab.lightbake";

        saver.Done(new BakeDone(file, "cottage", default, BakeQuality.Final, bytes));

        Assert.True(Poll(() => !_written.IsEmpty), "the cache write was never reported");
        // The log line comes after the report, on the pool thread, so wait for the save to finish before reading it.
        Assert.True(saver.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal([file], _written);
        Assert.Equal(bytes, cache.Read(file));
        Assert.False(Directory.Exists(Path.Combine(_folder, "content")));
        string path = Path.Combine(_folder, "cache", "lighting", "cottage", "00000000000000ab.lightbake");
        Assert.Equal($"Baked cottage (Final) in 2.0 s: {path}{Environment.NewLine}", _log.ToString());
    }

    [Fact]
    public void A_bake_that_cant_be_written_says_so_in_one_line_and_keeps_the_next()
    {
        // A file where the lighting folder should be, so the space's bake can't be written.
        string root = Path.Combine(_folder, "content");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "lighting"), "in the way");
        var cache = new BakeCache(Path.Combine(_folder, "cache"), TimeProvider.System);
        var saver = new BakeSaver(root, cache, _written.Enqueue, _log, _errors);

        saver.Done(new BakeDone("lighting/house-garden.lightbake", "house-garden", default, BakeQuality.Preview, Bake(1)));
        saver.Done(new BakeDone("lighting/cottage/00000000000000ab.lightbake", "cottage", default, BakeQuality.Preview, Bake(1)));

        Assert.True(saver.Wait(TimeSpan.FromSeconds(5)));
        string[] errors = _errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(errors);
        Assert.StartsWith("Couldn't keep the bake of house-garden: ", errors[0]);
        Assert.StartsWith("Baked cottage (Preview)", _log.ToString());
    }

    [Fact]
    public void Bakes_are_kept_in_the_order_they_finished()
    {
        string root = Path.Combine(_folder, "content");
        var saver = new BakeSaver(root, null, _written.Enqueue, _log, _errors);
        byte[] preview = Bake(1);
        byte[] final = Bake(9);

        saver.Done(new BakeDone("lighting/hall.lightbake", "hall", default, BakeQuality.Preview, preview));
        saver.Done(new BakeDone("lighting/hall.lightbake", "hall", default, BakeQuality.Final, final));

        Assert.True(saver.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(final, File.ReadAllBytes(Path.Combine(root, "lighting", "hall.lightbake")));
        string[] lines = _log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Baked hall (Preview)", lines[0]);
        Assert.StartsWith("Baked hall (Final)", lines[1]);
    }

    private static bool Poll(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (done())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return done();
    }

    // A one-page bake that says it took the seconds given.
    private static byte[] Bake(double seconds) => new LightBakeFile(
        default, BakeQuality.Preview,
        [(7UL, new LightmapRect(0, new Vector2(0.5f), new Vector2(0.25f)))],
        [new LightBakePage(1, 1, LightBakePageFormat.Rgba16f, [1, 2, 3, 4, 5, 6, 7, (byte)seconds])],
        [], [], null, null, null, [], new LightBakeReport([], 0f, 64, TimeSpan.FromSeconds(seconds))).ToBytes();
}
