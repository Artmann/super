using Rendition;

namespace Super.Tests;

public class SkylinePackerTests
{
    [Fact]
    public void Keeps_the_gutter_between_rectangles()
    {
        (int, int)[] sizes = [(10, 10), (10, 10), (10, 10)];
        var at = new (int X, int Y)[3];
        Assert.True(SkylinePacker.TryPack(sizes, 64, 64, gutter: 2, at));
        for (int a = 0; a < 3; a++)
        {
            for (int b = a + 1; b < 3; b++)
            {
                bool apartX = at[a].X + 10 + 2 <= at[b].X || at[b].X + 10 + 2 <= at[a].X;
                bool apartY = at[a].Y + 10 + 2 <= at[b].Y || at[b].Y + 10 + 2 <= at[a].Y;
                Assert.True(apartX || apartY, $"rectangles {a} and {b} are closer than the gutter");
            }
        }
    }

    [Fact]
    public void Keeps_every_rectangle_inside_the_page()
    {
        (int Width, int Height)[] sizes = [(20, 9), (7, 30), (13, 13), (5, 5), (30, 4), (11, 17)];
        var at = new (int X, int Y)[sizes.Length];
        Assert.True(SkylinePacker.TryPack(sizes, 64, 48, gutter: 2, at));
        for (int i = 0; i < sizes.Length; i++)
        {
            Assert.True(at[i].X >= 0 && at[i].Y >= 0, $"rectangle {i} starts outside the page");
            Assert.True(at[i].X + sizes[i].Width <= 64, $"rectangle {i} runs past the right edge");
            Assert.True(at[i].Y + sizes[i].Height <= 48, $"rectangle {i} runs past the bottom edge");
        }
    }

    [Fact]
    public void Fills_a_page_exactly_without_a_gutter()
    {
        (int, int)[] sizes = [(32, 32), (32, 32), (32, 32), (32, 32)];
        Assert.True(SkylinePacker.TryPack(sizes, 64, 64, gutter: 0, new (int, int)[4]));
    }

    [Fact]
    public void Leaves_no_gutter_at_the_pages_edges()
    {
        var at = new (int X, int Y)[2];
        Assert.True(SkylinePacker.TryPack([(31, 64), (31, 64)], 64, 64, gutter: 2, at));
        Assert.Equal(new[] { (0, 0), (33, 0) }, at);
        Assert.False(SkylinePacker.TryPack([(32, 64), (32, 64)], 64, 64, gutter: 2, new (int, int)[2]));
    }

    [Fact]
    public void Says_when_they_dont_fit() =>
        Assert.False(SkylinePacker.TryPack([(65, 1)], 64, 64, gutter: 0, new (int, int)[1]));

    [Fact]
    public void Says_when_the_last_one_doesnt_fit()
    {
        (int, int)[] sizes = [(32, 32), (32, 32), (32, 32), (32, 32), (1, 1)];
        Assert.False(SkylinePacker.TryPack(sizes, 64, 64, gutter: 0, new (int, int)[5]));
    }

    [Fact]
    public void Places_the_tallest_first_at_the_lowest_then_leftmost_spot()
    {
        // The 10 high one goes first, to the corner. The short one then fits lowest, at
        // the floor beside it.
        var at = new (int X, int Y)[2];
        Assert.True(SkylinePacker.TryPack([(10, 5), (10, 10)], 64, 64, gutter: 0, at));
        Assert.Equal(new[] { (10, 0), (0, 0) }, at);
    }

    [Fact]
    public void Stacks_on_the_lowest_step_of_the_skyline()
    {
        // A 10x10 and a 10x4 fill the page's width side by side, so the skyline is 10
        // high then 4 high. The third one is lowest on the 4 high step, not on the 10.
        var at = new (int X, int Y)[3];
        Assert.True(SkylinePacker.TryPack([(10, 10), (10, 4), (10, 3)], 20, 14, gutter: 0, at));
        Assert.Equal(new[] { (0, 0), (10, 0), (10, 4) }, at);
    }

    [Fact]
    public void Packs_the_same_way_every_time()
    {
        (int, int)[] sizes = [(7, 3), (3, 7), (5, 5), (7, 3)];
        var first = new (int, int)[4];
        var second = new (int, int)[4];
        Assert.True(SkylinePacker.TryPack(sizes, 32, 32, 2, first));
        Assert.True(SkylinePacker.TryPack(sizes, 32, 32, 2, second));
        Assert.Equal(first, second);
        Assert.Equal(first.Distinct().Count(), first.Length);
    }

    [Fact]
    public void Breaks_ties_by_width_then_index()
    {
        // Equal heights: the wider goes first, equal sizes go in input order.
        var at = new (int X, int Y)[3];
        Assert.True(SkylinePacker.TryPack([(4, 4), (6, 4), (4, 4)], 64, 64, gutter: 0, at));
        Assert.Equal(new[] { (6, 0), (0, 0), (10, 0) }, at);
    }

    [Fact]
    public void Packs_nothing_into_any_page()
    {
        Assert.True(SkylinePacker.TryPack([], 64, 64, gutter: 2, Span<(int, int)>.Empty));
    }

    [Fact]
    public void Rejects_a_rectangle_with_no_area()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            SkylinePacker.TryPack([(0, 5)], 64, 64, gutter: 0, new (int, int)[1]));
        Assert.Contains("rectangle 0", thrown.Message);
    }

    [Fact]
    public void Rejects_too_few_positions()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            SkylinePacker.TryPack([(1, 1), (1, 1)], 64, 64, gutter: 0, new (int, int)[1]));
        Assert.Contains("positions", thrown.Message);
    }
}
