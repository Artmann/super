using Rendition.Vulkan;

namespace Super.Tests;

/// <summary>Which rough reflection levels each frame redraws after the sky changes.</summary>
public class RoughSlicesTests
{
    // The reflections have six levels; the first is drawn with the sharp part, so these are the rough ones.
    private static readonly int[] RoughLevels = [1, 2, 3, 4, 5];

    [Fact]
    public void A_sky_that_changes_every_frame_still_redraws_every_rough_level_every_three_frames()
    {
        var slices = new RoughSlices();
        var drawn = new List<int[]>();
        for (int frame = 0; frame < 9; frame++)
        {
            slices.Restart();
            drawn.Add(slices.Take());
        }

        for (int start = 0; start + 3 <= drawn.Count; start++)
        {
            var levels = drawn.Skip(start).Take(3).SelectMany(slice => slice).Order().ToArray();
            Assert.Equal(RoughLevels, levels);
        }
    }

    [Fact]
    public void A_change_followed_by_still_frames_finishes_after_three_frames()
    {
        var slices = new RoughSlices();
        slices.Restart();

        var levels = new List<int>();
        while (slices.Unfinished)
        {
            levels.AddRange(slices.Take());
        }

        Assert.Equal(RoughLevels, levels.Order().ToArray());
        Assert.Equal(3, RoughSlices.Count);
    }
}
