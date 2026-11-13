using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>
/// The pieces of Super that turn background bakes on during play, tell the renderer about bakes written to the cache,
/// and say why nothing bakes on a GPU that can't trace. All three run without a window.
/// </summary>
public sealed class BakeOnArrivalGlueTests : IDisposable
{
    private static readonly BakedSpace Courtyard = new("courtyard", new Vector3(-5f, 0f, -5f), new Vector3(5f, 5f, 5f), 1f);

    // A room 20 m east of the courtyard, 4 m across in its own space.
    private static readonly BakedChunk Room = new(
        "room", Matrix4x4.CreateTranslation(20f, 0f, 0f), new Vector3(-2f, 0f, -2f), new Vector3(2f, 3f, 2f));

    private static readonly BakedChunk Cellar = Room with { Name = "cellar", Transform = Matrix4x4.CreateTranslation(40f, 0f, 0f) };

    private static readonly Vector3 InCourtyard = new(1f, 1f, 1f);
    private static readonly Vector3 InRoom = new(20.5f, 1f, 0f);
    private static readonly Vector3 InCellar = new(40.5f, 1f, 0f);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bake-glue-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Auto_bake_switch_applies_nothing_until_turned_on()
    {
        var graphics = new Graphics();
        var auto = new AutoBakeSwitch();

        Assert.False(auto.Changed(graphics));
        graphics.BakeBudget = 2f;
        Assert.False(auto.Changed(graphics));
        Assert.False(auto.IsOn);

        auto.TurnOn();

        Assert.True(auto.Changed(graphics));
    }

    [Fact]
    public void Auto_bake_switch_applies_again_only_when_bake_on_arrival_or_the_budget_changes()
    {
        var graphics = new Graphics();
        var auto = new AutoBakeSwitch();
        auto.TurnOn();

        Assert.True(auto.Changed(graphics));
        Assert.False(auto.Changed(graphics));

        // Other settings don't start the bake queue again.
        graphics.Vsync = false;
        graphics.MaxFramesPerSecond = 30;
        Assert.False(auto.Changed(graphics));

        graphics.BakeBudget = 2f;
        Assert.True(auto.Changed(graphics));
        Assert.False(auto.Changed(graphics));

        graphics.BakeOnArrival = false;
        Assert.True(auto.Changed(graphics));
        Assert.False(auto.Changed(graphics));

        // Back to the first pair is a change from the last one applied.
        graphics.BakeOnArrival = true;
        graphics.BakeBudget = null;
        Assert.True(auto.Changed(graphics));
    }

    [Fact]
    public void Chunk_bake_written_in_the_background_is_read_again_after_the_written_files_are_forgotten()
    {
        var cache = new BakeCache(Path.Combine(_folder, "cache"), TimeProvider.System);
        var written = new WrittenBakes();
        var log = new StringWriter();
        var saver = new BakeSaver(null, cache, written.Written, log, log);
        var store = new LightBakeStore(cache.Read, TextWriter.Null);
        const string file = "lighting/room/00000000000000ab.lightbake";

        saver.Done(new BakeDone(file, "room", default, BakeQuality.Preview, Bake(BakeQuality.Preview)));
        Assert.True(saver.Wait(TimeSpan.FromSeconds(5)));
        written.ForgetIn(store);
        store.TakeForgotten();
        Assert.Equal(BakeQuality.Preview, store.SavedQuality(file));

        saver.Done(new BakeDone(file, "room", default, BakeQuality.Final, Bake(BakeQuality.Final)));
        Assert.True(saver.Wait(TimeSpan.FromSeconds(5)));

        // Until the render thread forgets it, the store keeps the header it read.
        Assert.Equal(BakeQuality.Preview, store.SavedQuality(file));

        written.ForgetIn(store);

        Assert.Equal(BakeQuality.Final, store.SavedQuality(file));
        // The renderer is told to let go of that one bake on the GPU, not every bake.
        Assert.Equal([file], store.TakeForgotten());
        Assert.Equal(0, store.Reloads);

        // Each file is forgotten once.
        written.ForgetIn(store);
        Assert.Empty(store.TakeForgotten());
    }

    [Fact]
    public void Not_baking_notice_names_the_space_first_and_says_once()
    {
        var errors = new StringWriter();
        var notice = new NotBakingNotice(errors) { On = true };
        var bakes = Resolve(Courtyard, [Room], [Static(1, InCourtyard), Static(2, InRoom)]);

        notice.Check(bakes, Courtyard, [Room], bakedLightingOff: false, "the GPU can't trace rays");
        notice.Check(bakes, Courtyard, [Room], bakedLightingOff: false, "the GPU can't trace rays");

        Assert.Equal($"Not baking courtyard: the GPU can't trace rays{Environment.NewLine}", errors.ToString());
    }

    [Fact]
    public void Not_baking_notice_names_the_first_chunk_with_no_bake_and_none_on_its_way()
    {
        var errors = new StringWriter();
        var notice = new NotBakingNotice(errors) { On = true };
        // The room's bake is on its way, so the cellar is the first that wants one.
        var bakes = Resolve(null, [Room, Cellar], [Static(1, InRoom), Static(2, InCellar)],
            pending: file => file.StartsWith("lighting/room/", StringComparison.Ordinal));
        Assert.True(bakes.ChunkPending(0));

        notice.Check(bakes, null, [Room, Cellar], bakedLightingOff: false, "no ray tracing");

        Assert.Equal($"Not baking cellar: no ray tracing{Environment.NewLine}", errors.ToString());
    }

    [Fact]
    public void Not_baking_notice_says_nothing_when_off_when_baked_lighting_is_off_or_when_every_area_is_baked()
    {
        var errors = new StringWriter();
        var missing = Resolve(Courtyard, [Room], [Static(1, InCourtyard), Static(2, InRoom)]);
        var baked = Resolve(Courtyard, [Room], [Static(1, InCourtyard), Static(2, InRoom)], everyBakeFresh: true);

        new NotBakingNotice(errors).Check(missing, Courtyard, [Room], bakedLightingOff: false, "no ray tracing");
        var on = new NotBakingNotice(errors) { On = true };
        on.Check(missing, Courtyard, [Room], bakedLightingOff: true, "no ray tracing");
        on.Check(null, Courtyard, [Room], bakedLightingOff: false, "no ray tracing");
        on.Check(baked, Courtyard, [Room], bakedLightingOff: false, "no ray tracing");

        Assert.Equal("", errors.ToString());

        // None of those used up the one time it says why.
        on.Check(missing, Courtyard, [Room], bakedLightingOff: false, "no ray tracing");
        Assert.Equal($"Not baking courtyard: no ray tracing{Environment.NewLine}", errors.ToString());
    }

    private static BakeFrame Resolve(
        BakedSpace? space, BakedChunk[] chunks, DrawItem[] draws, Func<string, bool>? pending = null,
        bool everyBakeFresh = false)
    {
        IBakeStore store = everyBakeFresh ? new EveryBakeFresh() : NoBakes.Instance;
        var resolver = new BakeResolver(store);
        if (pending is not null)
        {
            // A chunk counts as on its way when its bake is fresh but not on the GPU yet.
            resolver = new BakeResolver(new FreshWhen(pending)) { ChunkResident = (_, _) => false };
        }

        var frame = new FrameView { BakedSpace = space, BakedChunks = chunks, Time = 1f };
        return resolver.Resolve(frame, draws, _ => new BakeSphere(Vector3.Zero, 0.5f), 42, new AnimatedLights(TextWriter.Null));
    }

    // An id's low 12 bits hold the part, so the instance number is moved above them.
    private static DrawItem Static(ulong instance, Vector3 at) =>
        new() { Transform = Matrix4x4.CreateTranslation(at), BakeId = BakeIds.Of(instance << 16, 0) };

    private static byte[] Bake(BakeQuality quality) => new LightBakeFile(
        default, quality,
        [(7UL, new LightmapRect(0, new Vector2(0.5f), new Vector2(0.25f)))],
        [new LightBakePage(1, 1, LightBakePageFormat.Rgba16f, [1, 2, 3, 4, 5, 6, 7, 8])],
        [], [], null, null, null, [], new LightBakeReport([], 0f, 64, TimeSpan.FromSeconds(1))).ToBytes();

    private sealed class EveryBakeFresh : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }

    private sealed class FreshWhen(Func<string, bool> fresh) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => fresh(file);
    }
}
