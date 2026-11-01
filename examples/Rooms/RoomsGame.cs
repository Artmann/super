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
}

/// <summary>Something to pick up outside. It's scoped, so those left behind go when the player does.</summary>
public record struct Coin;

/// <summary>A menu, a loading screen, and two areas with one player walking between them.</summary>
public class RoomsGame(World world, Content content, GameRandom random, States<Screen> screen, Actions<Control> actions) : IGame
{
    public const int CoinsOutside = 8;
    public const float HouseSize = 5f;
    public const float OutsideSize = 12f;
    public static readonly Vector3 HouseDoor = new(0f, 0f, HouseSize);
    public static readonly Vector3 OutsideDoor = new(0f, 0f, -OutsideSize);

    private const float WalkPerSecond = 6f;
    private const float Reach = 1f;
    private const float ArrivalGap = 2f;
    private const int GroundTextureSize = 512;
    private static readonly Vector3 PlayerColor = new(0.9f, 0.45f, 0.15f);
    private static readonly Vector3 FloorColor = new(0.45f, 0.33f, 0.22f);
    private static readonly Vector3 DoorColor = new(0.2f, 0.5f, 0.8f);
    private static readonly Vector3 CoinColor = new(1f, 0.8f, 0.1f);

    private readonly RandomStream _coinSpots = random.Stream("coins");
    private readonly List<Entity> _collected = [];
    private ModelRef? _grass;

    public World World => world;

    public Entity Player { get; private set; }

    /// <summary>Coins picked up. It's only a number on this class, so changing state can't touch it.</summary>
    public int Coins { get; private set; }

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
                LeaveThrough(HouseDoor, Screen.Outside);
                break;

            case Screen.Outside:
                Walk(time, OutsideSize);
                CollectCoins();
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

        float distance = screen.Is(Screen.House) ? 12f : 24f;
        view.Camera(new Vector3(0f, distance, distance), Vector3.Zero);
        view.Sun(new Vector3(1f, 2f, 1f), new Vector3(1.4f));
        view.Models(world);
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

    private void EnterHouse()
    {
        Scoped(Flat(HouseSize), FloorColor);
        Scoped(Transform.At(HouseDoor), DoorColor);

        // Coming from outside means coming through the door.
        var arrival = screen.Previous == Screen.Outside ? HouseDoor - new Vector3(0f, 0f, ArrivalGap) : Vector3.Zero;
        Move(arrival);
    }

    private void EnterOutside()
    {
        var ground = world.Create();
        world.Add(ground, Flat(OutsideSize));
        world.Add(ground, new Model(_grass ?? ModelRef.Cube));
        world.Add(ground, new ScopedTo<Screen>(Screen.Outside));
        Scoped(Transform.At(OutsideDoor), DoorColor);

        for (int i = 0; i < CoinsOutside; i++)
        {
            float x = _coinSpots.Float() * 2f * (OutsideSize - 1f) - (OutsideSize - 1f);
            float z = _coinSpots.Float() * 2f * (OutsideSize - 3f) - (OutsideSize - 5f);
            var coin = Scoped(Transform.At(new Vector3(x, 0f, z), 0.4f), CoinColor);
            world.Add(coin, new Coin());
        }

        Move(OutsideDoor + new Vector3(0f, 0f, ArrivalGap));
    }

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
    }

    private void Walk(GameTime time, float size)
    {
        var move = actions.Vector(Control.Move) * (WalkPerSecond * time.TickSeconds);
        ref var transform = ref world.Get<Transform>(Player);
        var next = transform.Position + new Vector3(move.X, 0f, -move.Y);
        transform.Position = new Vector3(Math.Clamp(next.X, -size, size), 0f, Math.Clamp(next.Z, -size, size));
    }

    private void LeaveThrough(Vector3 door, Screen next)
    {
        if (Vector3.Distance(world.Get<Transform>(Player).Position, door) < Reach)
        {
            screen.Set(next);
        }
    }

    private void CollectCoins()
    {
        var player = world.Get<Transform>(Player).Position;
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
            Coins++;
        }

        _collected.Clear();
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
