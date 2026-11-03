using System.Numerics;
using Rendition;
using Super;
using Super.Ecs;
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
public struct Rising(Tween<float> height)
{
    /// <summary>A field, not a property, so a query can update the tween where it lies.</summary>
    public Tween<float> Height = height;
}

/// <summary>A menu, a loading screen, and two areas with one player walking between them.</summary>
public class RoomsGame(World world, Content content, GameRandom random, States<Screen> screen, Actions<Control> actions, Input input) : IGame
{
    public const int CoinsOutside = 8;
    public const float HouseSize = 5f;
    public const float OutsideSize = 12f;

    /// <summary>How high a hut's middle stands once it's up, and how far down it starts.</summary>
    public const float HutHeight = 0.1f;
    public const float HutBuried = -1.3f;

    // Models the game draws itself. Entities use IDs from zero, so these can't collide.
    public const int DoorLidKey = -1;
    public const int SparkleKey = -2;

    public static readonly Vector3 HouseDoor = new(0f, 0f, HouseSize);
    public static readonly Vector3 OutsideDoor = new(0f, 0f, -OutsideSize);

    private const float WalkPerSecond = 6f;
    private const float Reach = 1f;
    private const float ArrivalGap = 2f;
    private const float ArrivedWithin = 0.1f;
    private const float DoorWakesWithin = 3f;
    private const float SparkleSeconds = 0.4f;
    private const int GroundTextureSize = 512;

    private static readonly Vector3 PlayerColor = new(0.9f, 0.45f, 0.15f);
    private static readonly Vector3 FloorColor = new(0.45f, 0.33f, 0.22f);
    private static readonly Vector3 DoorColor = new(0.2f, 0.5f, 0.8f);
    private static readonly Vector3 CoinColor = new(1f, 0.8f, 0.1f);
    private static readonly Vector3 HutColor = new(0.75f, 0.3f, 0.3f);
    private static readonly Vector3 HutSpot = new(1.8f, 0f, 0f);

    private readonly Catalog<CoinKind> _coins = content.LoadAll<CoinKind>("coins");
    private readonly Catalog<Motion> _motions = content.LoadAll<Motion>("motions");
    private readonly RandomStream _coinSpots = random.Stream("coins");
    private readonly List<Entity> _collected = [];
    private readonly List<Entity> _risen = [];
    private readonly FollowCamera _camera = new();
    private Vector3? _walkingTo;
    private long? _lastCoinTick;
    private ModelRef? _grass;

    public World World => world;

    public Entity Player { get; private set; }

    /// <summary>Where the camera was on the last tick, which is what a click is measured against.</summary>
    public Camera Camera => _camera.Current;

    /// <summary>Coins picked up. It's only a number on this class, so changing state can't touch it.</summary>
    public int Coins { get; private set; }

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

        // Not scoped to anything, so the player carries through every state.
        Player = world.Create();
        world.Add(Player, Transform.At(Vector3.Zero, 0.8f));
        world.Add(Player, new Model(ModelRef.Cube, PlayerColor));
        world.Add(Player, new Smoothed());

        screen.OnEnter(Screen.Loading, () => _grass ??= content.Mesh(Grass()));
        screen.OnEnter(Screen.House, EnterHouse);
        screen.OnEnter(Screen.Outside, EnterOutside);
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
                Walk(time, HouseSize);
                _camera.Update(Position, time.TickSeconds);
                LeaveThrough(HouseDoor, Screen.Outside);
                break;

            case Screen.Outside:
                Walk(time, OutsideSize);
                _camera.Update(Position, time.TickSeconds);
                CollectCoins(time);
                BuildHuts(time);
                LeaveThrough(OutsideDoor, Screen.House);
                break;
        }
    }

    public void Draw(View view)
    {
        // The menu and loading screens are only text, and the world waits behind them.
        if (screen.Is(Screen.Menu) || screen.Is(Screen.Loading))
        {
            return;
        }

        view.Camera(_camera.At(view.Blend));
        view.Sun(new Vector3(1f, 2f, 1f), new Vector3(1.4f));
        view.Models(world);
        DrawDoorLid(view);
        DrawSparkle(view);
    }

    /// <summary>The lid lifts as the player comes close. Gameplay never asks how far, so it's view memory.</summary>
    private void DrawDoorLid(View view)
    {
        var door = screen.Is(Screen.House) ? HouseDoor : OutsideDoor;
        bool near = Vector3.Distance(Position, door) < DoorWakesWithin;
        float lift = view.Animate(DoorLidKey, near ? 1f : 0f, seconds: 0.3f);
        var lid = Matrix4x4.CreateScale(1.2f, 0.2f, 1.2f) * Matrix4x4.CreateTranslation(door + new Vector3(0f, 0.6f + lift, 0f));
        view.Model(DoorLidKey, ModelRef.Cube, lid, DoorColor);
    }

    /// <summary>A flash above the player, shrinking as the last coin gets older.</summary>
    private void DrawSparkle(View view)
    {
        if (_lastCoinTick is not { } tick || view.SecondsSince(tick) >= SparkleSeconds)
        {
            return;
        }

        float size = 0.5f * (1f - view.SecondsSince(tick) / SparkleSeconds);
        var sparkle = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(Position + new Vector3(0f, 1.2f, 0f));
        view.Model(SparkleKey, ModelRef.Cube, sparkle, CoinColor);
    }

    /// <summary>Where the coins are now.</summary>
    public List<Vector3> CoinPositions()
    {
        var positions = new List<Vector3>();
        foreach (var row in world.Query<Transform, Coin>())
        {
            positions.Add(row.First.Position);
        }

        return positions;
    }

    /// <summary>Where the huts are now, built or still rising.</summary>
    public List<Vector3> HutPositions()
    {
        var positions = new List<Vector3>();
        foreach (var row in world.Query<Transform, Hut>())
        {
            positions.Add(row.First.Position);
        }

        return positions;
    }

    private void EnterHouse()
    {
        _camera.Offset = new Vector3(0f, 10f, 10f);
        Scoped(Flat(HouseSize), FloorColor);
        Scoped(Transform.At(HouseDoor), DoorColor);

        // Coming from outside means coming through the door.
        var arrival = screen.Previous == Screen.Outside ? HouseDoor - new Vector3(0f, 0f, ArrivalGap) : Vector3.Zero;
        Move(arrival);
    }

    private void EnterOutside()
    {
        _camera.Offset = new Vector3(0f, 14f, 14f);
        var ground = world.Create();
        world.Add(ground, Flat(OutsideSize));
        world.Add(ground, new Model(_grass ?? ModelRef.Cube));
        world.Add(ground, new ScopedTo<Screen>(Screen.Outside));
        Scoped(Transform.At(OutsideDoor), DoorColor);

        for (int i = 0; i < CoinsOutside; i++)
        {
            float x = _coinSpots.Float() * 2f * (OutsideSize - 1f) - (OutsideSize - 1f);
            float z = _coinSpots.Float() * 2f * (OutsideSize - 3f) - (OutsideSize - 5f);
            var coin = Scoped(Transform.At(new Vector3(x, 0f, z), _coins["gold"].Size), CoinColor);
            world.Add(coin, new Coin());
        }

        Move(OutsideDoor + new Vector3(0f, 0f, ArrivalGap));
    }

    private Vector3 Position => world.Get<Transform>(Player).Position;

    private Entity Scoped(Transform transform, Vector3 color)
    {
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Model(ModelRef.Cube, color));
        world.Add(entity, new ScopedTo<Screen>(screen.Current));
        return entity;
    }

    private static Transform Flat(float size) =>
        new(new Vector3(0f, -0.6f, 0f), Quaternion.Identity, new Vector3(size * 2f, 0.2f, size * 2f));

    private void Move(Vector3 position)
    {
        ref var transform = ref world.Get<Transform>(Player);
        transform.Position = position;

        // A move between areas is a teleport, so nothing should slide across the map.
        _walkingTo = null;
        _camera.Jump();
        _camera.Update(position, 0f);
    }

    private void Walk(GameTime time, float size)
    {
        if (input.Pressed(MouseButton.Left))
        {
            _walkingTo = input.MouseRay(_camera.Current).Ground();
        }

        var stick = actions.Vector(Control.Move);
        if (stick != Vector2.Zero)
        {
            _walkingTo = null;
        }

        var step = new Vector3(stick.X, 0f, -stick.Y) * (WalkPerSecond * time.TickSeconds);
        ref var transform = ref world.Get<Transform>(Player);
        if (_walkingTo is { } spot)
        {
            var away = spot - transform.Position;
            float reach = WalkPerSecond * time.TickSeconds;
            step = away.Length() < MathF.Max(reach, ArrivedWithin) ? away : Vector3.Normalize(away) * reach;
        }

        var next = transform.Position + step;
        transform.Position = new Vector3(Math.Clamp(next.X, -size, size), 0f, Math.Clamp(next.Z, -size, size));
    }

    private void LeaveThrough(Vector3 door, Screen next)
    {
        if (Vector3.Distance(world.Get<Transform>(Player).Position, door) < Reach)
        {
            screen.Set(next);
        }
    }

    private void CollectCoins(GameTime time)
    {
        var player = Position;
        foreach (var row in world.Query<Transform, Coin>())
        {
            if (Vector3.Distance(row.First.Position, player) < Reach)
            {
                _collected.Add(row.Entity);
            }
        }

        // The world can't change shape during a query, so these go afterwards.
        foreach (var coin in _collected)
        {
            world.Destroy(coin);
            Coins += _coins["gold"].Value;
            _lastCoinTick = time.Tick;
        }

        _collected.Clear();
    }

    /// <summary>Puts a hut beside the player when asked, and raises every hut that's still on its way up.</summary>
    private void BuildHuts(GameTime time)
    {
        if (actions.Pressed(Control.Build))
        {
            var rise = _motions["rise"];
            var spot = Position + HutSpot;
            var hut = Scoped(Transform.At(spot with { Y = HutBuried }, 1.2f), HutColor);
            world.Add(hut, new Hut());
            world.Add(hut, new Smoothed());
            world.Add(hut, new Rising(Tween.Between(HutBuried, HutHeight, rise.Seconds).Ease(rise.Curve)));
        }

        foreach (var row in world.Query<Transform, Rising>())
        {
            ref var height = ref row.Second.Height;
            height.Update(time);
            row.First.Position = row.First.Position with { Y = height.Value };
            if (height.Completed)
            {
                _risen.Add(row.Entity);
            }
        }

        // A hut that's up has nothing left to do, so it stops being asked.
        foreach (var hut in _risen)
        {
            world.Remove<Rising>(hut);
        }

        _risen.Clear();
    }

    /// <summary>A checkered square, large enough that building it is worth doing in the background.</summary>
    private static GeneratedMesh Grass()
    {
        byte[] pixels = new byte[GroundTextureSize * GroundTextureSize * 4];
        for (int y = 0; y < GroundTextureSize; y++)
        {
            for (int x = 0; x < GroundTextureSize; x++)
            {
                bool light = (x / 32 + y / 32) % 2 == 0;
                int pixel = (y * GroundTextureSize + x) * 4;
                pixels[pixel] = (byte)(light ? 90 : 70);
                pixels[pixel + 1] = (byte)(light ? 150 : 125);
                pixels[pixel + 2] = 60;
                pixels[pixel + 3] = 255;
            }
        }

        var up = Vector3.UnitY;
        var along = new Vector4(1f, 0f, 0f, 1f);
        Vertex[] vertices =
        [
            new(new Vector3(-0.5f, 0.5f, -0.5f), up, along, new Vector2(0f, 0f)),
            new(new Vector3(0.5f, 0.5f, -0.5f), up, along, new Vector2(1f, 0f)),
            new(new Vector3(0.5f, 0.5f, 0.5f), up, along, new Vector2(1f, 1f)),
            new(new Vector3(-0.5f, 0.5f, 0.5f), up, along, new Vector2(0f, 1f)),
        ];
        return new GeneratedMesh(vertices, [0, 2, 1, 0, 3, 2], new GeneratedTexture(GroundTextureSize, pixels), 0.9f);
    }
}
