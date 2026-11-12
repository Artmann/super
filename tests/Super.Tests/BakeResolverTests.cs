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
            first.Frame, first.Draws, TwoParts, 42,new AnimatedLights(TextWriter.Null), NoBakes.Instance).Space!.Value);
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
                scenario.Frame, scenario.Draws, TwoParts, 42,new AnimatedLights(TextWriter.Null), scenario.Store);
            resolver.Store = scenario.Store;
            var actual = resolver.Resolve(
                scenario.Frame, scenario.Draws, TwoParts, 42,new AnimatedLights(TextWriter.Null));

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
            first.Frame, first.Draws, TwoParts, 42,new AnimatedLights(TextWriter.Null), first.Store).DroppedLocalLights);
    }

    [Fact]
    public void A_frame_with_no_areas_gives_the_shared_empty_frame_after_one_with_areas()
    {
        var resolver = new BakeResolver(new EveryBakeFresh());
        var animated = new AnimatedLights(TextWriter.Null);
        var busy = new FrameView { BakedSpace = Courtyard, Lights = [Sun(LightMode.Baked)], LocalLights = [Point(1, InCourtyard, LightMode.Baked)] };

        var withAreas = resolver.Resolve(busy, [], TwoParts, 42,animated);
        Assert.Equal([0], withAreas.DroppedLocalLights);

        var empty = resolver.Resolve(default, [], TwoParts, 42,animated);

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

    private sealed class SavedBakes : IBakeStore
    {
        private readonly Dictionary<string, BakeInputs> _saved = [];

        public void Save(string file, BakeInputs inputs) => _saved[file] = inputs;

        public bool IsFresh(string file, BakeInputs inputs) => _saved.TryGetValue(file, out var saved) && saved == inputs;
    }
}
