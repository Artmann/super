using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class BakeResolverTests
{
    private static readonly BakedSpace Courtyard = new("courtyard", new Vector3(-5f, 0f, -5f), new Vector3(5f, 5f, 5f), 1f);

    // Two rooms 20 m and 40 m east of the courtyard, each 4 m across in its own space.
    private static readonly BakedChunk Room = new(
        "room", Matrix4x4.CreateTranslation(20f, 0f, 0f), new Vector3(-2f, 0f, -2f), new Vector3(2f, 3f, 2f));

    private static readonly BakedChunk FarRoom = Room with { Name = "far", Transform = Matrix4x4.CreateTranslation(40f, 0f, 0f) };

    private static readonly Vector3 InCourtyard = new(1f, 1f, 1f);
    private static readonly Vector3 InRoom = new(20.5f, 1f, 0f);

    // Detail 0 is a ball 1 m west of a model's origin and detail 1 one 4 m east of it.
    private static BakeSphere TwoParts(DetailHandle detail) =>
        new(detail.Index == 0 ? new Vector3(-1f, 0f, 0f) : new Vector3(4f, 0f, 0f), 0.5f);

    [Fact]
    public void Resolving_a_frame_again_allocates_nothing()
    {
        var at = new Vector3(20f, 1f, 0f);
        BakedChunk[] chunks = [Room, FarRoom];
        DirectionalLight[] suns = [Sun(LightMode.Baked)];
        // One Baked light over the space, which the fresh bake drops, and a Mixed one in the room, which stays.
        LocalLight[] lights = [Point(1, InCourtyard, LightMode.Baked), Point(2, InRoom, LightMode.Mixed)];
        // A model's parts with another model between them, and one static draw in the courtyard.
        DrawItem[] draws = [Part(1, 0, at), Static(2, InRoom), Part(1, 1, at), Static(3, InCourtyard)];
        var frame = new FrameView { BakedSpace = Courtyard, BakedChunks = chunks, Lights = suns, LocalLights = lights, Time = 1f };
        var animated = new AnimatedLights(TextWriter.Null);
        var resolver = new BakeResolver(new EveryBakeFresh());
        Func<DetailHandle, BakeSphere> bounds = TwoParts;

        for (int i = 0; i < 3; i++)
        {
            resolver.Resolve(frame, draws, bounds, 42, animated);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        BakeFrame? resolved = null;
        for (int i = 0; i < 10; i++)
        {
            resolved = resolver.Resolve(frame, draws, bounds, 42, animated);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
        // The path that ran is the one with chunks, static draws and dropped lights.
        Assert.True(resolved!.SpaceFresh);
        Assert.Equal([true, true], resolved.ChunkFresh);
        Assert.Equal([0], resolved.DroppedLocalLights);
        Assert.Equal([0], resolved.DroppedSuns);
    }

    [Fact]
    public void Resolving_a_frame_with_chunks_again_allocates_nothing()
    {
        // Two copies of the room, whose bake is on the GPU, and the far room, whose bake is still on its way. Each
        // chunk has a Baked light: the rooms' are dropped, and the far room's stays while its bake is pending.
        var nextDoor = Room with { Transform = Matrix4x4.CreateTranslation(30f, 0f, 0f) };
        BakedChunk[] chunks = [Room, nextDoor, FarRoom];
        LocalLight[] lights =
        [
            Point(1, InRoom, LightMode.Baked), Point(2, InRoom + new Vector3(10f, 0f, 0f), LightMode.Baked),
            Point(3, InRoom + new Vector3(20f, 0f, 0f), LightMode.Baked),
        ];
        DrawItem[] draws = [Static(1, InRoom), Static(1, InRoom + new Vector3(10f, 0f, 0f)), Static(2, InRoom + new Vector3(20f, 0f, 0f))];
        var frame = new FrameView { BakedChunks = chunks, LocalLights = lights, Time = 1f };
        var animated = new AnimatedLights(TextWriter.Null);
        int asked = 0;
        var resolver = new BakeResolver(new EveryBakeFresh())
        {
            ChunkResident = (file, _) =>
            {
                asked++;
                return file.StartsWith("lighting/room/", StringComparison.Ordinal);
            },
        };
        Func<DetailHandle, BakeSphere> bounds = TwoParts;

        for (int i = 0; i < 3; i++)
        {
            resolver.Resolve(frame, draws, bounds, 42, animated);
        }

        asked = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        BakeFrame? resolved = null;
        for (int i = 0; i < 10; i++)
        {
            resolved = resolver.Resolve(frame, draws, bounds, 42, animated);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
        // Every fresh chunk was asked whether its bake is on the GPU, every frame.
        Assert.Equal(30, asked);
        Assert.Equal([true, true, false], resolved!.ChunkFresh);
        Assert.False(resolved.ChunkPending(0));
        Assert.False(resolved.ChunkPending(1));
        Assert.True(resolved.ChunkPending(2));
        Assert.Equal([0, 1], resolved.DroppedLocalLights);
    }

    [Fact]
    public void Owners_gives_each_draw_the_chunk_that_owns_its_model()
    {
        var resolver = new BakeResolver(NoBakes.Instance);
        var animated = new AnimatedLights(TextWriter.Null);
        var inFarRoom = new Vector3(40.5f, 1f, 0f);
        // A model in the room, a moving draw, a model in the courtyard, the far room's and another in the room.
        DrawItem[] draws =
        [
            Static(1, InRoom), new DrawItem { Transform = Matrix4x4.CreateTranslation(InRoom) },
            Static(2, InCourtyard), Static(3, inFarRoom), Static(4, InRoom),
        ];
        var frame = new FrameView { BakedSpace = Courtyard, BakedChunks = new[] { Room, FarRoom } };

        resolver.Resolve(frame, draws, TwoParts, 42, animated);

        Assert.Equal([0, BakeAreas.None, BakeAreas.None, 1, 0], resolver.Owners.ToArray());

        // A frame with no chunks owns nothing, though the last one did.
        resolver.Resolve(new FrameView { BakedSpace = Courtyard }, draws, TwoParts, 42, animated);

        Assert.Equal(
            [BakeAreas.None, BakeAreas.None, BakeAreas.None, BakeAreas.None, BakeAreas.None], resolver.Owners.ToArray());
    }

    [Fact]
    public void Chunk_whose_bake_is_missing_is_neither_fresh_nor_pending()
    {
        var saved = new SavedBakes();
        bool askedAboutMissing = false;
        var resolver = new BakeResolver(saved)
        {
            ChunkResident = (_, _) =>
            {
                askedAboutMissing = true;
                return false;
            },
        };
        var frame = new FrameView { BakedChunks = [Room], LocalLights = [Point(1, InRoom, LightMode.Baked)], Time = 1f };

        var resolved = resolver.Resolve(frame, [Static(1, InRoom)], TwoParts, 42, new AnimatedLights(TextWriter.Null));

        Assert.Equal([false], resolved.ChunkFresh);
        Assert.False(resolved.ChunkPending(0));
        Assert.False(askedAboutMissing);
        Assert.Empty(resolved.DroppedLocalLights);
    }

    [Fact]
    public void A_resolver_gives_the_same_frame_as_BakeFreshness()
    {
        var at = new Vector3(20f, 1f, 0f);
        var other = new Vector3(19f, 1f, 1f);
        var wide = Courtyard with { Max = new Vector3(20.6f, 5f, 5f) };
        var saved = new SavedBakes();
        DrawItem[] interleaved = [Part(1, 0, at), Part(2, 0, other), Part(1, 1, at), Part(2, 1, other)];
        DrawItem[] ordered = [Part(1, 0, at), Part(1, 1, at), Part(2, 0, other), Part(2, 1, other)];
        LocalLight[] lights =
        [
            Point(1, InCourtyard, LightMode.Baked),
            Point(2, InCourtyard + Vector3.UnitX, LightMode.Mixed),
            Point(3, InRoom, LightMode.Baked),
            Point(4, new Vector3(-100f, 1f, 0f), LightMode.Baked),
            Point(5, InRoom, LightMode.Realtime),
        ];
        DirectionalLight[] twoSuns = [Sun(LightMode.Baked), Sun(LightMode.Mixed)];

        // The bigger frames come first, so the resolver has more to forget when the smaller ones follow.
        var first = Scenario(Courtyard, [Room, FarRoom], twoSuns, lights, interleaved, new EveryBakeFresh());
        saved.Save("lighting/courtyard.lightbake", BakeFreshness.Resolve(
            first.Frame, first.Draws, TwoParts, 42, new AnimatedLights(TextWriter.Null), NoBakes.Instance).Space!.Value);
        var scenarios = new[]
        {
            first,
            Scenario(Courtyard, [Room, FarRoom], twoSuns, lights, ordered, new EveryBakeFresh()),
            Scenario(Courtyard, [Room, FarRoom], twoSuns, lights, interleaved, saved),
            Scenario(Courtyard, [Room], [twoSuns[0]], lights[..1], ordered[..2], new EveryBakeFresh()),
            Scenario(wide, [Room], [], lights, ordered, NoBakes.Instance),
            Scenario(null, [Room], [], lights, interleaved, new EveryBakeFresh()),
            Scenario(Courtyard, [], twoSuns, [], [], new EveryBakeFresh()),
            Scenario(null, [], twoSuns, lights, ordered, new EveryBakeFresh()),
            Scenario(Courtyard, [Room, FarRoom], twoSuns, lights, interleaved, new EveryBakeFresh()),
        };

        var resolver = new BakeResolver(NoBakes.Instance);
        for (int s = 0; s < scenarios.Length; s++)
        {
            var scenario = scenarios[s];
            var expected = BakeFreshness.Resolve(
                scenario.Frame, scenario.Draws, TwoParts, 42, new AnimatedLights(TextWriter.Null), scenario.Store);
            resolver.Store = scenario.Store;
            var actual = resolver.Resolve(
                scenario.Frame, scenario.Draws, TwoParts, 42, new AnimatedLights(TextWriter.Null));

            string where = $"scenario {s}";
            Assert.True(expected.SpaceFresh == actual.SpaceFresh, where);
            Assert.True(expected.Space == actual.Space, where);
            Assert.True(expected.Chunks.SequenceEqual(actual.Chunks), where);
            Assert.True(expected.ChunkFresh.SequenceEqual(actual.ChunkFresh), where);
            Assert.True(expected.DroppedLocalLights.SequenceEqual(actual.DroppedLocalLights), where);
            Assert.True(expected.DroppedSuns.SequenceEqual(actual.DroppedSuns), where);
            for (int key = 0; key <= 5; key++)
            {
                Assert.True(
                    expected.ModeOf(new LightKey(false, key)) == actual.ModeOf(new LightKey(false, key)), $"{where}, light {key}");
                Assert.True(
                    expected.ModeOf(new LightKey(true, key)) == actual.ModeOf(new LightKey(true, key)), $"{where}, sun {key}");
            }
        }

        // The scenarios must not all come to the same thing, or the comparison would prove little.
        Assert.NotEmpty(BakeFreshness.Resolve(
            first.Frame, first.Draws, TwoParts, 42, new AnimatedLights(TextWriter.Null), first.Store).DroppedLocalLights);
    }

    [Fact]
    public void A_frame_with_no_areas_gives_the_shared_empty_frame_after_one_with_areas()
    {
        var resolver = new BakeResolver(new EveryBakeFresh());
        var animated = new AnimatedLights(TextWriter.Null);
        var busy = new FrameView { BakedSpace = Courtyard, Lights = [Sun(LightMode.Baked)], LocalLights = [Point(1, InCourtyard, LightMode.Baked)] };

        var withAreas = resolver.Resolve(busy, [], TwoParts, 42, animated);
        Assert.Equal([0], withAreas.DroppedLocalLights);

        var empty = resolver.Resolve(default, [], TwoParts, 42, animated);

        Assert.Null(empty.Space);
        Assert.Empty(empty.DroppedLocalLights);
        Assert.Empty(empty.DroppedSuns);
        Assert.Empty(empty.Chunks);
    }

    [Fact]
    public void Each_area_is_hashed_at_its_own_texel_density()
    {
        var resolver = new BakeResolver(NoBakes.Instance);
        var animated = new AnimatedLights(TextWriter.Null);
        DrawItem[] draws = [Static(1, InCourtyard), Static(2, InRoom)];
        var denseCourtyard = Courtyard with { TexelsPerMetre = 40 };
        var denseRoom = Room with { TexelsPerMetre = 40 };
        BakeInputs Space(BakedSpace space, BakedChunk room) => resolver.Resolve(
            new FrameView { BakedSpace = space, BakedChunks = new[] { room } }, draws, TwoParts, 42, animated).Space!.Value;
        BakeInputs Chunk(BakedSpace space, BakedChunk room) => resolver.Resolve(
            new FrameView { BakedSpace = space, BakedChunks = new[] { room } }, draws, TwoParts, 42, animated).Chunks[0];

        var plain = Space(Courtyard, Room);
        var plainRoom = Chunk(Courtyard, Room);

        // Only the settings hash moves, and only for the area whose density changed.
        Assert.Equal(plain with { Settings = Space(denseCourtyard, Room).Settings }, Space(denseCourtyard, Room));
        Assert.NotEqual(plain.Settings, Space(denseCourtyard, Room).Settings);
        Assert.Equal(plainRoom, Chunk(denseCourtyard, Room));
        Assert.NotEqual(plainRoom.Settings, Chunk(Courtyard, denseRoom).Settings);
        Assert.Equal(plain.Settings, Space(Courtyard, denseRoom).Settings);

        // And it's the same hash a bake made at that density is saved with.
        Assert.Equal(
            BakeInputHash.Space([], [], denseCourtyard, 42, null, new BakeSettings(40)).Settings,
            Space(denseCourtyard, Room).Settings);
    }

    [Fact]
    public void Stale_chunk_bake_that_is_saved_fades_in_once_it_reaches_the_GPU()
    {
        var store = new Switch();
        bool resident = false;
        var resolver = new BakeResolver(store) { FadeSeconds = 1f, ChunkResident = (_, _) => resident };
        var animated = new AnimatedLights(TextWriter.Null);
        DrawItem[] draws = [Static(1, InRoom)];
        BakeFrame At(float time) => resolver.Resolve(
            new FrameView { BakedChunks = [Room], LocalLights = [Point(1, InRoom, LightMode.Baked)], Time = time },
            draws, TwoParts, 42, animated);

        var stale = At(10f);
        Assert.Equal(0f, stale.ChunkFade(0));
        Assert.Empty(stale.FadingLocalLights);
        Assert.Empty(stale.DroppedLocalLights);

        // Saved, but still on its way to the GPU: drawn live, and the fade hasn't started.
        store.Fresh = true;
        var pending = At(11f);
        Assert.True(pending.ChunkPending(0));
        Assert.Equal(0f, pending.ChunkFade(0));
        Assert.Empty(pending.FadingLocalLights);

        // On the GPU at 20 s: the fade starts there, not when the bake was saved.
        resident = true;
        var arrived = At(20f);
        Assert.Equal(0f, arrived.ChunkFade(0));
        Assert.Equal((0, 0f), Assert.Single(arrived.FadingLocalLights));
        Assert.Empty(arrived.DroppedLocalLights);
        Assert.Equal(LightMode.Baked, arrived.ModeOf(new LightKey(false, 1)));

        // Halfway, smoothstep gives a half too: 0.25 * (3 - 1).
        var half = At(20.5f);
        Assert.Equal(0.5f, half.ChunkFade(0), 1e-6f);
        Assert.Equal((0, 0.5f), Assert.Single(half.FadingLocalLights));

        var whole = At(21f);
        Assert.Equal(1f, whole.ChunkFade(0));
        Assert.Empty(whole.FadingLocalLights);
        Assert.Equal([0], whole.DroppedLocalLights);
    }

    [Fact]
    public void Space_bake_fading_in_is_at_the_smoothstep_of_the_time_since_it_arrived()
    {
        var store = new Switch();
        var resolver = new BakeResolver(store) { FadeSeconds = 2f };
        var animated = new AnimatedLights(TextWriter.Null);

        SpaceAt(resolver, animated, 1f);
        store.Fresh = true;
        SpaceAt(resolver, animated, 3f);
        var quarter = SpaceAt(resolver, animated, 3.5f);

        // A quarter of the way through: 0.25² * (3 - 2 * 0.25) = 0.0625 * 2.5.
        Assert.Equal(0.15625f, quarter.SpaceFade, 1e-6f);
        Assert.Single(quarter.FadingSuns);
        Assert.Equal(0.15625f, quarter.FadingSuns[0].Fade, 1e-6f);
        Assert.Equal(0.15625f, Assert.Single(quarter.FadingLocalLights).Fade, 1e-6f);
        Assert.Empty(quarter.DroppedSuns);
        Assert.Empty(quarter.DroppedLocalLights);
    }

    [Fact]
    public void Clock_that_goes_back_counts_as_the_bake_arrived()
    {
        var store = new Switch();
        var resolver = new BakeResolver(store) { FadeSeconds = 1f };
        var animated = new AnimatedLights(TextWriter.Null);

        SpaceAt(resolver, animated, 5f);
        store.Fresh = true;
        Assert.Equal(0f, SpaceAt(resolver, animated, 5f).SpaceFade);
        var back = SpaceAt(resolver, animated, 4.9f);

        Assert.Equal(1f, back.SpaceFade);
        Assert.Equal([0], back.DroppedLocalLights);
        Assert.Empty(back.FadingLocalLights);
    }

    [Fact]
    public void Area_that_leaves_the_frame_shows_its_bake_at_once_when_it_comes_back()
    {
        var store = new Switch();
        var resolver = new BakeResolver(store) { FadeSeconds = 1f };
        var animated = new AnimatedLights(TextWriter.Null);

        // Drawn live, then a frame that names only the room, then the courtyard again with its bake saved.
        SpaceAt(resolver, animated, 1f);
        resolver.Resolve(new FrameView { BakedChunks = [Room], Time = 2f }, [], TwoParts, 42, animated);
        store.Fresh = true;
        var back = SpaceAt(resolver, animated, 3f);

        Assert.Equal(1f, back.SpaceFade);
        Assert.Equal([0], back.DroppedLocalLights);

        // A frame that names no area at all forgets it too.
        store.Fresh = false;
        SpaceAt(resolver, animated, 4f);
        resolver.Resolve(new FrameView { Time = 5f }, [], TwoParts, 42, animated);
        store.Fresh = true;

        Assert.Equal(1f, SpaceAt(resolver, animated, 6f).SpaceFade);
    }

    [Fact]
    public void Zero_fade_seconds_shows_an_arriving_bake_at_once()
    {
        var store = new Switch();
        var resolver = new BakeResolver(store) { FadeSeconds = 0f };
        var animated = new AnimatedLights(TextWriter.Null);

        SpaceAt(resolver, animated, 1f);
        store.Fresh = true;
        var arrived = SpaceAt(resolver, animated, 2f);

        Assert.Equal(1f, arrived.SpaceFade);
        Assert.Equal([0], arrived.DroppedLocalLights);
        Assert.Equal([0], arrived.DroppedSuns);
        Assert.Empty(arrived.FadingLocalLights);
    }

    // The courtyard with a Baked sun and a Baked point light in it, at the time given.
    private static BakeFrame SpaceAt(BakeResolver resolver, AnimatedLights animated, float time) => resolver.Resolve(
        new FrameView
        {
            BakedSpace = Courtyard, Lights = [Sun(LightMode.Baked)], LocalLights = [Point(1, InCourtyard, LightMode.Baked)],
            Time = time,
        },
        [Static(1, InCourtyard)], TwoParts, 42, animated);

    private static ResolveCase Scenario(
        BakedSpace? space, BakedChunk[] chunks, DirectionalLight[] suns, LocalLight[] lights, DrawItem[] draws,
        IBakeStore store) => new(space, chunks, suns, lights, draws, store);

    // A FrameView can't be kept in a tuple or an array, so each case builds its own.
    private sealed record ResolveCase(
        BakedSpace? Space, BakedChunk[] Chunks, DirectionalLight[] Suns, LocalLight[] Lights, DrawItem[] Draws,
        IBakeStore Store)
    {
        public FrameView Frame => new()
        {
            BakedSpace = Space, BakedChunks = Chunks, Lights = Suns, LocalLights = Lights, Time = 1f,
        };
    }

    private static DirectionalLight Sun(LightMode mode) => new()
    {
        Mode = mode,
        Direction = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)),
        Radiance = new Vector3(3f, 3f, 2.5f),
        CastsShadow = true,
    };

    private static LocalLight Point(int key, Vector3 position, LightMode mode) => new()
    {
        Mode = mode,
        Key = key,
        Position = position,
        Radiance = new Vector3(2f, 1.5f, 1f),
        Range = 4f,
    };

    // An id's low 12 bits hold the part, so the instance number is moved above them.
    private static DrawItem Static(ulong instance, Vector3 at) =>
        new() { Transform = Matrix4x4.CreateTranslation(at), BakeId = BakeIds.Of(instance << 16, 0) };

    private static DrawItem Part(ulong instance, int part, Vector3 at) => new()
    {
        Transform = Matrix4x4.CreateTranslation(at),
        BakeId = BakeIds.Of(instance << 16, part),
        Detail = new DetailHandle(part),
    };

    // Answers fresh without allocating, as LightBakeStore does once warm (see LightBakeStoreTests), so what is
    // measured is the resolver alone.
    private sealed class EveryBakeFresh : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }

    // Every bake missing until Fresh is set, then every bake fresh, as when a background bake saves them.
    private sealed class Switch : IBakeStore
    {
        public bool Fresh { get; set; }

        public bool IsFresh(string file, BakeInputs inputs) => Fresh;
    }

    private sealed class SavedBakes : IBakeStore
    {
        private readonly Dictionary<string, BakeInputs> _saved = [];

        public void Save(string file, BakeInputs inputs) => _saved[file] = inputs;

        public bool IsFresh(string file, BakeInputs inputs) => _saved.TryGetValue(file, out var saved) && saved == inputs;
    }
}
