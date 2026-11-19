using System.Numerics;
using Super;
using Super.Ui;

namespace Rooms;

/// <summary>The parts of the game. Each one is a state, and nothing lives outside them but the player.</summary>
public enum Screen
{
    Menu,
    Loading,
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

/// <summary>Something to pick up outside. It's scoped, so those left behind go when the player does.</summary>
public record struct Coin;

/// <summary>What a coin is like, from content/coins. Save the file while playing to change it.</summary>
public record CoinKind(string Id, int Value, float Size);

/// <summary>How something moves, from content/motions: a curve from 0 to 1, played over some seconds.</summary>
public record Motion(string Id, float Seconds, Curve Curve);

/// <summary>A hut the player built outside. It's scoped like the coins.</summary>
public record struct Hut;

/// <summary>A hut on its way up out of the ground. The tween is its height, kept in the world like any other state.</summary>
public struct Rising(TweenValue<float> height)
{
    /// <summary>A field, not a property, so a query can update the tween where it lies.</summary>
    public TweenValue<float> Height = height;
}

/// <summary>A menu, a loading screen, and two areas with one player walking between them.</summary>
public class RoomsGame(World world, Content content, GameRandom random, States<Screen> screen, Actions<Control> actions, Input input) : IGame
{
    public const int CoinsOutside = CoinHunt.Count;
    public const float HouseSize = RoomAreas.HouseSize;
    public const float OutsideSize = RoomAreas.OutsideSize;

    /// <summary>How high a hut's middle stands once it's up, and how far down it starts.</summary>
    public const float HutHeight = HutBuilder.Height;
    public const float HutBuried = HutBuilder.Buried;

    // Models the game draws itself. Entities use IDs from zero, so these can't collide.
    public const int DoorLidKey = -1;
    public const int SparkleKey = CoinHunt.SparkleKey;

    public static readonly Vector3 HouseDoor = RoomAreas.HouseDoor;
    public static readonly Vector3 OutsideDoor = RoomAreas.OutsideDoor;

    private const float DoorWakesWithin = 3f;

    private readonly CoinHunt _coins = new(world, screen, content.LoadAll<CoinKind>("coins"), random.Stream("coins"));
    private readonly HutBuilder _huts = new(world, screen, content.LoadAll<Motion>("motions"));
    private readonly PlayerMover _player = new(world, input, actions);
    private RoomAreas _areas = null!;

    public World World => world;

    public Entity Player => _player.Player;

    /// <summary>Where the camera was on the last tick, which is what a click is measured against.</summary>
    public Camera Camera => _player.Camera.Current;

    /// <summary>Coins picked up. It's only a number on this class, so changing state can't touch it.</summary>
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

        _areas = new RoomAreas(world, screen, content, _player, _coins);
        screen.OnEnter(Screen.Loading, _areas.Load);
        screen.OnEnter(Screen.House, _areas.EnterHouse);
        screen.OnEnter(Screen.Outside, _areas.EnterOutside);
    }

    public void Update(GameTime time)
    {
        switch (screen.Current)
        {
            case Screen.Menu when actions.Pressed(Control.Start):
                screen.Set(Screen.Loading);
                break;

            case Screen.Loading when content.IsLoaded:
                screen.Set(Screen.House);
                break;

            case Screen.House:
                _player.Walk(time, HouseSize);
                if (_player.Reached(HouseDoor))
                {
                    screen.Set(Screen.Outside);
                }

                break;

            case Screen.Outside:
                _player.Walk(time, OutsideSize);
                _coins.Collect(time, _player.Position);
                _huts.Update(time, _player.Position, actions.Pressed(Control.Build));
                if (_player.Reached(OutsideDoor))
                {
                    screen.Set(Screen.House);
                }

                break;
        }
    }

    public void Draw(View view)
    {
        // The menu and loading screens are only text, and the world is empty behind them.
        if (screen.Is(Screen.Menu) || screen.Is(Screen.Loading))
        {
            return;
        }

        view.Camera(_player.Camera.At(view.Blend));
        view.Lights.Sun(new Vector3(1f, 2f, 1f), new Vector3(1.4f));
        DrawDoorLid(view);
        _coins.DrawSparkle(view, _player.Position);
    }

    /// <summary>Where the coins are now.</summary>
    public List<Vector3> CoinPositions() => _coins.Positions();

    /// <summary>Where the huts are now, built or still rising.</summary>
    public List<Vector3> HutPositions() => _huts.Positions();

    /// <summary>The lid lifts as the player comes close. Gameplay never asks how far, so it's view memory.</summary>
    private void DrawDoorLid(View view)
    {
        var door = screen.Is(Screen.House) ? HouseDoor : OutsideDoor;
        bool near = Vector3.Distance(_player.Position, door) < DoorWakesWithin;
        float lift = view.Animate(DoorLidKey, near ? 1f : 0f, seconds: 0.3f);
        var lid = Matrix4x4.CreateScale(1.2f, 0.2f, 1.2f) * Matrix4x4.CreateTranslation(door + new Vector3(0f, 0.6f + lift, 0f));
        view.Model(DoorLidKey, ModelRef.Cube, lid, RoomAreas.DoorColor);
    }
}
