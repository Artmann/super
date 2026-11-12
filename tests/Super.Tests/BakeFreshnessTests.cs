using System.Numerics;
using System.Text.RegularExpressions;
using Rendition;

namespace Super.Tests;

public sealed class BakeFreshnessTests
{
    private static readonly BakedSpace Courtyard = new("courtyard", new Vector3(-5f, 0f, -5f), new Vector3(5f, 5f, 5f), 1f);

    // A room placed 20 m east of the courtyard, its box 4 m across in its own space.
    private static readonly BakedChunk Room = new(
        "room", Matrix4x4.CreateTranslation(20f, 0f, 0f), new Vector3(-2f, 0f, -2f), new Vector3(2f, 3f, 2f));

    private static readonly Vector3 InCourtyard = new(1f, 1f, 1f);
    private static readonly Vector3 InRoom = new(20.5f, 1f, 0f);
    private static readonly Vector3 Nowhere = new(-100f, 1f, 0f);

    // Every draw is a ball of radius 0.5 round its origin, so its transform alone says where it is.
    private static BakeSphere HalfMetreBall(DetailHandle detail) => new(Vector3.Zero, 0.5f);

    [Fact]
    public void No_bake_makes_every_light_realtime()
    {
        DirectionalLight[] suns = [Sun(LightMode.Mixed)];
        LocalLight[] lights = [Point(1, InCourtyard, LightMode.Mixed), Point(2, InCourtyard, LightMode.Baked), Point(3, InRoom, LightMode.Baked)];

        var resolved = Resolve(Courtyard, [Room], suns, lights, [], NoBakes.Instance);

        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(true, 0)));
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(false, 1)));
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(false, 2)));
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(false, 3)));
        Assert.Empty(resolved.DroppedLocalLights);
        Assert.Empty(resolved.DroppedSuns);
        Assert.False(resolved.SpaceFresh);
        Assert.Equal([false], resolved.ChunkFresh);
    }

    [Fact]
    public void Fresh_bake_drops_baked_lights_and_keeps_mixed_ones()
    {
        DirectionalLight[] suns = [Sun(LightMode.Baked)];
        LocalLight[] lights = [Point(1, InCourtyard, LightMode.Mixed), Point(2, InCourtyard + Vector3.UnitX, LightMode.Baked)];
        DrawItem[] draws = [Static(1, InCourtyard)];
        var animated = new AnimatedLights(new StringWriter());

        // The first frame is baked, and the store keeps what it was baked from.
        var store = new SavedBakes();
        var baked = Resolve(Courtyard, [], suns, lights, draws, store, animated, time: 0f);
        store.Save("lighting/courtyard.lightbake", baked.Space!.Value);

        var resolved = Resolve(Courtyard, [], suns, lights, draws, store, animated, time: 0.1f);

        Assert.True(resolved.SpaceFresh);
        Assert.Equal([1], resolved.DroppedLocalLights);
        Assert.Equal([0], resolved.DroppedSuns);
        Assert.Equal(LightMode.Mixed, resolved.ModeOf(new LightKey(false, 1)));
        Assert.Equal(LightMode.Baked, resolved.ModeOf(new LightKey(false, 2)));
        Assert.Equal(LightMode.Baked, resolved.ModeOf(new LightKey(true, 0)));
    }

    [Fact]
    public void Stale_bake_drops_nothing()
    {
        LocalLight[] lights = [Point(2, InCourtyard, LightMode.Baked)];
        var store = new SavedBakes();
        var animated = new AnimatedLights(new StringWriter());
        var baked = Resolve(Courtyard, [], [], lights, [Static(1, InCourtyard)], store, animated, time: 0f);
        store.Save("lighting/courtyard.lightbake", baked.Space!.Value);

        // A draw added since the bake makes it stale.
        var resolved = Resolve(Courtyard, [], [], lights, [Static(1, InCourtyard), Static(2, InCourtyard)], store, animated, time: 0.1f);

        Assert.False(resolved.SpaceFresh);
        Assert.Empty(resolved.DroppedLocalLights);
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(false, 2)));
    }

    [Fact]
    public void Bake_is_not_reported_stale_before_a_moving_light_is_seen_moving()
    {
        var store = new SavedBakes();
        DrawItem[] draws = [Static(1, InCourtyard)];

        // Baked after the light was seen moving, so it is left out of the bake, as --bake's two seconds of play do.
        var baking = new AnimatedLights(new StringWriter());
        Resolve(Courtyard, [], [], [Swinging(0f)], draws, store, baking, time: 0f);
        var baked = Resolve(Courtyard, [], [], [Swinging(0.1f)], draws, store, baking, time: 0.1f);
        store.Save("lighting/courtyard.lightbake", baked.Space!.Value);
        store.Reported.Clear();

        // On a new run's first frame the light hasn't moved yet, so it is in the inputs and the bake doesn't match.
        var animated = new AnimatedLights(new StringWriter());
        var first = Resolve(Courtyard, [], [], [Swinging(0f)], draws, store, animated, time: 0f);
        Assert.False(first.SpaceFresh);

        // From the next frame it is seen moving, and the bake is fresh through the tracker settling and after.
        for (int i = 1; i <= 15; i++)
        {
            var later = Resolve(Courtyard, [], [], [Swinging(i * 0.1f)], draws, store, animated, time: i * 0.1f);
            Assert.True(later.SpaceFresh);
        }

        Assert.True(animated.Settled);
        Assert.Empty(store.Reported);
    }

    [Fact]
    public void Bake_still_stale_once_the_tracker_settles_is_reported()
    {
        LocalLight[] lights = [Point(2, InCourtyard, LightMode.Baked)];
        var store = new SavedBakes();
        var baked = Resolve(Courtyard, [], [], lights, [Static(1, InCourtyard)], store, time: 0f);
        store.Save("lighting/courtyard.lightbake", baked.Space!.Value);
        store.Reported.Clear();

        // A draw added since the bake makes it stale, whatever the lights do.
        DrawItem[] draws = [Static(1, InCourtyard), Static(2, InCourtyard)];
        var animated = new AnimatedLights(new StringWriter());
        Resolve(Courtyard, [], [], lights, draws, store, animated, time: 0f);
        Resolve(Courtyard, [], [], lights, draws, store, animated, time: 0.5f);
        Assert.Empty(store.Reported);

        var settled = Resolve(Courtyard, [], [], lights, draws, store, animated, time: AnimatedLights.SettleSeconds);

        Assert.False(settled.SpaceFresh);
        Assert.Equal(["lighting/courtyard.lightbake"], store.Reported);
    }

    // A Mixed lantern swinging along x in the courtyard, at the moment given.
    private static LocalLight Swinging(float seconds) => Point(9, new Vector3(MathF.Sin(seconds * 3f), 2f, 0f), LightMode.Mixed);

    [Fact]
    public void Baked_light_outside_every_area_stays()
    {
        LocalLight[] lights = [Point(4, Nowhere, LightMode.Baked)];

        var resolved = Resolve(Courtyard, [Room], [], lights, [], new EveryBakeFresh());

        Assert.True(resolved.SpaceFresh);
        Assert.Empty(resolved.DroppedLocalLights);
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(false, 4)));
    }

    [Fact]
    public void Animated_baked_light_is_not_dropped()
    {
        var animated = new AnimatedLights(new StringWriter());
        var store = new EveryBakeFresh();

        Resolve(Courtyard, [], [], [Point(5, new Vector3(0f, 1f, 0f), LightMode.Baked)], [], store, animated, time: 0f);
        var moved = Resolve(Courtyard, [], [], [Point(5, new Vector3(0.5f, 1f, 0f), LightMode.Baked)], [], store, animated, time: 0.1f);
        var movedAgain = Resolve(Courtyard, [], [], [Point(5, new Vector3(1f, 1f, 0f), LightMode.Baked)], [], store, animated, time: 0.2f);

        Assert.Empty(moved.DroppedLocalLights);
        Assert.Equal(LightMode.Realtime, moved.ModeOf(new LightKey(false, 5)));
        Assert.Empty(movedAgain.DroppedLocalLights);
        Assert.Equal(moved.Space!.Value.Lights, movedAgain.Space!.Value.Lights);
    }

    [Fact]
    public void Light_that_settles_returns_to_the_bake()
    {
        var animated = new AnimatedLights(new StringWriter());
        var store = new EveryBakeFresh();
        LocalLight[] still = [Point(5, new Vector3(0.5f, 1f, 0f), LightMode.Baked)];

        Resolve(Courtyard, [], [], [Point(5, new Vector3(0f, 1f, 0f), LightMode.Baked)], [], store, animated, time: 0f);
        Resolve(Courtyard, [], [], still, [], store, animated, time: 0.1f);
        var settled = Resolve(Courtyard, [], [], still, [], store, animated, time: 0.15f + AnimatedLights.SettleSeconds);

        Assert.Equal([0], settled.DroppedLocalLights);
        Assert.Equal(LightMode.Baked, settled.ModeOf(new LightKey(false, 5)));
    }

    [Fact]
    public void Realtime_light_is_never_in_the_bake()
    {
        var log = new StringWriter();
        var animated = new AnimatedLights(log);
        var store = new EveryBakeFresh();

        var first = Resolve(Courtyard, [Room], [], [Point(6, InCourtyard, LightMode.Realtime), Point(7, InRoom, LightMode.Realtime)], [], store, animated, time: 0f);
        var moved = Resolve(Courtyard, [Room], [], [Point(6, InCourtyard + Vector3.UnitY, LightMode.Realtime), Point(7, InRoom + Vector3.UnitY, LightMode.Realtime)], [], store, animated, time: 0.1f);
        var none = Resolve(Courtyard, [Room], [], [], [], store, animated, time: 0.2f);

        Assert.Equal(none.Space!.Value.Lights, first.Space!.Value.Lights);
        Assert.Equal(none.Chunks[0].Lights, first.Chunks[0].Lights);
        Assert.Equal(first.Space!.Value.Lights, moved.Space!.Value.Lights);
        Assert.Empty(moved.DroppedLocalLights);
        Assert.Equal(LightMode.Realtime, moved.ModeOf(new LightKey(false, 6)));
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void Sun_belongs_to_the_space_and_not_to_a_chunk()
    {
        var sun = Sun(LightMode.Mixed);
        var turned = sun;
        turned.Direction = Vector3.Normalize(new Vector3(1f, 1f, 0f));

        var before = Resolve(Courtyard, [Room], [sun], [], [], NoBakes.Instance);
        var after = Resolve(Courtyard, [Room], [turned], [], [], NoBakes.Instance);

        Assert.NotEqual(before.Space!.Value.Lights, after.Space!.Value.Lights);
        Assert.Equal(before.Chunks[0], after.Chunks[0]);
    }

    [Fact]
    public void Fresh_chunk_does_not_free_the_space()
    {
        LocalLight[] lights = [Point(1, InCourtyard, LightMode.Baked), Point(2, InRoom, LightMode.Baked)];
        var onlyRooms = new FreshWhere(file => file.StartsWith("lighting/room/", StringComparison.Ordinal));

        var resolved = Resolve(Courtyard, [Room], [Sun(LightMode.Baked)], lights, [], onlyRooms);

        Assert.False(resolved.SpaceFresh);
        Assert.Equal([true], resolved.ChunkFresh);
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(false, 1)));
        Assert.Equal(LightMode.Realtime, resolved.ModeOf(new LightKey(true, 0)));
        Assert.Empty(resolved.DroppedSuns);
        // The room's own light is dropped, so the chunk really was fresh.
        Assert.Equal([1], resolved.DroppedLocalLights);
        Assert.Equal(LightMode.Baked, resolved.ModeOf(new LightKey(false, 2)));
    }

    [Fact]
    public void Store_is_asked_with_the_files_path()
    {
        var store = new RecordingStore();

        var resolved = Resolve(Courtyard, [Room], [], [], [Static(1, InRoom)], store);

        Assert.Equal(2, store.Asked.Count);
        Assert.Contains(("lighting/courtyard.lightbake", resolved.Space!.Value), store.Asked);
        var (roomFile, roomInputs) = Assert.Single(store.Asked, asked => asked.File.StartsWith("lighting/room/", StringComparison.Ordinal));
        Assert.Matches(new Regex("^lighting/room/[0-9a-f]{16}\\.lightbake$"), roomFile);
        Assert.Equal(resolved.Chunks[0], roomInputs);
        Assert.Equal($"lighting/room/{resolved.Chunks[0].Combined:x16}.lightbake", roomFile);
    }

    [Fact]
    public void Draw_in_a_space_and_a_chunk_counts_once()
    {
        // A space whose box reaches into the room's west side, a draw in both, and one in the room alone.
        var wide = Courtyard with { Max = new Vector3(20.6f, 5f, 5f) };
        var shared = new Vector3(20.5f, 1f, 0f);
        var roomOnly = new Vector3(21.8f, 1f, 0f);
        DrawItem[] before = [Static(1, shared), Static(9, roomOnly)];
        DrawItem[] after = [Static(2, shared), Static(9, roomOnly)];

        var first = Resolve(wide, [Room], [], [], before, NoBakes.Instance);
        var second = Resolve(wide, [Room], [], [], after, NoBakes.Instance);
        var roomAlone = Resolve(wide, [Room], [], [], [Static(9, roomOnly)], NoBakes.Instance);

        Assert.NotEqual(first.Space!.Value.Draws, second.Space!.Value.Draws);
        Assert.Equal(first.Chunks[0].Draws, second.Chunks[0].Draws);
        Assert.Equal(roomAlone.Chunks[0].Draws, first.Chunks[0].Draws);
    }

    [Fact]
    public void Chunk_only_hashes_its_own_draws()
    {
        var alone = Resolve(null, [Room], [], [], [Static(1, InRoom)], NoBakes.Instance);
        var withAnother = Resolve(null, [Room], [], [], [Static(1, InRoom), Static(2, Nowhere)], NoBakes.Instance);
        var withOneInside = Resolve(null, [Room], [], [], [Static(1, InRoom), Static(3, InRoom + Vector3.UnitZ)], NoBakes.Instance);

        Assert.Equal(alone.Chunks[0], withAnother.Chunks[0]);
        Assert.NotEqual(alone.Chunks[0].Draws, withOneInside.Chunks[0].Draws);
    }

    [Fact]
    public void Draw_that_is_not_static_counts_nowhere()
    {
        var alone = Resolve(Courtyard, [Room], [], [], [Static(1, InCourtyard)], NoBakes.Instance);
        var withMoving = Resolve(Courtyard, [Room], [], [], [Static(1, InCourtyard), Moving(InCourtyard), Moving(InRoom)], NoBakes.Instance);

        Assert.Equal(alone.Space, withMoving.Space);
        Assert.Equal(alone.Chunks[0], withMoving.Chunks[0]);
    }

    [Fact]
    public void Every_static_draw_blocks_light_in_the_space()
    {
        var alone = Resolve(Courtyard, [], [], [], [Static(1, InCourtyard)], NoBakes.Instance);
        var withFarDraw = Resolve(Courtyard, [], [], [], [Static(1, InCourtyard), Static(2, Nowhere)], NoBakes.Instance);

        Assert.NotEqual(alone.Space!.Value.Draws, withFarDraw.Space!.Value.Draws);
    }

    [Fact]
    public void Moving_a_chunk_changes_the_space_draws()
    {
        // The room 5 m further east, with its draw moved along, so the draw's chunk-relative id is the same.
        var moved = Room with { Transform = Matrix4x4.CreateTranslation(25f, 0f, 0f) };

        var here = Resolve(Courtyard, [Room], [], [], [Static(1, InRoom)], NoBakes.Instance);
        var there = Resolve(Courtyard, [moved], [], [], [Static(1, InRoom + new Vector3(5f, 0f, 0f))], NoBakes.Instance);

        Assert.Equal(here.Chunks[0], there.Chunks[0]);
        Assert.Equal(["draws"], there.Space!.Value.Changes(here.Space!.Value));
    }

    [Fact]
    public void Swapping_where_two_chunks_stand_changes_the_space_draws()
    {
        // Two rooms, 20 m and 40 m east, each owning one draw. Swapping them keeps both the set of ids and the set of
        // places, so only pairing each place with its draws can tell.
        var far = Room with { Transform = Matrix4x4.CreateTranslation(40f, 0f, 0f) };
        var farRoom = InRoom + new Vector3(20f, 0f, 0f);

        var before = Resolve(Courtyard, [Room, far], [], [], [Static(1, InRoom), Static(2, farRoom)], NoBakes.Instance);
        var after = Resolve(Courtyard, [Room, far], [], [], [Static(2, InRoom), Static(1, farRoom)], NoBakes.Instance);

        Assert.Equal(["draws"], after.Space!.Value.Changes(before.Space!.Value));
    }

    [Fact]
    public void Same_model_in_two_copies_of_a_chunk_counts_in_each()
    {
        // Two copies of the room hold the same draw at the same place in each, so both draws have one id. They come
        // one after the other, like a model's parts, but stand in different places, so they are two models.
        var far = Room with { Transform = Matrix4x4.CreateTranslation(40f, 0f, 0f) };
        var farRoom = InRoom + new Vector3(20f, 0f, 0f);

        var both = Resolve(null, [Room, far], [], [], [Static(1, InRoom), Static(1, farRoom)], NoBakes.Instance);
        var alone = Resolve(null, [Room], [], [], [Static(1, InRoom)], NoBakes.Instance);

        Assert.Equal(alone.Chunks[0].Draws, both.Chunks[0].Draws);
        Assert.Equal(alone.Chunks[0].Draws, both.Chunks[1].Draws);
    }

    // Detail 0 is a ball 1 m west of a model's origin and detail 1 one 4 m east of it, so a model with both parts has
    // its union centred 1.5 m east of the origin, and the second part alone is 4 m east.
    private static BakeSphere TwoParts(DetailHandle detail) =>
        new(detail.Index == 0 ? new Vector3(-1f, 0f, 0f) : new Vector3(4f, 0f, 0f), 0.5f);

    private static DrawItem Part(ulong instance, int part, Vector3 at) => new()
    {
        Transform = Matrix4x4.CreateTranslation(at),
        BakeId = BakeIds.Of(instance << 16, part),
        Detail = new DetailHandle(part),
    };

    [Fact]
    public void Interleaved_parts_of_a_model_are_filed_together()
    {
        // The model stands with its origin at x = 20 in the room (box 18 to 22 in x). Its first part's ball is at 19,
        // inside, and its second at 24, outside; the union is centred at 21.5, inside. Another model comes between.
        var at = new Vector3(20f, 1f, 0f);
        DrawItem[] interleaved = [Part(1, 0, at), Static(2, Nowhere), Part(1, 1, at)];
        var store = new RecordingStore();

        Resolve(null, [Room], [], [], interleaved, store, bounds: TwoParts);
        var together = Resolve(null, [Room], [], [], [Part(1, 0, at), Part(1, 1, at)], NoBakes.Instance, bounds: TwoParts);
        var firstAlone = Resolve(null, [Room], [], [], [Part(1, 0, at)], NoBakes.Instance, bounds: TwoParts);

        var (_, roomInputs) = Assert.Single(store.Asked);
        Assert.Equal(together.Chunks[0], roomInputs);
        Assert.NotEqual(firstAlone.Chunks[0].Draws, roomInputs.Draws);
    }

    [Fact]
    public void Draw_order_does_not_change_chunk_inputs()
    {
        var at = new Vector3(20f, 1f, 0f);
        var other = new Vector3(19f, 1f, 1f);
        // Each model's second part is outside the room alone, so a model split in two is filed differently.
        DrawItem[] one = [Part(1, 0, at), Part(1, 1, at), Part(2, 0, other), Part(2, 1, other)];
        DrawItem[] two = [Part(1, 0, at), Part(2, 0, other), Part(1, 1, at), Part(2, 1, other)];

        var first = Resolve(Courtyard, [Room], [], [], one, NoBakes.Instance, bounds: TwoParts);
        var second = Resolve(Courtyard, [Room], [], [], two, NoBakes.Instance, bounds: TwoParts);

        Assert.Equal(first.Chunks[0], second.Chunks[0]);
        Assert.Equal(first.Space, second.Space);
    }

    [Fact]
    public void Chunk_light_hash_does_not_depend_on_where_the_chunk_stands()
    {
        var turned = Room with { Transform = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-40f, 0f, 30f) };
        // The same light half a metre along the room's own x axis, in each placement.
        var here = Point(1, new Vector3(20.5f, 1f, 0f), LightMode.Mixed);
        var there = Point(1, Vector3.Transform(new Vector3(0.5f, 1f, 0f), turned.Transform), LightMode.Mixed);

        var placedHere = Resolve(null, [Room], [], [here], [], NoBakes.Instance);
        var placedThere = Resolve(null, [turned], [], [there], [], NoBakes.Instance);
        var empty = Resolve(null, [Room], [], [], [], NoBakes.Instance);

        Assert.Equal(placedHere.Chunks[0], placedThere.Chunks[0]);
        Assert.NotEqual(empty.Chunks[0].Lights, placedHere.Chunks[0].Lights);
    }

    [Fact]
    public void Frame_with_no_areas_resolves_to_nothing()
    {
        var log = new StringWriter();
        var animated = new AnimatedLights(log);
        var store = new RecordingStore();

        Resolve(null, [], [], [Point(1, InCourtyard, LightMode.Baked)], [Static(1, InCourtyard)], store, animated, time: 0f);
        var resolved = Resolve(null, [], [], [Point(1, InRoom, LightMode.Baked)], [Static(1, InCourtyard)], store, animated, time: 0.1f);

        Assert.Null(resolved.Space);
        Assert.Empty(resolved.Chunks);
        Assert.Empty(resolved.ChunkFresh);
        Assert.False(resolved.SpaceFresh);
        Assert.Empty(resolved.DroppedLocalLights);
        Assert.Empty(resolved.DroppedSuns);
        Assert.Empty(store.Asked);
        // The light moved, but nothing hashed or tracked it.
        Assert.False(animated.IsAnimated(new LightKey(false, 1)));
        Assert.Equal("", log.ToString());
    }

    private static BakeFrame Resolve(
        BakedSpace? space, BakedChunk[] chunks, DirectionalLight[] suns, LocalLight[] lights, DrawItem[] draws,
        IBakeStore store, AnimatedLights? animated = null, float time = 0f, Func<DetailHandle, BakeSphere>? bounds = null)
    {
        var frame = new FrameView
        {
            BakedSpace = space,
            BakedChunks = chunks,
            Lights = suns,
            LocalLights = lights,
            Time = time,
        };
        return BakeFreshness.Resolve(
            frame, draws, bounds ?? HalfMetreBall, 42, animated ?? new AnimatedLights(new StringWriter()), store);
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

    private static DrawItem Moving(Vector3 at) => new() { Transform = Matrix4x4.CreateTranslation(at) };

    // Keeps what each bake was made from, and answers fresh only for the same inputs, as a real store will. Each stale
    // answer to a question asked out loud is kept in Reported, where a real store would log it.
    private sealed class SavedBakes : IBakeStore
    {
        private readonly Dictionary<string, BakeInputs> _saved = [];

        public List<string> Reported { get; } = [];

        public void Save(string file, BakeInputs inputs) => _saved[file] = inputs;

        public bool IsFresh(string file, BakeInputs inputs)
        {
            bool fresh = IsFreshQuietly(file, inputs);
            if (!fresh)
            {
                Reported.Add(file);
            }

            return fresh;
        }

        public bool IsFreshQuietly(string file, BakeInputs inputs) => _saved.TryGetValue(file, out var saved) && saved == inputs;
    }

    private sealed class EveryBakeFresh : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }

    private sealed class FreshWhere(Func<string, bool> fresh) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => fresh(file);
    }

    private sealed class RecordingStore : IBakeStore
    {
        public List<(string File, BakeInputs Inputs)> Asked { get; } = [];

        public bool IsFresh(string file, BakeInputs inputs)
        {
            Asked.Add((file, inputs));
            return false;
        }
    }
}
