using System.Globalization;
using System.Numerics;
using Rendition;
using Super;
using Super.Ecs;
using Super.Ui;

namespace Forest;

/// <summary>Where the game is. There's only the forest, but its ambience belongs to it.</summary>
public enum Place
{
    Forest,
}

/// <summary>Whether the world is moving or frozen.</summary>
public enum Pause
{
    Running,
    Paused,
}

/// <summary>The builder works on the cabin for a while, then walks on to the next side of it.</summary>
public sealed class Builder
{
    public const float BuildSeconds = 6f;
    public const float WalkSeconds = 4f;
    public const float Radius = 4f;

    /// <summary>The gap between knocks in hammering.wav, so the builder bobs in time with the sound.</summary>
    public const float KnockSeconds = 0.55f;

    private float _seconds;
    private int _side;

    /// <summary>True while hammering, false while walking to the next side of the cabin.</summary>
    public bool IsBuilding { get; private set; } = true;

    /// <summary>Where the builder is, on the ground around the cabin site at the origin.</summary>
    public Vector3 Position
    {
        get
        {
            float quarter = MathF.PI / 2f;
            float angle = _side * quarter;
            float bob = 0f;
            if (IsBuilding)
            {
                bob = 0.2f * MathF.Abs(MathF.Sin(_seconds / KnockSeconds * MathF.PI));
            }
            else
            {
                angle += quarter * (_seconds / WalkSeconds);
            }

            return new Vector3(MathF.Cos(angle) * Radius, 0.4f + bob, MathF.Sin(angle) * Radius);
        }
    }

    /// <summary>Moves on by this many seconds of game time. Returns true when it switched between building and walking.</summary>
    public bool Advance(float seconds)
    {
        _seconds += seconds;
        float length = IsBuilding ? BuildSeconds : WalkSeconds;
        if (_seconds < length)
        {
            return false;
        }

        _seconds -= length;
        IsBuilding = !IsBuilding;
        if (IsBuilding)
        {
            _side++;
        }

        return true;
    }
}

/// <summary>
/// A builder hammering at a cabin in a rainy, windy forest, with music. Space clicks, 1 to 4 cycle the channel
/// volumes, P pauses the world's sounds, and the camera circles the cabin so the hammering pans across the speakers.
/// </summary>
public sealed class ForestGame(
    World world,
    Content content,
    Audio audio,
    GameTime time,
    GameRandom random,
    Input input,
    States<Place> place,
    States<Pause> pause) : IGame
{
    private const int TreeCount = 45;
    private const float OrbitRadiansPerSecond = 0.2f;

    private static readonly float[] Levels = [1f, 0.5f, 0f];
    private static readonly (Key Key, Channel Channel)[] VolumeKeys =
    [
        (Key.D1, Channel.Music),
        (Key.D2, Channel.Ambience),
        (Key.D3, Channel.Effects),
        (Key.D4, Channel.Interface),
    ];

    private readonly int[] _levelOf = new int[VolumeKeys.Length];
    private SoundRef _hammering;
    private SoundRef _accept;
    private Entity _builder;

    public Builder Builder { get; } = new();

    /// <summary>The camera circling the cabin. Tests turn it to hear the hammering pan.</summary>
    public OrbitCamera Camera { get; } = new() { Distance = 16f, Pitch = 0.5f, MinDistance = 6f, MaxDistance = 40f, WideAs = 16f / 9f };

    /// <summary>How fast the camera circles on its own, in radians a second. Dragging with the mouse turns it as well.</summary>
    public float OrbitSpeed { get; set; } = OrbitRadiansPerSecond;

    public UiView? Ui
    {
        get
        {
            string volumes = string.Join("   ", VolumeKeys.Select((entry, index) =>
                string.Create(CultureInfo.InvariantCulture, $"{index + 1} {entry.Channel} {Levels[_levelOf[index]]:0.0}")));
            return new VStack(HorizontalAlignment.Leading, Spacing: 4f)
            {
                new Text(volumes),
                new Text(pause.Is(Pause.Paused) ? "Paused (P)" : "P pauses, Space clicks"),
            }
            .Padding(16f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
        }
    }

    public void Load()
    {
        _hammering = content.Sound("sounds/hammering.wav");
        _accept = content.Sound("sounds/accept.wav");
        var music = content.Sound("music/clearing.ogg");
        var forest = content.Ambience("sounds/forest.ambience.json");

        place.OnEnter(Place.Forest, () =>
        {
            audio.Music(music);

            // The ambience belongs to the forest, so leaving it would stop the rain and wind.
            var ambience = world.Create();
            world.Add(ambience, new Ambience(forest));
            world.Add(ambience, new ScopedTo<Place>(Place.Forest));

            BuildForest();
        });
        pause.OnEnter(Pause.Paused, () => time.Scale = 0f);
        pause.OnExit(Pause.Paused, () => time.Scale = 1f);
    }

    public void Update(GameTime tick)
    {
        if (input.Pressed(Key.P))
        {
            pause.Set(pause.Is(Pause.Paused) ? Pause.Running : Pause.Paused);
        }

        if (input.Pressed(Key.Space))
        {
            audio.Play(_accept);
        }

        for (int i = 0; i < VolumeKeys.Length; i++)
        {
            if (input.Pressed(VolumeKeys[i].Key))
            {
                _levelOf[i] = (_levelOf[i] + 1) % Levels.Length;
                audio.Volume(VolumeKeys[i].Channel, Levels[_levelOf[i]]);
            }
        }

        if (Builder.Advance(tick.DeltaSeconds))
        {
            ChangeWork();
        }

        ref var transform = ref world.Get<Transform>(_builder);
        transform.Position = Builder.Position;

        if (input.Down(MouseButton.Left))
        {
            Camera.Turn(input.MouseDelta * 0.005f);
        }

        Camera.Turn(new Vector2(OrbitSpeed * tick.TickSeconds, 0f));
        Camera.Update(new Vector3(0f, 1f, 0f));
    }

    public void Draw(View view)
    {
        view.Camera(Camera.At(view.Blend));
        view.Sun(new Vector3(0.4f, 1f, 0.3f), new Vector3(1.5f));
        view.Models(world);
    }

    /// <summary>The hammering is a sound on the builder, there for as long as the builder is working.</summary>
    private void ChangeWork()
    {
        if (Builder.IsBuilding)
        {
            world.Add(_builder, new Sound(_hammering, Volume: 1f, Near: 6f, Far: 40f));
        }
        else
        {
            world.Remove<Sound>(_builder);
        }
    }

    private void BuildForest()
    {
        Scoped(Slab(new Vector3(0f, -0.25f, 0f), new Vector3(80f, 0.5f, 80f)), new Vector3(0.25f, 0.4f, 0.2f));
        Scoped(Slab(new Vector3(0f, 0.02f, 0f), new Vector3(5f, 0.05f, 5f)), new Vector3(0.45f, 0.38f, 0.3f));
        Scoped(Slab(new Vector3(0f, 0.7f, 0f), new Vector3(3f, 1.4f, 3f)), new Vector3(0.5f, 0.32f, 0.16f));

        _builder = Scoped(Transform.At(Builder.Position, 0.8f), new Vector3(0.9f, 0.45f, 0.15f));
        world.Add(_builder, new Smoothed());
        ChangeWork();

        var trees = random.Stream("trees");
        for (int i = 0; i < TreeCount; i++)
        {
            float angle = trees.Angle();
            float distance = trees.Between(20f, 36f);
            float height = trees.Between(2.5f, 5f);
            var foot = new Vector3(MathF.Cos(angle) * distance, 0f, MathF.Sin(angle) * distance);
            Scoped(Slab(foot + new Vector3(0f, height / 2f, 0f), new Vector3(0.5f, height, 0.5f)), new Vector3(0.35f, 0.22f, 0.12f));

            float crown = height * 0.6f;
            Scoped(Transform.At(foot + new Vector3(0f, height + crown * 0.3f, 0f), crown), new Vector3(0.1f, trees.Between(0.35f, 0.5f), 0.12f));
        }
    }

    private static Transform Slab(Vector3 position, Vector3 size) => new(position, Quaternion.Identity, size);

    private Entity Scoped(Transform transform, Vector3 color)
    {
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Model(ModelRef.Cube, color));
        world.Add(entity, new ScopedTo<Place>(Place.Forest));
        return entity;
    }
}
