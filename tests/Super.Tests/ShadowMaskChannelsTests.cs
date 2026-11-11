using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ShadowMaskChannelsTests
{
    private static readonly BakedSpace Hall = new("hall", new Vector3(0f, 0f, 0f), new Vector3(20f, 5f, 20f), 1f);

    private static LocalLight Point(int key, float x, LightMode mode = LightMode.Mixed, float range = 5f, float z = 10f) =>
        new() { Key = key, Position = new Vector3(x, 1f, z), Range = range, Mode = mode };

    private static (int[] Suns, int[] Lights, List<int> Unassigned) Run(
        DirectionalLight[] suns, LocalLight[] lights)
    {
        var sunChannels = new int[suns.Length];
        var lightChannels = new int[lights.Length];
        var unassigned = new List<int>();
        ShadowMaskChannels.Assign(Hall, suns, lights, sunChannels, lightChannels, unassigned);

        return (sunChannels, lightChannels, unassigned);
    }

    [Fact]
    public void A_mixed_sun_takes_channel_zero()
    {
        var (suns, _, unassigned) = Run([new DirectionalLight { Mode = LightMode.Mixed }], []);

        Assert.Equal([0], suns);
        Assert.Empty(unassigned);
    }

    [Fact]
    public void A_mixed_sun_leaves_channel_zero_to_no_light()
    {
        var (_, lights, _) = Run([new DirectionalLight { Mode = LightMode.Mixed }], [Point(1, 10f)]);

        Assert.Equal([1], lights);
    }

    [Fact]
    public void Lights_whose_ranges_do_not_overlap_share_a_channel()
    {
        // A and B are 16 apart with ranges of 5, so 10 reach between them leaves 6 clear. C is 2 from A, so it overlaps A
        // only and takes the next channel. A and B take 0, C takes 1.
        var (_, lights, unassigned) = Run([], [Point(1, 2f), Point(2, 18f), Point(3, 4f)]);

        Assert.Equal([0, 0, 1], lights);
        Assert.Empty(unassigned);
    }

    [Fact]
    public void Five_overlapping_mixed_lights_leave_one_without_a_channel()
    {
        var (suns, lights, unassigned) = Run(
            [new DirectionalLight { Mode = LightMode.Mixed }],
            [Point(40, 11f), Point(10, 8f), Point(30, 10f), Point(20, 9f)]);

        // The sun has 0. Keys 10, 20 and 30 come first and take 1, 2 and 3, so key 40 has nothing left.
        Assert.Equal([0], suns);
        Assert.Equal([-1, 1, 3, 2], lights);
        Assert.Equal([40], unassigned);
    }

    [Fact]
    public void Baked_and_real_time_lights_take_no_channel_and_leave_channels_free()
    {
        var (_, lights, unassigned) = Run([], [
            Point(1, 8f, LightMode.Baked),
            Point(2, 9f, LightMode.Realtime),
            Point(3, 10f),
            Point(4, 11f),
            Point(5, 12f),
            Point(6, 13f),
        ]);

        // Four mixed lights fill 0 to 3 though a baked and a real-time light overlap them.
        Assert.Equal([-1, -1, 0, 1, 2, 3], lights);
        Assert.Empty(unassigned);
    }

    [Fact]
    public void A_baked_or_real_time_sun_takes_no_channel()
    {
        var (suns, lights, _) = Run(
            [new DirectionalLight { Mode = LightMode.Baked }, new DirectionalLight { Mode = LightMode.Realtime }],
            [Point(1, 10f)]);

        Assert.Equal([-1, -1], suns);
        Assert.Equal([0], lights);
    }

    [Fact]
    public void Only_the_first_of_two_mixed_suns_takes_channel_zero()
    {
        var (suns, _, _) = Run(
            [new DirectionalLight { Mode = LightMode.Mixed }, new DirectionalLight { Mode = LightMode.Mixed }], []);

        Assert.Equal([0, -1], suns);
    }

    [Fact]
    public void Lights_outside_the_space_take_no_channel()
    {
        // Key 5 stands 10 past the box's x = 20 face; its range of 15 reaches in but it is not in the box.
        var (_, lights, unassigned) = Run([], [Point(5, 30f, range: 15f), Point(1, 10f)]);

        Assert.Equal([-1, 0], lights);
        Assert.Empty(unassigned);
    }

    [Fact]
    public void Assignment_does_not_depend_on_light_order()
    {
        LocalLight[] original = [Point(40, 11f), Point(10, 8f), Point(30, 10f), Point(20, 9f), Point(50, 2f, z: 19f), Point(60, 18f, z: 1f)];
        var sun = new DirectionalLight { Mode = LightMode.Mixed };

        for (var seed = 1; seed <= 10; seed++)
        {
            var shuffled = original.ToArray();
            new Random(seed).Shuffle(shuffled);

            var (_, channels, unassigned) = Run([sun], shuffled);

            var byKey = new SortedDictionary<int, int>();
            for (var i = 0; i < shuffled.Length; i++)
            {
                byKey[shuffled[i].Key] = channels[i];
            }

            // Keys 10, 20 and 30 take 1, 2 and 3, and 40 is left. 50 and 60 stand in opposite corners, more than 10
            // from the four and from each other, so each takes channel 1, the first after the sun's.
            Assert.Equal([10, 20, 30, 40, 50, 60], byKey.Keys);
            Assert.Equal([1, 2, 3, -1, 1, 1], byKey.Values);
            Assert.Equal([40], unassigned);
        }
    }

    [Fact]
    public void A_channel_list_that_is_too_short_throws_and_says_so()
    {
        var error = Assert.Throws<ArgumentException>(() => ShadowMaskChannels.Assign(
            Hall, [], [Point(1, 10f), Point(2, 11f)], [], new int[1], new List<int>()));

        Assert.Contains("lightChannels holds 1 entries for 2 lights", error.Message);
    }
}
