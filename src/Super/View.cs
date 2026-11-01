using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Super.Ui;

namespace Super;

/// <summary>A model to draw. It only exists until the next frame describes the world again.</summary>
public readonly record struct ModelDraw(int Key, ModelRef Model, Matrix4x4 Transform, Vector3? Color, SwimMotion Swim = default);

/// <summary>A light as far away as the sun, shining the same way everywhere.</summary>
public readonly record struct SunLight(Vector3 Direction, Vector3 Color);

/// <summary>Names a model. <see cref="Content"/> hands these out, and there's the built-in cube.</summary>
public readonly record struct ModelRef(int Index)
{
    /// <summary>A cube one unit wide, centred on zero.</summary>
    public static ModelRef Cube { get; } = new(0);
}

/// <summary>
/// What's on screen this frame. Draw describes everything visible, every frame, and
/// this class writes it down. It touches no GPU, so it works the same with no window.
/// </summary>
public sealed class View
{
    private readonly List<ModelDraw> _models = [];
    private readonly List<SunLight> _suns = [];

    internal Vector3 CameraPosition { get; private set; }
    internal Vector3 CameraTarget { get; private set; }
    internal float CameraFieldOfView { get; private set; }
    internal Vector3 CameraUp => new Camera(CameraPosition, CameraTarget).Up;
    internal ReadOnlySpan<SunLight> Suns => CollectionsMarshal.AsSpan(_suns);
    internal UnderwaterSettings WaterSettings { get; private set; }
    internal PostSettings Post { get; private set; }
    internal float ShadowRadius { get; private set; }
    internal UiTree? Ui { get; set; }

    /// <summary>Everything drawn so far this frame.</summary>
    public ReadOnlySpan<ModelDraw> Drawn => CollectionsMarshal.AsSpan(_models);

    /// <summary>How far the display is between the last tick and the next, from 0 to 1.</summary>
    public float Blend { get; private set; }

    /// <summary>Places the camera. Field of view is the vertical angle in radians.</summary>
    public void Camera(Vector3 position, Vector3 lookAt, float fieldOfView = MathF.PI / 4f)
    {
        CameraPosition = position;
        CameraTarget = lookAt;
        CameraFieldOfView = fieldOfView;
    }

    /// <summary>Places a camera your game keeps, so Update can pick with the same one.</summary>
    public void Camera(in Camera camera) => Camera(camera.Position, camera.Target, camera.FieldOfView);

    /// <summary>
    /// Adds a sun. The direction points from the ground towards it, and needn't be one
    /// unit long. Colors above 1 are brighter. The first sun casts the shadows.
    /// </summary>
    public void Sun(Vector3 direction, Vector3 color) => _suns.Add(new SunLight(Vector3.Normalize(direction), color));

    /// <summary>
    /// Draws a model. The key names it across frames, so use a stable ID from your game.
    /// A color replaces the model's own, as red, green and blue from 0 to 1.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color = null) =>
        _models.Add(new ModelDraw(key, model, transform, color));

    /// <summary>Draws a model with both a color and a swimming motion.</summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color, SwimMotion swim) =>
        _models.Add(new ModelDraw(key, model, transform, color, swim));

    /// <summary>
    /// Draws a model that bends as it swims. Its head must face +z. Unlike a color, this
    /// costs nothing extra, so every fish can have its own.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, SwimMotion swim) =>
        _models.Add(new ModelDraw(key, model, transform, null, swim));

    /// <summary>Puts the whole view underwater.</summary>
    public void Water(in UnderwaterSettings settings) => WaterSettings = settings;

    /// <summary>Sets the look of the finished picture.</summary>
    public void Finish(in PostSettings settings) => Post = settings;

    /// <summary>Sets how far around the camera's target shadows reach, in metres.</summary>
    public void Shadows(float radius) => ShadowRadius = radius;

    /// <summary>
    /// Blends between a thing's transform on the last two ticks, which keeps motion
    /// smooth at any frame rate. It suits small steps, not whole turns in one tick.
    /// </summary>
    public Matrix4x4 Smooth(in Matrix4x4 previous, in Matrix4x4 current) => Matrix4x4.Lerp(previous, current, Blend);

    /// <summary>Clears the last frame, keeping its memory so a running game allocates nothing here.</summary>
    internal void Begin(float blend)
    {
        Blend = blend;
        _models.Clear();
        _suns.Clear();
        WaterSettings = default;
        Post = new PostSettings { Vignette = 0.3f, Sharpen = 0.4f };
        ShadowRadius = 8f;
        Camera(new Vector3(0f, 0f, 5f), Vector3.Zero);
    }
}
