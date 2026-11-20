using System.Numerics;
using Super;

namespace Rooms;

/// <summary>The player: a cube that walks where the keys or a click send it, with a camera following it.</summary>
internal sealed class PlayerMover(World world, Input input, Actions<Control> actions)
{
    private const float WalkPerSecond = 6f;
    private const float DoorWithin = 1f;
    private const float ArrivedWithin = 0.1f;

    private static readonly Vector3 PlayerColor = new(0.9f, 0.45f, 0.15f);

    private Vector3? _walkingTo;

    private bool _spawned;

    /// <summary>The player's entity, once the first room has made it.</summary>
    public Entity Player { get; private set; }

    /// <summary>The camera that follows the player.</summary>
    public FollowCamera Camera { get; } = new();

    /// <summary>Where the player is.</summary>
    public Vector3 Position => world.Get<Transform>(Player).Position;

    /// <summary>
    /// Makes the player and its camera, with the first room, so the menu and the loading screen show an empty world.
    /// They're spawned into the world rather than a scene, so they carry through every room.
    /// </summary>
    private void Spawn()
    {
        _spawned = true;
        Player = world.Spawn(Transform.At(Vector3.Zero, 0.8f), new Model(ModelRef.Cube, PlayerColor));
        world.Spawn(new OnRig(Camera));
    }

    /// <summary>Puts the player at <paramref name="position"/> at once, making it the first time.</summary>
    public void Move(Vector3 position)
    {
        if (!_spawned)
        {
            Spawn();
        }

        // A move between areas is a teleport, so nothing should slide across the map.
        world.Teleport(Player, world.Get<Transform>(Player) with { Position = position });
        _walkingTo = null;
        Camera.Jump();
        Camera.Update(position, 0f);
    }

    /// <summary>Walks one tick, staying within <paramref name="size"/> of the middle, and moves the camera after.</summary>
    public void Walk(GameTime time, float size)
    {
        if (input.Pressed(MouseButton.Left))
        {
            _walkingTo = input.Mouse.Ray(Camera.Current).Ground();
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
        Camera.Update(Position, time.TickSeconds);
    }

    /// <summary>Whether the player has come to <paramref name="door"/>.</summary>
    public bool Reached(Vector3 door) => Vector3.Distance(world.Get<Transform>(Player).Position, door) < DoorWithin;
}
