namespace Super.Tests;

public class WorldFindTests
{
    private record struct Player(int Lives);

    private record struct Coin(int Worth);

    private record struct Shiny;

    private readonly World _world = new();

    [Fact]
    public void Count_is_how_many_entities_have_the_component()
    {
        _world.Spawn(new Coin(1));
        _world.Spawn(new Coin(2));
        _world.Spawn(new Coin(3), new Shiny());
        _world.Spawn(new Player(3));

        Assert.Equal(3, _world.Count<Coin>());
        Assert.Equal(1, _world.Count<Shiny>());
        Assert.Equal(4, _world.Count);
    }

    [Fact]
    public void Count_is_zero_for_a_component_no_entity_ever_had()
    {
        _world.Spawn(new Coin(1));

        Assert.Equal(0, _world.Count<Player>());
    }

    [Fact]
    public void Count_drops_when_an_entity_is_destroyed()
    {
        var coin = _world.Spawn(new Coin(1));
        _world.Spawn(new Coin(2));
        _world.Spawn(new Coin(3));

        _world.Destroy(coin);

        Assert.Equal(2, _world.Count<Coin>());
    }

    [Fact]
    public void Single_gives_the_one_entity_with_the_component()
    {
        _world.Spawn(new Coin(1));
        var player = _world.Spawn(new Player(3), new Shiny());

        Assert.Equal(player, _world.Single<Player>());
        Assert.Equal(3, _world.Get<Player>(_world.Single<Player>()).Lives);
    }

    [Fact]
    public void Single_says_when_there_is_none()
    {
        _world.Spawn(new Coin(1));

        var error = Assert.Throws<InvalidOperationException>(() => _world.Single<Player>());

        Assert.Equal("No entity has a Player.", error.Message);
    }

    [Fact]
    public void Single_says_how_many_there_are_when_there_is_more_than_one()
    {
        _world.Spawn(new Coin(1));
        _world.Spawn(new Coin(2), new Shiny());

        var error = Assert.Throws<InvalidOperationException>(() => _world.Single<Coin>());

        Assert.Equal("2 entities have a Coin, and Single needs exactly one.", error.Message);
    }

    [Fact]
    public void TrySingle_finds_the_one_and_refuses_none_or_many()
    {
        var player = _world.Spawn(new Player(3));
        _world.Spawn(new Coin(1));
        _world.Spawn(new Coin(2));

        Assert.True(_world.TrySingle<Player>(out var found));
        Assert.Equal(player, found);
        Assert.False(_world.TrySingle<Coin>(out _));
        Assert.False(_world.TrySingle<Shiny>(out _));
    }

    [Fact]
    public void TrySingle_refuses_two_that_share_one_archetype()
    {
        _world.Spawn(new Coin(1));
        _world.Spawn(new Coin(2));

        Assert.False(_world.TrySingle<Coin>(out _));
    }
}
