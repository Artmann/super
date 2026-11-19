using System.Numerics;
using Rooms;
using Super;
using Super.Testing;

namespace Rooms.Tests;

public class RoomsTests
{
    private static HeadlessRun<RoomsGame> Start(int seed) => HeadlessGame.Create<RoomsGame>(seed);

    /// <summary>Goes straight to a room, past the menu, with no fade.</summary>
    private static void Into(HeadlessRun<RoomsGame> run, Room room)
    {
        if (!run.States<Screen>().Is(Screen.Playing))
        {
            run.States<Screen>().Set(Screen.Playing);
            run.Ticks(1);
        }

        run.Game.GoTo(room);
        run.Ticks(1);
        Assert.Equal(room, run.Game.CurrentRoom);
    }

    private static Vector3 Player(HeadlessRun<RoomsGame> run) => run.World.Get<Transform>(run.Game.Player).Position;

    /// <summary>Walks towards a spot, until the room changes or the player gets there, and waits out any fade.</summary>
    private static void WalkTo(HeadlessRun<RoomsGame> run, Vector3 target)
    {
        var start = run.Game.CurrentRoom;
        for (int i = 0; i < 600 && run.Game.CurrentRoom == start; i++)
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

        // A door fades to the next room, which is built once the screen is black.
        for (int i = 0; i < 60 && run.Scenes.IsChanging; i++)
        {
            run.Ticks(1);
        }
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
        Assert.True(run.States<Screen>().Is(Screen.Playing));
        Assert.Equal(Room.House, run.Game.CurrentRoom);
    }

    [Fact]
    public void Space_starts_the_game()
    {
        using var run = Start(1);
        run.Input.Press(Key.Space);
        run.Ticks(3);
        Assert.Equal(Room.House, run.Game.CurrentRoom);
    }

    [Fact]
    public void Clicking_the_ground_walks_there()
    {
        using var run = Start(1);
        Into(run, Room.House);

        var pixel = new Vector2(760f, 300f);
        var spot = run.Game.Camera.Ray(pixel, run.ScreenSize).Ground()!.Value;
        run.Input.Click(MouseButton.Left, pixel);
        run.Ticks(120);
        Assert.True(Vector3.Distance(spot, Player(run)) < 0.01f, $"Expected {spot}, got {Player(run)}");
    }

    [Fact]
    public void A_key_takes_over_from_a_click()
    {
        using var run = Start(1);
        Into(run, Room.House);
        run.Input.Click(MouseButton.Left, new Vector2(760f, 300f));
        run.Ticks(2);

        var before = Player(run);
        run.Input.Hold(Control.Move, -Vector2.UnitX);
        run.Ticks(5);
        Assert.True(Player(run).X < before.X);
    }

    [Fact]
    public void The_door_lid_starts_shut_and_lifts_as_the_player_comes_close()
    {
        using var run = Start(1);
        Into(run, Room.House);
        float Lid() => Assert.Single(Lids(run));
        float shut = Lid();

        WalkTo(run, RoomsGame.HouseDoor - new Vector3(0f, 0f, 1.5f));
        run.Ticks(30);
        Assert.Equal(shut + RoomsGame.LidLift, Lid(), 3);
    }

    [Fact]
    public void A_coin_makes_a_sparkle_that_fades()
    {
        using var run = Start(1);
        Into(run, Room.Outside);
        WalkTo(run, run.Game.CoinPositions()[0]);
        Assert.True(run.Game.Coins > 0);
        Assert.Contains(run.Drawn, model => model.Key == RoomsGame.SparkleKey);

        run.Ticks(30);
        Assert.DoesNotContain(run.Drawn, model => model.Key == RoomsGame.SparkleKey);
    }

    [Fact]
    public void Walking_through_the_door_goes_outside_and_back()
    {
        using var run = Start(1);
        Into(run, Room.House);

        WalkTo(run, RoomsGame.HouseDoor);
        Assert.Equal(Room.Outside, run.Game.CurrentRoom);

        WalkTo(run, RoomsGame.OutsideDoor);
        Assert.Equal(Room.House, run.Game.CurrentRoom);
    }

    [Fact]
    public void A_door_fades_out_before_the_next_room_is_built()
    {
        using var run = Start(1);
        Into(run, Room.House);
        run.Input.Hold(Control.Move, -Vector2.UnitY);
        for (int i = 0; i < 120 && !run.Scenes.IsChanging; i++)
        {
            run.Ticks(1);
        }

        run.Input.Release(Control.Move);
        Assert.True(run.Scenes.IsChanging);
        Assert.Equal(Room.House, run.Game.CurrentRoom);

        run.Ticks(30);
        Assert.Equal(Room.Outside, run.Game.CurrentRoom);
    }

    [Fact]
    public void Coming_back_inside_puts_the_player_by_the_door()
    {
        using var run = Start(1);
        Into(run, Room.Outside);
        Into(run, Room.House);
        Assert.True(Vector3.Distance(Player(run), RoomsGame.HouseDoor) < 3f);
    }

    [Fact]
    public void Arriving_never_lands_on_a_door()
    {
        using var run = Start(1);
        Into(run, Room.House);
        Into(run, Room.Outside);
        run.Ticks(5);
        Assert.Equal(Room.Outside, run.Game.CurrentRoom);
        Assert.True(Vector3.Distance(Player(run), RoomsGame.OutsideDoor) > 1f);

        Into(run, Room.House);
        run.Ticks(5);
        Assert.Equal(Room.House, run.Game.CurrentRoom);
    }

    [Fact]
    public void Collected_coins_carry_but_the_uncollected_are_replaced()
    {
        using var run = Start(3);
        Into(run, Room.Outside);
        var first = run.Game.CoinPositions();
        Assert.Equal(RoomsGame.CoinsOutside, first.Count);

        WalkTo(run, first[0]);
        int carried = run.Game.Coins;
        Assert.True(carried > 0);

        Into(run, Room.House);
        Assert.Empty(run.Game.CoinPositions());
        Assert.Equal(carried, run.Game.Coins);

        Into(run, Room.Outside);
        Assert.Equal(RoomsGame.CoinsOutside, run.Game.CoinPositions().Count);
    }

    [Fact]
    public void A_built_hut_rises_past_its_height_and_settles_on_it()
    {
        using var run = Start(1);
        Into(run, Room.Outside);
        run.Input.Press(Control.Build);
        run.Ticks(1);
        float start = Assert.Single(run.Game.HutPositions()).Y;

        float highest = start;
        for (int tick = 0; tick < 40; tick++)
        {
            run.Ticks(1);
            highest = MathF.Max(highest, run.Game.HutPositions()[0].Y);
        }

        // The first tick has already lifted it a little out of the ground.
        Assert.InRange(start, RoomsGame.HutBuried, RoomsGame.HutBuried + 0.2f);

        // The curve in content/motions/rise.json goes to 1.15 of the way, so it overshoots.
        Assert.True(highest > RoomsGame.HutHeight + 0.1f, $"The hut only reached {highest}.");
        Assert.Equal(RoomsGame.HutHeight, run.Game.HutPositions()[0].Y, 0.0001f);
        Assert.Equal(0, Rising(run));
    }

    [Fact]
    public void Huts_stay_outside()
    {
        using var run = Start(1);
        Into(run, Room.Outside);
        run.Input.Press(Control.Build);
        run.Ticks(1);
        Assert.Single(run.Game.HutPositions());

        Into(run, Room.House);

        Assert.Empty(run.Game.HutPositions());
        Assert.Equal(0, Rising(run));
    }

    /// <summary>How high each door lid in the room stands.</summary>
    private static List<float> Lids(HeadlessRun<RoomsGame> run)
    {
        var heights = new List<float>();
        foreach (var row in run.World.Query<DoorLid, Transform>())
        {
            heights.Add(row.Second.Position.Y);
        }

        return heights;
    }

    private static int Rising(HeadlessRun<RoomsGame> run)
    {
        int count = 0;
        foreach (var _ in run.World.Query<Rising>())
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void Areas_never_leak_entities()
    {
        using var run = Start(1);
        Into(run, Room.House);
        int house = run.World.Count;
        for (int i = 0; i < 5; i++)
        {
            Into(run, Room.Outside);
            Into(run, Room.House);
        }

        Assert.Equal(house, run.World.Count);
    }

    // The Z edge past the door is walked on a diagonal, since walking straight at a door goes through it.
    [Theory]
    [InlineData(Room.Outside, 1f, 0f)]
    [InlineData(Room.Outside, -1f, 0f)]
    [InlineData(Room.Outside, 0f, -1f)]
    [InlineData(Room.Outside, 1f, 1f)]
    [InlineData(Room.House, 1f, 0f)]
    [InlineData(Room.House, -1f, 0f)]
    [InlineData(Room.House, 1f, -1f)]
    [InlineData(Room.House, -1f, 1f)]
    public void The_player_stays_on_the_ground_and_in_the_area(Room area, float x, float y)
    {
        using var run = Start(1);
        Into(run, area);
        float size = area == Room.Outside ? RoomsGame.OutsideSize : RoomsGame.HouseSize;
        run.Input.Hold(Control.Move, Vector2.Normalize(new Vector2(x, y)));
        run.Ticks(600);

        var position = Player(run);
        Assert.Equal(area, run.Game.CurrentRoom);
        Assert.InRange(position.X, -size, size);
        Assert.InRange(position.Z, -size, size);
        Assert.Equal(0f, position.Y);
        Assert.True(MathF.Max(MathF.Abs(position.X), MathF.Abs(position.Z)) >= size - 0.01f, $"Only got to {position}.");
    }

    [Fact]
    public void A_coin_is_worth_what_its_file_says()
    {
        var files = new MemoryContent
        {
            ["coins/gold.json"] = """{ "id": "gold", "value": 5, "size": 0.4 }""",
            ["motions/rise.json"] = """{ "id": "rise", "seconds": 0.8, "curve": { "easing": "backOut" } }""",
        };
        using var run = HeadlessGame.Create<RoomsGame>(3, services => services.AddContentFiles(files));
        Into(run, Room.Outside);
        WalkTo(run, run.Game.CoinPositions()[0]);
        Assert.Equal(0, run.Game.Coins % 5);
        Assert.InRange(run.Game.Coins, 5, 5 * RoomsGame.CoinsOutside);
    }

    [Fact]
    public void The_same_seed_replays_the_same_coins()
    {
        using var first = Start(9);
        using var second = Start(9);
        Into(first, Room.Outside);
        Into(second, Room.Outside);
        Assert.Equal(first.Game.CoinPositions(), second.Game.CoinPositions());

        using var other = Start(10);
        Into(other, Room.Outside);
        Assert.NotEqual(first.Game.CoinPositions(), other.Game.CoinPositions());
    }
}
