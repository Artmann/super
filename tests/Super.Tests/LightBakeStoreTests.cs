using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class LightBakeStoreTests
{
    private const string Crypt = "lighting/crypt.lightbake";

    private static readonly BakeInputs Saved = new(11UL, 22UL, 33UL, 44UL);

    [Fact]
    public void A_matching_header_is_fresh()
    {
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), TextWriter.Null);

        Assert.True(store.IsFresh(Crypt, Saved));
    }

    [Fact]
    public void Saved_quality_is_read_from_the_header()
    {
        const string Hall = "lighting/hall.lightbake";
        var store = new LightBakeStore(
            Files((Crypt, Bake(Saved, BakeQuality.Preview)), (Hall, Bake(Saved, BakeQuality.Final))), TextWriter.Null);

        Assert.Equal(BakeQuality.Preview, store.SavedQuality(Crypt));
        Assert.Equal(BakeQuality.Final, store.SavedQuality(Hall));
        Assert.Null(store.SavedQuality("lighting/missing.lightbake"));
    }

    [Fact]
    public void A_bake_made_from_other_inputs_is_not_fresh()
    {
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), TextWriter.Null);

        Assert.False(store.IsFresh(Crypt, Saved with { Settings = 45UL }));
    }

    [Fact]
    public void A_missing_file_is_not_fresh_and_logs_nothing()
    {
        var log = new StringWriter();
        var store = new LightBakeStore(Files(), log);

        Assert.False(store.IsFresh(Crypt, Saved));
        Assert.Null(store.Load(Crypt));
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void A_stale_bake_logs_once_naming_the_changes()
    {
        var log = new StringWriter();
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), log);
        var moved = Saved with { Lights = 23UL };

        for (int i = 0; i < 3; i++)
        {
            Assert.False(store.IsFresh(Crypt, moved));
        }

        string[] lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["The bake lighting/crypt.lightbake is stale, since its lights changed, so its area is lit live until it is baked again."],
            lines);
    }

    [Fact]
    public void Asking_quietly_answers_the_same_and_logs_nothing()
    {
        var log = new StringWriter();
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), log);

        Assert.True(store.IsFreshQuietly(Crypt, Saved));
        Assert.False(store.IsFreshQuietly(Crypt, Saved with { Lights = 23UL }));
        Assert.False(store.IsFreshQuietly("lighting/missing.lightbake", Saved));
        Assert.Equal("", log.ToString());

        // A quiet question doesn't use up the one report, so asking out loud still reports it.
        Assert.False(store.IsFresh(Crypt, Saved with { Lights = 23UL }));
        Assert.Contains("lighting/crypt.lightbake is stale", log.ToString());
    }

    [Fact]
    public void Each_stale_file_is_logged_once()
    {
        var log = new StringWriter();
        const string other = "lighting/yard.lightbake";
        var store = new LightBakeStore(Files((Crypt, Bake(Saved)), (other, Bake(Saved))), log);
        var changed = new BakeInputs(12UL, 22UL, 34UL, 44UL);

        store.IsFresh(Crypt, changed);
        store.IsFresh(other, changed);
        store.IsFresh(Crypt, changed);

        string[] lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "The bake lighting/crypt.lightbake is stale, since its draws and environment changed, so its area is lit live until it is baked again.",
                "The bake lighting/yard.lightbake is stale, since its draws and environment changed, so its area is lit live until it is baked again.",
            ],
            lines);
    }

    [Fact]
    public void The_header_is_read_once_per_file()
    {
        int reads = 0;
        byte[] bake = Bake(Saved);
        var store = new LightBakeStore(name =>
        {
            reads++;
            return name == Crypt ? bake : null;
        }, TextWriter.Null);

        store.IsFresh(Crypt, Saved);
        store.IsFresh(Crypt, Saved with { Draws = 1UL });
        store.IsFresh(Crypt, Saved);
        store.IsFresh("lighting/missing.lightbake", Saved);
        store.IsFresh("lighting/missing.lightbake", Saved);

        Assert.Equal(2, reads);
    }

    [Fact]
    public void Forget_reads_the_file_again()
    {
        var files = new Dictionary<string, byte[]> { [Crypt] = Bake(Saved) };
        var log = new StringWriter();
        var store = new LightBakeStore(name => files.GetValueOrDefault(name), log);
        var now = Saved with { Environment = 99UL };
        Assert.False(store.IsFresh(Crypt, now));

        // The bake is made again from what the frame has now, and the content reloads.
        files[Crypt] = Bake(now);
        Assert.False(store.IsFresh(Crypt, now));
        store.Forget();

        Assert.True(store.IsFresh(Crypt, now));
        // Stale again after another reload, it's logged again, since what was logged is forgotten too.
        files[Crypt] = Bake(Saved);
        store.Forget();
        Assert.False(store.IsFresh(Crypt, now));
        Assert.Equal(2, log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Each_forget_counts_a_reload()
    {
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), TextWriter.Null);
        Assert.Equal(0, store.Reloads);

        store.Forget();
        store.Forget();

        Assert.Equal(2, store.Reloads);
    }

    [Fact]
    public void Saved_gives_the_header_or_null_for_a_missing_file()
    {
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), TextWriter.Null);

        Assert.Equal(Saved, store.Saved(Crypt));
        Assert.Null(store.Saved("lighting/yard.lightbake"));
    }

    [Fact]
    public void A_damaged_header_is_not_fresh_and_says_why_once()
    {
        var log = new StringWriter();
        byte[] damaged = Bake(Saved);
        damaged[0] ^= 0xFF;
        var store = new LightBakeStore(Files((Crypt, damaged)), log);

        Assert.False(store.IsFresh(Crypt, Saved));
        Assert.False(store.IsFresh(Crypt, Saved));

        string[] lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string line = Assert.Single(lines);
        Assert.Equal(
            "The bake lighting/crypt.lightbake can't be read, so its area is lit live: The .lightbake magic is wrong: found 0x4B4142B3, expected 0x4B41424C.",
            line);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_file_that_fails_to_read_is_not_fresh_says_why_once_and_is_tried_again_after_forget(Type failure)
    {
        var log = new StringWriter();
        int reads = 0;
        bool failing = true;
        byte[] bake = Bake(Saved);
        var store = new LightBakeStore(
            name =>
            {
                reads++;
                if (failing)
                {
                    throw (Exception)Activator.CreateInstance(failure, "The file is in use.")!;
                }

                return name == Crypt ? bake : null;
            },
            log);

        Assert.False(store.IsFresh(Crypt, Saved));
        Assert.False(store.IsFresh(Crypt, Saved));

        Assert.Equal(1, reads);
        Assert.Equal(
            $"The bake lighting/crypt.lightbake can't be read, so its area is lit live: The file is in use.{Environment.NewLine}",
            log.ToString());

        // Once the content reloads, the file is read again and found.
        failing = false;
        store.Forget();
        Assert.True(store.IsFresh(Crypt, Saved));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Load_reads_the_whole_file()
    {
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), TextWriter.Null);

        var file = store.Load(Crypt);

        Assert.NotNull(file);
        Assert.Equal(Saved, file.Inputs);
        Assert.Equal(BakeQuality.Preview, file.Quality);
        var (bakeId, rect) = Assert.Single(file.Draws);
        Assert.Equal(7UL, bakeId);
        Assert.Equal(new LightmapRect(0, new Vector2(0.5f), new Vector2(0.25f)), rect);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Assert.Single(file.Irradiance!).Pixels);
    }

    // Ruling R1: the renderer asks the real store every frame, so once each file's header is read, asking again makes
    // no garbage, whether the bake is fresh, stale and already logged, or missing.
    [Fact]
    public void Asking_again_once_warm_allocates_nothing()
    {
        var store = new LightBakeStore(Files((Crypt, Bake(Saved))), TextWriter.Null);
        var stale = Saved with { Lights = 23UL };
        const string missing = "lighting/missing.lightbake";
        store.IsFresh(Crypt, Saved);
        store.IsFresh(Crypt, stale);
        store.IsFresh(missing, Saved);

        bool fresh = false;
        bool wasStale = true;
        bool found = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            fresh = store.IsFresh(Crypt, Saved);
            wasStale = store.IsFresh(Crypt, stale);
            found = store.IsFresh(missing, Saved);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
        Assert.True(fresh);
        Assert.False(wasStale);
        Assert.False(found);
    }

    [Fact]
    public void Forgetting_one_file_keeps_the_others()
    {
        const string Hall = "lighting/hall.lightbake";
        var reads = new Dictionary<string, int>();
        var files = new Dictionary<string, byte[]> { [Crypt] = Bake(Saved), [Hall] = Bake(Saved) };
        var log = new StringWriter();
        var store = new LightBakeStore(name =>
        {
            reads[name] = reads.GetValueOrDefault(name) + 1;
            return files.GetValueOrDefault(name);
        }, log);
        var changed = Saved with { Lights = 23UL };
        Assert.False(store.IsFresh(Crypt, changed));
        Assert.False(store.IsFresh(Hall, changed));
        int reloads = store.Reloads;

        files[Crypt] = Bake(changed);
        store.Forget(Crypt);

        Assert.True(store.IsFresh(Crypt, changed));
        Assert.False(store.IsFresh(Hall, changed));
        Assert.Equal(2, reads[Crypt]);
        Assert.Equal(1, reads[Hall]);
        Assert.Equal(reloads, store.Reloads);

        // Hall's stale report was kept, so it isn't logged again; Crypt's was dropped.
        Assert.False(store.IsFresh(Crypt, Saved with { Lights = 24UL }));
        string[] lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, line => line.StartsWith("The bake lighting/hall.lightbake is stale"));
    }

    [Fact]
    public void Forgotten_files_are_handed_over_once_for_the_renderer_to_let_go_of()
    {
        var store = new LightBakeStore(Files(), TextWriter.Null);
        Assert.Empty(store.TakeForgotten());

        store.Forget(Crypt);
        store.Forget("lighting/hall.lightbake");
        store.Forget(Crypt);

        Assert.Equal(["lighting/crypt.lightbake", "lighting/hall.lightbake"], store.TakeForgotten().Order());
        Assert.Empty(store.TakeForgotten());
    }

    private static Func<string, byte[]?> Files(params (string Name, byte[] Bytes)[] files)
    {
        var byName = files.ToDictionary(file => file.Name, file => file.Bytes);
        return name => byName.GetValueOrDefault(name);
    }

    // A one-page bake made from the inputs given, with one draw on its page.
    private static byte[] Bake(BakeInputs inputs, BakeQuality quality = BakeQuality.Preview) => new LightBakeFile(
        inputs, quality,
        [(7UL, new LightmapRect(0, new Vector2(0.5f), new Vector2(0.25f)))],
        [new LightBakePage(1, 1, LightBakePageFormat.Rgba16f, [1, 2, 3, 4, 5, 6, 7, 8])],
        [], [], null, null, null, [], new LightBakeReport([], 0f, 64, TimeSpan.FromSeconds(1))).ToBytes();
}
