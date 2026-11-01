using System.Numerics;
using Rooms;
using Super;
using Super.Ecs;
using Super.Testing;

namespace Rooms.Tests;

public class RoomsTests
{
    private static HeadlessRun<RoomsGame> Start(int seed) => HeadlessGame.Create<RoomsGame>(seed, services => services.AddSuperEcs());

    private static void Into(HeadlessRun<RoomsGame> run, Screen target)
    {
        run.States<Screen>().Set(target);
        run.Ticks(1);
    }

    private static Vector3 Player(HeadlessRun<RoomsGame> run) => run.Game.World.Get<Transform>(run.Game.Player).Position;

    /// <summary>Walks towards a spot, until the state changes or the player gets there.</summary>
    private static void WalkTo(HeadlessRun<RoomsGame> run, Vector3 target)
    {
        var start = run.States<Screen>().Current;
        for (int i = 0; i < 600 && run.States<Screen>().Is(start); i++)
        {
            var away = target - Player(run);
            if (away.Length() < 0.1f)
            {
                break;
            }

            run.Input.Hold(Control.Move, Vector2.Normalize(new Vector2(away.X, -away.Z)));
            run.Ticks(1);
        }

        run.Input.Release(Control.Move);
        run.Ticks(1);
    }

    [Fact]
    public void The_game_starts_at_the_menu_with_nothing_in_the_world_drawn()
    {
        using var run = Start(1);
        run.Ticks(5);
        Assert.True(run.States<Screen>().Is(Screen.Menu));
        Assert.Empty(run.Drawn);
        Assert.Contains(run.ShownText, shown => shown.Text == "Rooms");
    }

    [Fact]
    public void Starting_goes_through_loading_to_the_house()
    {
        using var run = Start(1);
        run.HoldLoading();
        run.Input.Press(Control.Start);
        run.Ticks(1);
        Assert.True(run.States<Screen>().Is(Screen.Loading));

        run.Ticks(30);
        Assert.True(run.States<Screen>().Is(Screen.Loading));
        Assert.Contains(run.ShownText, shown => shown.Text == "Loading 0%");

        run.FinishLoading();
        run.Ticks(1);
        Assert.True(run.States<Screen>().Is(Screen.House));
    }

    [Fact]
    public void Space_starts_the_game()
    {
        using var run = Start(1);
        run.Input.Press(Key.Space);
        run.Ticks(3);
        Assert.True(run.States<Screen>().Is(Screen.House));
    }

    [Fact]
    public void Walking_through_the_door_goes_outside_and_back()
    {
        using var run = Start(1);
        Into(run, Screen.House);

        WalkTo(run, RoomsGame.HouseDoor);
        Assert.True(run.States<Screen>().Is(Screen.Outside));

        WalkTo(run, RoomsGame.OutsideDoor);
        Assert.True(run.States<Screen>().Is(Screen.House));
    }

    [Fact]
    public void Coming_back_inside_puts_the_player_by_the_door()
    {
        using var run = Start(1);
        Into(run, Screen.Outside);
        Into(run, Screen.House);
        Assert.True(Vector3.Distance(Player(run), RoomsGame.HouseDoor) < 3f);
    }

    [Theory, Seeds(100)]
    public void Arriving_never_lands_on_a_door(int seed)
    {
        using var run = Start(seed);
        Into(run, Screen.House);
        Into(run, Screen.Outside);
        run.Ticks(5);
        Assert.True(run.States<Screen>().Is(Screen.Outside));
        Into(run, Screen.House);
        run.Ticks(5);
        Assert.True(run.States<Screen>().Is(Screen.House));
    }

    [Fact]
    public void The_same_player_carries_between_areas()
    {
        using var run = Start(1);
        var player = run.Game.Player;
        Into(run, Screen.House);
        Into(run, Screen.Outside);
        Into(run, Screen.House);
        Assert.Equal(player, run.Game.Player);
        Assert.True(run.Game.World.IsAlive(player));
    }

    [Fact]
    public void Collected_coins_carry_but_the_uncollected_are_replaced()
    {
        using var run = Start(3);
        Into(run, Screen.Outside);
        var first = run.Game.CoinPositions();
        Assert.Equal(RoomsGame.CoinsOutside, first.Count);

        WalkTo(run, first[0]);
        int carried = run.Game.Coins;
        Assert.InRange(carried, 1, RoomsGame.CoinsOutside);

        Into(run, Screen.House);
        Assert.Empty(run.Game.CoinPositions());
        Assert.Equal(carried, run.Game.Coins);

        Into(run, Screen.Outside);
        Assert.Equal(RoomsGame.CoinsOutside, run.Game.CoinPositions().Count);
    }

    [Theory, Seeds(200)]
    public void Areas_never_leak_entities(int seed)
    {
        using var run = Start(seed);
        Into(run, Screen.House);
        int house = run.Game.World.Count;
        for (int i = 0; i < 5; i++)
        {
            Into(run, Screen.Outside);
            Into(run, Screen.House);
        }

        Assert.Equal(house, run.Game.World.Count);
    }

    [Theory, Seeds(100)]
    public void The_player_stays_on_the_ground(int seed)
    {
        using var run = Start(seed);
        Into(run, Screen.Outside);
        run.Input.Hold(Control.Move, Vector2.Normalize(new Vector2(seed % 7 - 3.5f, 1f)));
        run.Ticks(600);
        Assert.InRange(Player(run).X, -RoomsGame.OutsideSize, RoomsGame.OutsideSize);
        Assert.InRange(Player(run).Z, -RoomsGame.OutsideSize, RoomsGame.OutsideSize);
    }

    [Fact]
    public void A_coin_is_worth_what_its_file_says()
    {
        var files = new MemoryContent { ["coins/gold.json"] = """{ "id": "gold", "value": 5, "size": 0.4 }""" };
        using var run = HeadlessGame.Create<RoomsGame>(3, services => services.AddSuperEcs().AddContentFiles(files));
        Into(run, Screen.Outside);
        WalkTo(run, run.Game.CoinPositions()[0]);
        Assert.Equal(0, run.Game.Coins % 5);
        Assert.InRange(run.Game.Coins, 5, 5 * RoomsGame.CoinsOutside);
    }

    [Fact]
    public void The_same_seed_replays_the_same_coins()
    {
        using var first = Start(9);
        using var second = Start(9);
        Into(first, Screen.Outside);
        Into(second, Screen.Outside);
        Assert.Equal(first.Game.CoinPositions(), second.Game.CoinPositions());
    }
}
