namespace Super.Tests;

public class GameArgumentsTests
{
    [Fact]
    public void Nothing_means_nothing_was_asked_for()
    {
        Assert.Equal(new GameArguments(null, null, null), GameArguments.Parse([]));
    }

    [Fact]
    public void Reads_every_option()
    {
        var arguments = GameArguments.Parse(["--seed", "42", "--ticks", "6000", "--screenshot", "day.png"]);

        Assert.Equal(new GameArguments(42, 6000, "day.png"), arguments);
    }

    [Fact]
    public void Leaves_the_games_own_options_alone()
    {
        Assert.Equal(new GameArguments(42, null, null), GameArguments.Parse(["--god-mode", "--seed", "42"]));
    }

    [Theory]
    [InlineData("--seed")]
    [InlineData("--seed", "soon")]
    [InlineData("--seed", "99999999999")]
    [InlineData("--ticks", "many", "--screenshot", "a.png")]
    [InlineData("--ticks", "-1", "--screenshot", "a.png")]
    [InlineData("--ticks", "10")]
    [InlineData("--screenshot")]
    public void Explains_a_bad_command_line(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => GameArguments.Parse(args));
    }
}
