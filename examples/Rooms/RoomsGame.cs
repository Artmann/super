using System.Numerics;
using Super;
using Super.Ui;

namespace Rooms;

/// <summary>Where the game is: on the menu, loading, or playing in one of the rooms.</summary>
public enum Screen
{
    Menu,
    Loading,
    Playing,
}

/// <summary>Where in the world the player is. Each room is a scene, built as the player walks in.</summary>
public enum Room
{
    House,
    Outside,
}

/// <summary>What a player can do.</summary>
public enum Control
{
    /// <summary>Leave the menu.</summary>
    Start,

    /// <summary>Walk, in a direction.</summary>
    Move,

    /// <summary>Build a hut beside the player, outside.</summary>
    Build,
}

/// <summary>Something to pick up outside. It belongs to the scene, so those left behind go when the player does.</summary>
public record struct Coin;

/// <summary>What a coin is like, from content/coins. Save the file while playing to change it.</summary>
public record CoinKind(string Id, int Value, float Size);

/// <summary>How something moves, from content/motions: a curve from 0 to 1, played over some seconds.</summary>
public record Motion(string Id, float Seconds, Curve Curve);

/// <summary>A hut the player built outside. It belongs to the scene like the coins.</summary>
public record struct Hut;

/// <summary>A hut on its way up out of the ground. The tween is its height, kept in the world like any other state.</summary>
public struct Rising(TweenValue<float> height)
{
    /// <summary>A field, not a property, so a query can update the tween where it lies.</summary>
    public TweenValue<float> Height = height;
}

/// <summary>The lid over a door, lifted by <paramref name="Lift"/> metres as the player comes close.</summary>
public record struct DoorLid(Vector3 Door, float Lift);

/// <summary>A menu, a loading screen, and two rooms with one player walking between them.</summary>
public class RoomsGame(World world, Content content, GameRandom random, States<Screen> screen, Actions<Control> actions, Input input, Scenes scenes) : IGame
{
    public const int CoinsOutside = CoinHunt.Count;
    public const float HouseSize = RoomAreas.HouseSize;
    public const float OutsideSize = RoomAreas.OutsideSize;

    /// <summary>How high a hut's middle stands once it's up, and how far down it starts.</summary>
    public const float HutHeight = HutBuilder.Height;
    public const float HutBuried = HutBuilder.Buried;

    // The sparkle is the one model the game draws itself. Entities use IDs from zero, so it can't collide.
    public const int SparkleKey = CoinHunt.SparkleKey;

    /// <summary>How far a door's lid lifts.</summary>
    public const float LidLift = RoomAreas.LidLift;

    public static readonly Vector3 HouseDoor = RoomAreas.HouseDoor;
    public static readonly Vector3 OutsideDoor = RoomAreas.OutsideDoor;

    private readonly CoinHunt _coins = new(world, content.LoadAll<CoinKind>("coins"), random.Stream("coins"));
    private readonly HutBuilder _huts = new(world, scenes, content.LoadAll<Motion>("motions"));
    private readonly PlayerMover _player = new(world, input, actions);
    private RoomAreas _areas = null!;

    public Entity Player => _player.Player;

    /// <summary>The room the player is in, which changes as its scene is built. None before the first.</summary>
    public Room? CurrentRoom { get; private set; }

    /// <summary>Where the camera was on the last tick, which is what a click is measured against.</summary>
    public Camera Camera => _player.Camera.Current;

    /// <summary>Coins picked up. It's only a number on this class, so changing rooms can't touch it.</summary>
    public int Coins => _coins.Total;

    /// <summary>Walks outside and builds three huts in a row, catching the last ones on their way up.</summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["building"] = new InputScript()
            .Press(Key.Space).Wait(40)
            .Hold(Key.S).Wait(30).Release(Key.S).Wait(5)
            .Press(Key.E).Hold(Key.D).Wait(8).Release(Key.D)
            .Press(Key.E).Hold(Key.D).Wait(8).Release(Key.D)
            .Press(Key.E).Wait(6),
    };

    public UiView? Ui
    {
        get
        {
            UiView? shown = screen.Current switch
            {
                Screen.Menu => new VStack(HorizontalAlignment.Center, Spacing: 8f)
                {
                    new Text("Rooms").Bold().FontSize(32f),
                    new Text("Press space to start"),
                },
                Screen.Loading => new Text($"Loading {content.Progress * 100f:0}%").FontSize(20f),
                _ => new Text($"Coins: {Coins}").FontSize(20f).Padding(16f)
                    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading),
            };
            return shown.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity);
        }
    }

    public void Load()
    {
        actions.Bind(Control.Start, Key.Space, GamepadButton.South, MouseButton.Left);
        actions.Bind(Control.Move, Keys.WASD, Keys.Arrows, GamepadStick.Left);
        actions.Bind(Control.Build, Key.E, GamepadButton.West);

        _areas = new RoomAreas(world, content, _player, _coins);
        screen.OnEnter(Screen.Loading, _areas.Load);
        screen.OnEnter(Screen.Playing, () => GoTo(Room.House));
    }

    /// <summary>
    /// Goes to a room once this tick ends, through <paramref name="transition"/> if there is one. The room's scene is
    /// built fresh, and everything spawned in the last room goes with it.
    /// </summary>
    public void GoTo(Room room, Transition? transition = null)
    {
        scenes.Go(scene => Build(scene, room), transition);
    }

    public void Update(GameTime time)
    {
        switch (screen.Current)
        {
            case Screen.Menu when actions.Pressed(Control.Start):
                screen.Set(Screen.Loading);
                break;

            case Screen.Loading when content.IsLoaded:
                screen.Set(Screen.Playing);
                break;

            case Screen.Playing when CurrentRoom == Room.House:
                _player.Walk(time, HouseSize);
                Through(HouseDoor, Room.Outside);
                break;

            case Screen.Playing when CurrentRoom == Room.Outside:
                _player.Walk(time, OutsideSize);
                _coins.Collect(time, _player.Position);
                _huts.Update(time, _player.Position, actions.Pressed(Control.Build));
                Through(OutsideDoor, Room.House);
                break;
        }

        if (CurrentRoom is not null)
        {
            _areas.LiftLids(time, _player.Position);
        }
    }

    public void Draw(View view)
    {
        // The menu and loading screens are only text, with no player yet.
        if (CurrentRoom is null)
        {
            return;
        }

        _coins.DrawSparkle(view, _player.Position);
    }

    /// <summary>Where the coins are now.</summary>
    public List<Vector3> CoinPositions() => _coins.Positions();

    /// <summary>Where the huts are now, built or still rising.</summary>
    public List<Vector3> HutPositions() => _huts.Positions();

    private void Build(Scene scene, Room room)
    {
        var from = CurrentRoom;
        CurrentRoom = room;
        if (room == Room.House)
        {
            _areas.House(scene, from);
        }
        else
        {
            _areas.Outside(scene);
        }
    }

    /// <summary>
    /// Fades to <paramref name="next"/> when the player walks into <paramref name="door"/>, unless it's already going. The
    /// new room is built while the screen is black.
    /// </summary>
    private void Through(Vector3 door, Room next)
    {
        if (_player.Reached(door) && !scenes.IsChanging)
        {
            GoTo(next, Transition.Fade(0.5f));
        }
    }

}
