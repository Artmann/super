using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>
/// The frame's four shadow mask slots, shared by the space's bake and every chunk bake drawn. The space's lights keep
/// their channels as slots; chunk lights take the rest greedily, in order of key.
/// </summary>
public class MaskSlotsTests
{
    // A lamp in a chunk of its own, unless a chunk is given.
    private static (int Key, int Chunk, Vector3 Position, float Range) Lamp(int key, float x, float range = 2f, int chunk = -1) =>
        (key, chunk < 0 ? key : chunk, new Vector3(x, 1f, 0f), range);

    private static (int[] Slots, List<int> Unslotted) Run(
        int spaceSun, (int Key, int SpaceChannel)[] space, (int Key, int Chunk, Vector3 Position, float Range)[] chunk)
    {
        var slots = new int[space.Length + chunk.Length];
        var unslotted = new List<int> { 99 };
        MaskSlots.Assign(spaceSun, space, chunk, slots, unslotted);
        return (slots, unslotted);
    }

    [Fact]
    public void Two_chunks_with_overlapping_lights_get_different_slots()
    {
        // Each lamp is channel 0 in its own chunk's bake. They stand 1 metre apart with ranges of 2, so their light
        // overlaps: key 3 comes first and takes slot 0, key 7 the next.
        var (slots, unslotted) = Run(-1, [], [Lamp(7, 1f), Lamp(3, 0f)]);

        Assert.Equal([1, 0], slots);
        Assert.Empty(unslotted);
    }

    [Fact]
    public void Far_apart_chunk_lights_share_a_slot()
    {
        // 10 metres apart with 4 metres of reach between them.
        var (slots, unslotted) = Run(-1, [], [Lamp(7, 10f), Lamp(3, 0f)]);

        Assert.Equal([0, 0], slots);
        Assert.Empty(unslotted);
    }

    [Fact]
    public void Two_lights_of_one_chunk_never_share_a_slot()
    {
        // Two forge fires 20 metres apart in one placed chunk. The chunk's one table can give each slot only one of its
        // channels, so they need two slots however far apart they stand.
        var (slots, unslotted) = Run(-1, [], [Lamp(7, 20f, chunk: 4), Lamp(3, 0f, chunk: 4)]);

        Assert.Equal([1, 0], slots);
        Assert.Empty(unslotted);
    }

    [Fact]
    public void Lights_of_two_copies_of_a_chunk_share_a_slot_when_far_apart()
    {
        // The same chunk placed twice, 20 metres apart: each copy has its own table, so only overlap keeps them apart.
        var (slots, _) = Run(-1, [], [Lamp(7, 20f, chunk: 1), Lamp(3, 0f, chunk: 0)]);

        Assert.Equal([0, 0], slots);
    }

    [Fact]
    public void Lights_just_out_of_reach_share_a_slot_and_just_in_reach_do_not()
    {
        // Ranges of 2 reach 4 between them: 4.01 apart they share, 3.99 apart they don't.
        Assert.Equal([0, 0], Run(-1, [], [Lamp(1, 0f), Lamp(2, 4.01f)]).Slots);
        Assert.Equal([0, 1], Run(-1, [], [Lamp(1, 0f), Lamp(2, 3.99f)]).Slots);
    }

    [Fact]
    public void Space_channels_keep_their_slots()
    {
        // The space's sun holds 0 and its lamps 1 and 2, wherever the chunk lamp stands, so the chunk lamp, far from all
        // of them, gets 3.
        var (slots, unslotted) = Run(0, [(5, 2), (6, 1)], [Lamp(9, 100f)]);

        Assert.Equal([2, 1, 3], slots);
        Assert.Empty(unslotted);
    }

    [Fact]
    public void Without_a_space_sun_slot_zero_is_free()
    {
        var (slots, _) = Run(-1, [(5, 1)], [Lamp(9, 100f)]);

        Assert.Equal([1, 0], slots);
    }

    [Fact]
    public void A_chunk_light_with_every_slot_taken_is_unslotted()
    {
        // The sun holds 0 and the space's lamp 1. Keys 2 and 4 overlap and take 2 and 3; key 8, overlapping both, has
        // nothing left, nor has key 6. Key 10 is far from them all and shares 2. The unslotted keys come in order.
        var (slots, unslotted) = Run(
            0, [(1, 1)], [Lamp(8, 0.5f), Lamp(4, 1f), Lamp(10, 50f), Lamp(2, 0f), Lamp(6, 1.5f)]);

        Assert.Equal([1, -1, 3, 2, 2, -1], slots);
        Assert.Equal([6, 8], unslotted);
    }

    [Fact]
    public void Assign_allocates_nothing_with_the_scratch_given()
    {
        (int Key, int SpaceChannel)[] space = [(1, 1), (2, 2)];
        (int Key, int Chunk, Vector3 Position, float Range)[] chunk = [Lamp(8, 0.5f), Lamp(4, 1f), Lamp(10, 50f), Lamp(2, 0f), Lamp(6, 1.5f)];
        var slots = new int[space.Length + chunk.Length];
        var unslotted = new List<int>(chunk.Length);
        MaskSlots.Assign(0, space, chunk, slots, unslotted);
        Assert.NotEmpty(unslotted);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            MaskSlots.Assign(0, space, chunk, slots, unslotted);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Too_few_slots_throws_naming_the_counts()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaskSlots.Assign(-1, [(1, 1)], [Lamp(2, 0f)], new int[1], []));

        Assert.StartsWith("slots holds 1 entries for 1 space and 1 chunk lights.", error.Message);
        Assert.Equal("slots", error.ParamName);
    }
}
