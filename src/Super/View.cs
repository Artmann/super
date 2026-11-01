using System.Numerics;
using System.Runtime.CompilerServices;
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
    private readonly ViewMemory _memory = new();
    private long _frame;
    private long _tick;
    private float _tickSeconds;

    internal Camera Shot { get; private set; }
    /// <summary>The time on screen, which sits between the last tick and the next.</summary>
    private double Now => (_tick - 1 + (double)Blend) * _tickSeconds;

    internal ReadOnlySpan<SunLight> Suns => CollectionsMarshal.AsSpan(_suns);
    internal UnderwaterSettings WaterSettings { get; private set; }
    internal PostSettings Post { get; private set; }
    internal float ShadowRadius { get; private set; }
    internal int ShadowCascades { get; private set; }
    internal UiTree? Ui { get; set; }

    /// <summary>What the game named, so drawing something it unloaded fails where the mistake is.</summary>
    internal Content? Content { get; set; }

    /// <summary>Everything drawn so far this frame.</summary>
    public ReadOnlySpan<ModelDraw> Drawn => CollectionsMarshal.AsSpan(_models);

    /// <summary>How far the display is between the last tick and the next, from 0 to 1.</summary>
    public float Blend { get; private set; }

    /// <summary>Places the camera. Field of view is the vertical angle in radians.</summary>
    public void Camera(Vector3 position, Vector3 lookAt, float fieldOfView = MathF.PI / 4f) =>
        Shot = new Camera(position, lookAt, fieldOfView);

    /// <summary>Places a camera your game keeps, so Update can pick with the same one.</summary>
    public void Camera(in Camera camera) => Shot = camera;

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
        Model(key, model, transform, color, default);

    /// <summary>
    /// Draws a model that bends as it swims. Its head must face +z. Unlike a color, this
    /// costs nothing extra, so every fish can have its own.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, SwimMotion swim) =>
        Model(key, model, transform, null, swim);

    /// <summary>Draws a model with both a color and a swimming motion.</summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color, SwimMotion swim)
    {
        if (Content is { } content && content.IsUnloaded(model.Index))
        {
            throw new InvalidOperationException($"Model {model.Index} was unloaded, so it can't be drawn.");
        }

        _models.Add(new ModelDraw(key, model, transform, color, swim));
    }

    /// <summary>Puts the whole view underwater.</summary>
    public void Water(in UnderwaterSettings settings) => WaterSettings = settings;

    /// <summary>Sets the look of the finished picture.</summary>
    public void Finish(in PostSettings settings) => Post = settings;

    /// <summary>
    /// Sets how far around the camera's target the sharpest shadows reach, in metres,
    /// and how many maps to draw, 1 to 4. Each map covers three times the radius of the
    /// one before, so a wide world keeps sharp shadows near the camera.
    /// </summary>
    public void Shadows(float radius, int cascades = 1)
    {
        if (!(radius > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The shadow radius must be more than zero.");
        }

        if (cascades is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(cascades), cascades, "There can be 1 to 4 shadow cascades.");
        }

        ShadowRadius = radius;
        ShadowCascades = cascades;
    }

    /// <summary>
    /// Blends between a thing's transform on the last two ticks, which keeps motion
    /// smooth at any frame rate. It suits small steps, not whole turns in one tick.
    /// </summary>
    public Matrix4x4 Smooth(in Matrix4x4 previous, in Matrix4x4 current) => Matrix4x4.Lerp(previous, current, Blend);

    /// <summary>
    /// Moves a number towards a target over this many seconds, easing in and out, and gives
    /// its value now. The key names it across frames. The first time a key is seen, or the
    /// first time after a frame that didn't draw it, the value is simply the target, so a
    /// loaded game appears finished. Two calls in different places can share a key.
    /// </summary>
    public float Animate(
        int key,
        float target,
        float seconds,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        double now = Now;
        ref var motion = ref _memory.Motions.Touch(new MemorySite(key, file, line), _frame, out bool isNew);
        if (isNew)
        {
            motion = new ViewMotion(target, target, now, seconds);
        }
        else if (motion.To != target)
        {
            motion = new ViewMotion(motion.At(now), target, now, seconds);
        }

        return motion.At(now);
    }

    /// <summary>
    /// Whether a value differs from the one this call saw last frame. It's never true for
    /// a key seen for the first time, so loading a game plays no sounds.
    /// </summary>
    public bool Changed<T>(
        int key,
        T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        ref var last = ref _memory.Kind<T>().Touch(new MemorySite(key, file, line), _frame, out bool isNew);
        bool changed = !isNew && !EqualityComparer<T>.Default.Equals(last, value);
        last = value;
        return changed;
    }

    /// <summary>
    /// How long ago something happened, given the <see cref="GameTime.Tick"/> your Update
    /// saved at that moment. It's worked out from game time, so it needs no memory.
    /// </summary>
    public float SecondsSince(long tick) => MathF.Max((_tick - 1 - tick + Blend) * _tickSeconds, 0f);

    /// <summary>
    /// Runs a function that draws models, then replays them without calling it again until
    /// the version changes. Only models are kept, so put lights and the camera outside it,
    /// along with anything that moves or uses Animate.
    /// </summary>
    public void Cached<TData>(
        int key,
        long version,
        TData data,
        Action<TData, View> draw,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        ref var slot = ref _memory.Recordings.Touch(new MemorySite(key, file, line), _frame, out bool isNew);
        var recording = slot ??= new ViewRecording();
        if (!isNew && recording.Version == version)
        {
            _models.AddRange(CollectionsMarshal.AsSpan(recording.Models));
            return;
        }

        int start = _models.Count;
        draw(data, this);
        recording.Version = version;
        recording.Models.Clear();
        recording.Models.AddRange(CollectionsMarshal.AsSpan(_models)[start..]);
    }

    /// <summary>Clears the last frame, keeping its memory so a running game allocates nothing here.</summary>
    internal void Begin(float blend, long tick = 0, float tickSeconds = 0f)
    {
        // What the last frame didn't draw goes now, before this frame can touch it.
        _memory.Forget(_frame);
        _frame++;
        _tick = tick;
        _tickSeconds = tickSeconds;
        Blend = blend;
        _models.Clear();
        _suns.Clear();
        WaterSettings = default;
        Post = new PostSettings { Vignette = 0.3f, Sharpen = 0.4f };
        ShadowRadius = 8f;
        ShadowCascades = 1;
        Camera(new Vector3(0f, 0f, 5f), Vector3.Zero);
    }
}
