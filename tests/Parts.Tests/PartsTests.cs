using Parts;
using Super;
using Super.Testing;

namespace Parts.Tests;

public sealed class PartsTests
{
    private static readonly string[] FigureParts =
        ["Body_Red", "Body_Green", "Body_Blue", "Head_Round", "Head_Square", "Head_Cone", "Hat_Top", "Hat_Cap"];

    private static HeadlessRun<PartsGame> Start(int seed = 1) => HeadlessGame.Create<PartsGame>(seed);

    private static List<ModelDraw> FigureDraws(HeadlessRun<PartsGame> run) =>
        run.Drawn.Where(draw => draw.Model == run.Game.Figure).ToList();

    private static string Shown(PartMask mask) =>
        string.Concat(Enumerable.Range(0, mask.Count).Select(part => mask.Shows(part) ? '1' : '0'));

    [Fact]
    public void The_cooked_figure_has_its_eight_parts_in_the_order_the_file_lists_them()
    {
        using var run = Start();

        Assert.Equal(FigureParts, run.Game.FigureParts);
    }

    [Fact]
    public void Each_of_the_nine_figures_shows_one_body_one_head_and_maybe_a_hat()
    {
        using var run = Start();
        run.Ticks(3);

        var draws = FigureDraws(run);
        Assert.Equal(9, draws.Count);
        foreach (var draw in draws)
        {
            Assert.NotNull(draw.Parts);
            Assert.InRange(draw.Parts.Kept, 2, 3);
            var kept = FigureParts.Where((_, part) => draw.Parts.Shows(part)).ToList();
            Assert.Single(kept, name => name.StartsWith("Body_", StringComparison.Ordinal));
            Assert.Single(kept, name => name.StartsWith("Head_", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_nine_figures_show_nine_different_parts()
    {
        using var run = Start();
        run.Ticks(3);

        var shown = FigureDraws(run).Select(draw => Shown(draw.Parts!)).ToList();

        Assert.Equal(9, shown.Distinct().Count());
    }

    [Fact]
    public void Space_shows_every_part_of_every_figure()
    {
        using var run = Start();
        run.Ticks(3);
        Assert.All(FigureDraws(run), draw => Assert.NotNull(draw.Parts));

        run.Input.Press(Key.Space);
        run.Ticks(2);

        Assert.True(run.Game.ShowingEveryPart);
        var draws = FigureDraws(run);
        Assert.Equal(9, draws.Count);
        Assert.All(draws, draw => Assert.Null(draw.Parts));
    }

    [Fact]
    public void Space_again_puts_the_masks_back()
    {
        using var run = Start();
        run.Input.Press(Key.Space);
        run.Ticks(2);

        run.Input.Press(Key.Space);
        run.Ticks(2);

        Assert.False(run.Game.ShowingEveryPart);
        Assert.All(FigureDraws(run), draw => Assert.NotNull(draw.Parts));
    }

    [Fact]
    public void The_unmasked_script_shows_every_part_for_the_before_picture()
    {
        using var run = Start();
        run.Play(run.Game.Scripts["unmasked"]);
        run.Ticks(3);

        Assert.All(FigureDraws(run), draw => Assert.Null(draw.Parts));
    }
}
