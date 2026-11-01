namespace Super.Tests;

public class GameRandomTests
{
    private static ulong[] Rolls(RandomStream stream, int count = 8)
    {
        var rolls = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            rolls[i] = stream.NextBits();
        }

        return rolls;
    }

    [Fact]
    public void Same_seed_and_name_give_the_same_sequence()
    {
        Assert.Equal(Rolls(new GameRandom(42).Stream("combat")), Rolls(new GameRandom(42).Stream("combat")));
    }

    [Fact]
    public void Different_seeds_give_different_sequences()
    {
        Assert.NotEqual(Rolls(new GameRandom(1).Stream("combat")), Rolls(new GameRandom(2).Stream("combat")));
    }

    [Fact]
    public void Different_names_give_different_sequences()
    {
        var random = new GameRandom(42);
        Assert.NotEqual(Rolls(random.Stream("combat")), Rolls(random.Stream("weather")));
    }

    [Fact]
    public void Rolling_one_stream_leaves_another_alone()
    {
        var expected = Rolls(new GameRandom(42).Stream("weather"));

        var random = new GameRandom(42);
        Rolls(random.Stream("combat"), 1000);

        Assert.Equal(expected, Rolls(random.Stream("weather")));
    }

    [Fact]
    public void Asking_for_a_name_twice_gives_the_same_stream()
    {
        var random = new GameRandom(42);
        Assert.Same(random.Stream("combat"), random.Stream("combat"));
    }

    [Fact]
    public void The_sequence_never_changes_between_builds()
    {
        // Replays and saved seeds depend on these exact numbers. If this fails, the
        // generator changed, and every recorded seed in every game stops meaning anything.
        ulong[] expected = [11926690769562539327, 6436225704593015136, 8086968665446730987];
        Assert.Equal(expected, Rolls(new GameRandom(42).Stream("weather"), 3));

        // The helpers built on those bits are pinned too, since a game feels them and not the bits.
        var stream = new GameRandom(42).Stream("weather");
        Assert.Equal(new[] { 0.6465472f, 0.34890848f }, new[] { stream.Float(), stream.Float() });
        Assert.Equal(new[] { 44, 65, 41 }, new[] { stream.Between(1, 100), stream.Between(1, 100), stream.Between(1, 100) });
        var items = new List<int> { 1, 2, 3, 4, 5, 6 };
        stream.Shuffle(items);
        Assert.Equal([4, 1, 5, 3, 6, 2], items);
    }
}
