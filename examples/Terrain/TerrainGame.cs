using System.Numerics;
using Rendition;
using Super;

namespace Terrain;

/// <summary>What a player can do: fly around.</summary>
public enum Control
{
    /// <summary>Move along the ground plane, forward being positive y.</summary>
    Move,

    /// <summary>Turn the camera, in radians.</summary>
    Look,

    /// <summary>Climb or sink.</summary>
    Rise,

    /// <summary>Plays a flyover that rides a rail above the road, watching the cart.</summary>
    Flyover,
}

/// <summary>
/// Endless hills made from noise, textured by height and slope. Chunks appear around the
/// camera as it flies and are unloaded behind it. A road made from a spline loops round
/// the start, and F plays a flyover along it.
/// </summary>
public sealed class TerrainGame(GameRandom random, Actions<Control> actions, Input input, Content content, Director director) : IGame
{
    private const float FlyoverSeconds = 8f;

    internal const int ChunkQuads = TerrainChunks.ChunkQuads;
    internal const float Spacing = Hills.Spacing;
    internal const float ChunkSize = TerrainChunks.ChunkSize;
    internal const int LoadRadius = TerrainChunks.LoadRadius;
    internal const int UnloadRadius = TerrainChunks.UnloadRadius;
    private const float Speed = 24f;
    private const float TurnPerPixel = 0.0025f;
    private const float TurnPerSecond = 2.5f;

    private Hills _hills = null!;
    private TerrainChunks _chunks = null!;
    private Vector3 _position;
    private Vector3 _previousPosition;
    private Road _road = null!;
    private float _yaw;
    private float _pitch = -0.25f;
    private float _previousYaw;
    private float _previousPitch;

    /// <summary>The camera's place in the world.</summary>
    internal Vector3 Position => _position;

    /// <summary>Which chunks are loaded right now, by chunk coordinates.</summary>
    internal IReadOnlyCollection<(int X, int Z)> LoadedChunks => _chunks.Loaded;

    /// <summary>How high the ground is anywhere.</summary>
    internal float GroundHeight(float x, float z) => _hills.Field.Height(x, z);

    /// <summary>The road round the start, and the cart on it.</summary>
    internal Road Road => _road;

    /// <summary>A flyover: press F, then wait for the camera to ride the rail.</summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["flyover"] = new InputScript().Press(Key.F).Wait(120),
    };

    public void Load()
    {
        actions.Bind(Control.Move, Keys.WASD, Keys.Arrows, GamepadStick.Left);
        actions.Bind(Control.Look, Binding.MouseDelta.Scaled(TurnPerPixel), ((Binding)GamepadStick.Right).Scaled(TurnPerSecond / 30f));
        actions.Bind(Control.Rise, Binding.Axis((Binding)Key.LeftShift, (Binding)Key.Space), Binding.Axis((Binding)GamepadTrigger.Left, (Binding)GamepadTrigger.Right));
        actions.Bind(Control.Flyover, Key.F, GamepadButton.North);

        var ground = new Noise(random.Stream("ground"));
        var moisture = new Noise(random.Stream("moisture"));
        _hills = new Hills(ground);
        var shader = content.Shader("terrain");
        var layers = GroundLayers.Make(content, random.Stream("looks"));
        _chunks = new TerrainChunks(content, _hills, moisture, shader, layers);

        _position = new Vector3(0f, _hills.Height(0f, 0f) + 14f, 0f);
        _previousPosition = _position;
        _previousYaw = _yaw;
        _previousPitch = _pitch;
        _road = new Road(content, _hills.Height);
        _chunks.Update(_position);
    }

    public void Update(GameTime time)
    {
        input.Mouse.Locked = true;
        _previousPosition = _position;
        _previousYaw = _yaw;
        _previousPitch = _pitch;

        var look = actions.Vector(Control.Look);
        _yaw -= look.X;
        _pitch = Math.Clamp(_pitch - look.Y, -1.5f, 1.5f);

        var move = actions.Vector(Control.Move);
        var forward = Forward(_yaw, 0f);
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var step = (forward * move.Y + right * move.X) * (Speed * time.TickSeconds);
        step.Y += actions.Value(Control.Rise) * Speed * 0.6f * time.TickSeconds;
        _position += step;

        // Stay a little above the ground even when diving.
        float floor = _hills.Height(_position.X, _position.Z) + 2f;
        if (_position.Y < floor)
        {
            _position.Y = floor;
        }

        _chunks.Update(_position);
        _road.Update(time);
        if (actions.Pressed(Control.Flyover))
        {
            director.Play(new Cutscene().Then(Shot.Along(FlyoverSeconds, Rail(), () => _road.Cart, look: new Vector3(0f, 1f, 0f))));
        }
    }

    public void Draw(View view)
    {
        var position = Vector3.Lerp(_previousPosition, _position, view.Blend);
        float yaw = _previousYaw + (_yaw - _previousYaw) * view.Blend;
        float pitch = _previousPitch + (_pitch - _previousPitch) * view.Blend;
        view.Camera(position, position + Forward(yaw, pitch), 60f * MathF.PI / 180f);
        view.Lights.Sun(new Vector3(0.5f, 0.8f, 0.3f), new Vector3(3.6f, 3.4f, 3.1f));
        view.Lights.Shadows(40f, 3);
        view.Effects.Finish(new PostSettings { Contrast = 0.08f, Saturation = 0.08f, Vignette = 0.25f });
        _chunks.Draw(view);
        _road.Draw(view);
    }

    /// <summary>
    /// A rail for the flyover camera: it starts high and wide behind the cart, then swoops
    /// down beside the road ahead of it, so the cart drives through the shot.
    /// </summary>
    private Spline Rail()
    {
        var path = _road.Path;
        float start = _road.CartDistance;
        var points = new Vector3[6];
        for (int i = 0; i < points.Length; i++)
        {
            float share = i / (points.Length - 1f);
            var pose = path.FrameAtDistance(start + (share * 1.2f - 0.1f) * Road.CartSpeed * FlyoverSeconds);
            float outwards = float.Lerp(30f, 9f, share);
            float above = float.Lerp(28f, 3f, share * share);
            points[i] = pose.Position + pose.Right * outwards + new Vector3(0f, above, 0f);
        }

        return Spline.Through(points);
    }

    private static Vector3 Forward(float yaw, float pitch) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
}
