using Rendition;

namespace Super.Tests;

public class AutoBakePolicyTests
{
    private static readonly DevSettings Dev = new(
        [Path.Combine("C:", "game", "content"), Path.Combine("C:", "game", "shared")],
        Path.Combine("C:", "tools", "super-cook.dll"),
        "desktop");

    [Fact]
    public void From_source_bakes_spaces_and_chunks_preview_then_final_at_4_ms()
    {
        var plan = AutoBakePolicy.For(Dev, canTrace: true, new Graphics());

        Assert.NotNull(plan);
        Assert.True(plan.Spaces);
        Assert.Equal([BakeQuality.Preview, BakeQuality.Final], plan.Qualities);
        Assert.Equal(4f, plan.Budget);
        Assert.Equal(Path.Combine("C:", "game", "content"), plan.SpaceFolder);
    }

    [Fact]
    public void Published_bakes_chunks_only_at_preview_and_2_ms()
    {
        var plan = AutoBakePolicy.For(null, canTrace: true, new Graphics());

        Assert.NotNull(plan);
        Assert.False(plan.Spaces);
        Assert.Equal([BakeQuality.Preview], plan.Qualities);
        Assert.Equal(2f, plan.Budget);
        Assert.Null(plan.SpaceFolder);
    }

    [Fact]
    public void No_tracing_bakes_nothing()
    {
        Assert.Null(AutoBakePolicy.For(Dev, canTrace: false, new Graphics()));
        Assert.Null(AutoBakePolicy.For(null, canTrace: false, new Graphics()));
    }

    [Fact]
    public void Switch_off_bakes_nothing()
    {
        var graphics = new Graphics { BakeOnArrival = false };

        Assert.Null(AutoBakePolicy.For(Dev, canTrace: true, graphics));
        Assert.Null(AutoBakePolicy.For(null, canTrace: true, graphics));
    }

    [Fact]
    public void Budget_set_by_the_game_wins()
    {
        var graphics = new Graphics { BakeBudget = 3f };

        Assert.Equal(3f, AutoBakePolicy.For(Dev, canTrace: true, graphics)!.Budget);
        Assert.Equal(3f, AutoBakePolicy.For(null, canTrace: true, graphics)!.Budget);
    }
}
