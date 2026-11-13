using Rendition;

namespace Super.Tests;

public sealed class AnimatedLightTests
{
    private static readonly LightKey Point7 = new(false, 7);

    private static void Feed(AnimatedLights lights, float time, params (LightKey Key, ulong Hash)[] frame)
    {
        lights.Update(frame, time);
    }

    [Fact]
    public void Still_light_is_not_animated()
    {
        var log = new StringWriter();
        var lights = new AnimatedLights(log);

        for (var i = 0; i < 10; i++)
        {
            Feed(lights, i * 0.1f, (Point7, 42ul));

            Assert.False(lights.IsAnimated(Point7));
        }

        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void Settles_a_second_after_its_first_frame()
    {
        var lights = new AnimatedLights(new StringWriter());
        Assert.False(lights.Settled);

        // Starting at 5 s, as a game watched from part way through does.
        Feed(lights, 5f, (Point7, 1ul));
        Assert.False(lights.Settled);
        Feed(lights, 5.9f, (Point7, 1ul));
        Assert.False(lights.Settled);
        Feed(lights, 6f, (Point7, 1ul));
        Assert.True(lights.Settled);
    }

    [Fact]
    public void Time_going_back_starts_settling_again()
    {
        var lights = new AnimatedLights(new StringWriter());
        Feed(lights, 0f, (Point7, 1ul));
        Feed(lights, 2f, (Point7, 1ul));
        Assert.True(lights.Settled);

        Feed(lights, 0.5f, (Point7, 1ul));
        Assert.False(lights.Settled);
        Feed(lights, 1.5f, (Point7, 1ul));
        Assert.True(lights.Settled);
    }

    [Fact]
    public void Light_whose_hash_changes_is_animated()
    {
        var lights = new AnimatedLights(new StringWriter());

        Feed(lights, 0f, (Point7, 1ul));
        Assert.False(lights.IsAnimated(Point7));

        Feed(lights, 0.1f, (Point7, 2ul));
        Assert.True(lights.IsAnimated(Point7));
    }

    [Fact]
    public void Log_names_an_animated_light_once()
    {
        var log = new StringWriter();
        var lights = new AnimatedLights(log);

        for (var i = 0; i < 100; i++)
        {
            Feed(lights, i * 0.01f, (Point7, (ulong)i));
        }

        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var line = Assert.Single(lines);
        Assert.Contains("point light 7", line);
        Assert.Contains("kept out of the bake", line);
        Assert.Contains("1 second", line);
    }

    [Fact]
    public void Spot_and_sun_are_named_by_their_kind()
    {
        var log = new StringWriter();
        var lights = new AnimatedLights(log);
        var spot = new LightKey(false, 7);
        var sun = new LightKey(true, 0);

        lights.Update(new (LightKey, ulong, bool)[] { (spot, 1ul, true), (sun, 1ul, false) }, 0f);
        lights.Update(new (LightKey, ulong, bool)[] { (spot, 2ul, true), (sun, 2ul, false) }, 0.1f);

        var text = log.ToString();
        Assert.Contains("spot light 7", text);
        Assert.Contains("sun 0", text);
        Assert.DoesNotContain("point light", text);
    }

    [Fact]
    public void Settled_light_returns_after_one_second_and_not_before()
    {
        var lights = new AnimatedLights(new StringWriter());

        Feed(lights, 0f, (Point7, 1ul));
        Feed(lights, 1f, (Point7, 2ul));
        Feed(lights, 2f, (Point7, 3ul));
        Feed(lights, 2.5f, (Point7, 3ul));
        Assert.True(lights.IsAnimated(Point7));

        Feed(lights, 2.99f, (Point7, 3ul));
        Assert.True(lights.IsAnimated(Point7));

        Feed(lights, 3f, (Point7, 3ul));
        Assert.False(lights.IsAnimated(Point7));
    }

    [Fact]
    public void Light_that_moves_again_starts_its_second_over()
    {
        var lights = new AnimatedLights(new StringWriter());

        Feed(lights, 1f, (Point7, 1ul));
        Feed(lights, 1.5f, (Point7, 2ul));
        Feed(lights, 2f, (Point7, 2ul));
        Feed(lights, 2.5f, (Point7, 3ul));
        Feed(lights, 3f, (Point7, 3ul));
        Feed(lights, 3.4f, (Point7, 3ul));
        Assert.True(lights.IsAnimated(Point7));

        Feed(lights, 3.5f, (Point7, 3ul));
        Assert.False(lights.IsAnimated(Point7));
    }

    [Fact]
    public void Clock_going_back_starts_over()
    {
        var log = new StringWriter();
        var lights = new AnimatedLights(log);

        Feed(lights, 9.9f, (Point7, 1ul));
        Feed(lights, 10f, (Point7, 2ul));
        Assert.True(lights.IsAnimated(Point7));

        Feed(lights, 0.2f, (Point7, 2ul));
        Assert.False(lights.IsAnimated(Point7));

        // The light is new again, so moving it logs again, and it settles a second after that move.
        Feed(lights, 0.5f, (Point7, 3ul));
        Assert.True(lights.IsAnimated(Point7));
        Assert.Equal(2, log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        Feed(lights, 1.49f, (Point7, 3ul));
        Assert.True(lights.IsAnimated(Point7));
        Feed(lights, 1.5f, (Point7, 3ul));
        Assert.False(lights.IsAnimated(Point7));
    }

    [Fact]
    public void Two_lights_sharing_a_key_are_both_animated()
    {
        var log = new StringWriter();
        var lights = new AnimatedLights(log);
        var key = new LightKey(false, 3);

        Feed(lights, 0f, (key, 1ul));
        Feed(lights, 0.1f, (key, 1ul), (key, 2ul));
        Assert.True(lights.IsAnimated(key));

        Feed(lights, 0.2f, (key, 1ul), (key, 2ul));
        Assert.True(lights.IsAnimated(key));

        var line = Assert.Single(log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("share key 3", line);
    }

    [Fact]
    public void Light_that_leaves_and_comes_back_is_new()
    {
        var lights = new AnimatedLights(new StringWriter());

        Feed(lights, 0f, (Point7, 1ul));
        Feed(lights, 0.1f, new (LightKey, ulong)[0]);
        Feed(lights, 0.2f, (Point7, 2ul));

        Assert.False(lights.IsAnimated(Point7));
    }

    [Fact]
    public void Sun_and_point_light_with_the_same_number_are_apart()
    {
        var lights = new AnimatedLights(new StringWriter());
        var sun = new LightKey(true, 0);
        var point = new LightKey(false, 0);

        Feed(lights, 0f, (sun, 1ul), (point, 5ul));
        Feed(lights, 0.1f, (sun, 2ul), (point, 5ul));

        Assert.True(lights.IsAnimated(sun));
        Assert.False(lights.IsAnimated(point));
    }

    [Fact]
    public void A_steady_frame_allocates_nothing()
    {
        var lights = new AnimatedLights(new StringWriter());
        var frame = new (LightKey, ulong)[] { (Point7, 1ul), (new LightKey(true, 0), 2ul), (new LightKey(false, 8), 3ul) };

        for (var i = 0; i < 5; i++)
        {
            lights.Update(frame, i * 0.1f);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 5; i < 105; i++)
        {
            lights.Update(frame, i * 0.1f);
            lights.IsAnimated(Point7);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
