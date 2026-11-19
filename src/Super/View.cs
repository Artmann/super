using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition;
using Super.Ui;

namespace Super;

/// <summary>
/// What's on screen this frame. Draw describes everything visible, every frame, and
/// this class writes it down. It touches no GPU, so it works the same with no window.
/// The models and the camera are set here; the lights, the light that bounces, what surrounds the world and the
/// effects each have their own part: <see cref="Lights"/>, <see cref="Indirect"/>, <see cref="Surroundings"/> and
/// <see cref="Effects"/>.
/// </summary>
public sealed class View
{
    private readonly List<ModelDraw> _models = [];
    private readonly ViewMemory _memory = new();
    private ViewClock _clock = new(0, 0, 0f, 1f, 0f);

    internal Camera Shot { get; private set; }

    internal UiTree? Ui { get; set; }

    /// <summary>What the game named, so drawing something it unloaded fails where the mistake is.</summary>
    internal Content? Content
    {
        get;
        set
        {
            field = value;
            Effects.SurfaceCheck = value is null ? null : surface => ItemChecks.RequireSurface(value.Store, surface);
        }
    }

    /// <summary>The world this view draws, so <see cref="ViewPlacement.TransformOf"/> can find an entity.</summary>
    internal World? World { get; set; }

    /// <summary>The suns, the point and spot lights, and the shadows they cast.</summary>
    public ViewLights Lights { get; } = new();

    /// <summary>The light that bounces: ambient occlusion, screen bounce, light probes, bakes and reflection probes.</summary>
    public ViewIndirect Indirect { get; } = new();

    /// <summary>The sky, the water, the puddles and the wind.</summary>
    public ViewSurroundings Surroundings { get; } = new();

    /// <summary>Particles, decals and fog volumes, and the finish, outlines and transition of the picture.</summary>
    public ViewEffects Effects { get; } = new();

    /// <summary>Everything drawn so far this frame.</summary>
    public ReadOnlySpan<ModelDraw> Drawn => CollectionsMarshal.AsSpan(_models);

    /// <summary>How far the display is between the last tick and the next, from 0 to 1.</summary>
    public float Blend => _clock.Blend;

    /// <summary>Places the camera. Field of view is the vertical angle in radians.</summary>
    public void Camera(Vector3 position, Vector3 lookAt, float fieldOfView = MathF.PI / 4f) =>
        Shot = new Camera(position, lookAt, fieldOfView);

    /// <summary>Places a camera your game keeps, so Update can pick with the same one.</summary>
    public void Camera(in Camera camera) => Shot = camera;

    /// <summary>
    /// Draws a model. The key names it across frames, so use a stable ID from your game.
    /// A color tints the model, as red, green and blue from 0 to 1. It multiplies the
    /// model's own colors and keeps its textures, and costs nothing extra, so every copy
    /// of a model can have its own. The cube is white under a color. For more, such as a swim, some meshes only, a
    /// static draw or a fade, pass <see cref="ModelOptions"/>.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color = null) =>
        Model(key, model, transform, null, new ModelOptions { Color = color });

    /// <summary>Draws a model with a color, a swim, some meshes only, as a static draw or fading out, as the options say.</summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, in ModelOptions options) =>
        Model(key, model, transform, null, options);

    /// <summary>
    /// Draws an animated character in its current pose, with any options but a static draw. The animator's rig must
    /// come from <see cref="Content.Rig"/>, which knows the model; otherwise pass the model and
    /// <see cref="Animator.Pose"/> instead. The skeleton is skinned once for the draw, even when the options show only
    /// some meshes.
    /// </summary>
    public void Model(int key, Animator animator, Matrix4x4 transform, in ModelOptions options = default)
    {
        if (animator.Rig.Model is not { } model)
        {
            throw new InvalidOperationException("The animator's rig wasn't read with content.Rig, so it has no model. Pass the model and the animator's Pose instead.");
        }

        Model(key, model, transform, animator.Pose, options);
    }

    /// <summary>
    /// Draws a model in one flat colour that glows a little, as a preview of where something
    /// will go: green where a building fits, red where it doesn't. It reads the same over
    /// any model and in any light, since the model's own colours and textures are left out.
    /// An opacity below 1 lets the world show through. A ghost casts no shadow. The colour
    /// is linear, from 0 to 1. <paramref name="meshes"/>, when set, shows only the meshes it picks, such as a preview of
    /// a unit in the look it will have; the mask must have been made for this model.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Ghost ghost, MeshMask? meshes = null)
    {
        if (!(ghost.Opacity > 0f && ghost.Opacity <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(ghost), ghost.Opacity, "A ghost's opacity must be more than 0 and at most 1.");
        }

        ModelOptions.CheckMeshes(meshes, model, nameof(meshes));
        Model(key, model, transform, null, new ModelOptions { Meshes = meshes });
        _models[^1] = _models[^1] with { Ghost = ghost };
    }

    /// <summary>
    /// Draws a model in a pose, such as a rigged model in a pose from an <see cref="Animator"/>, with options. The
    /// model must have been cooked with a skeleton, and the pose's rig must be the model's own. Clips keep moving
    /// between ticks, like <see cref="ViewPlacement.Smooth"/> does for transforms. A null pose draws the model as it stands. Throws
    /// if the mask was made for another model, if a static draw swims or has a pose, since a bake holds it where it
    /// stands, or for a fade that ends before it starts.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, AnimationPose? pose, in ModelOptions options = default)
    {
        options.Check(key, pose is not null, nameof(options));

        if (model.Index < -Shapes.BuiltInNames.Length)
        {
            throw new ArgumentException($"Model {model.Index} is not a built-in shape. Use ModelRef.Sphere and the others, or a model from Content.", nameof(model));
        }

        if (Content is { } content && content.Store.IsUnloaded(model.Index))
        {
            throw new InvalidOperationException($"Model {model.Index} was unloaded, so it can't be drawn.");
        }

        ModelOptions.CheckMeshes(options.Meshes, model, nameof(options));

        // So the animator's clip sounds play where the model stands.
        pose?.Drawn = transform;
        _models.Add(new ModelDraw(
            key, model, transform, options.Color, options.Swim, pose, pose is null ? 0f : _clock.PoseAhead, null, options.Meshes, options.Static, options.Fade));
    }

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
        double now = _clock.Now;
        ref var motion = ref _memory.Motions.Touch(new MemorySite(key, file, line), _clock.FrameCount, out bool isNew);
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
        ref var last = ref _memory.Kind<T>().Touch(new MemorySite(key, file, line), _clock.FrameCount, out bool isNew);
        bool changed = !isNew && !EqualityComparer<T>.Default.Equals(last, value);
        last = value;
        return changed;
    }

    /// <summary>
    /// How long ago something happened, given the <see cref="GameTime.Tick"/> your Update
    /// saved at that moment. It's worked out from game time, so it needs no memory.
    /// </summary>
    public float SecondsSince(long tick) => _clock.SecondsSince(tick);

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
        ref var slot = ref _memory.Recordings.Touch(new MemorySite(key, file, line), _clock.FrameCount, out bool isNew);
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
    internal void Begin(float blend, long tick = 0, float tickSeconds = 0f, float timeScale = 1f)
    {
        // What the last frame didn't draw goes now, before this frame can touch it.
        _memory.Forget(_clock.FrameCount);
        _clock = _clock.Next(blend, tick, tickSeconds, timeScale);
        _models.Clear();
        Lights.Clear();
        Indirect.Clear();
        Surroundings.Clear();
        Effects.Clear();
        Camera(new Vector3(0f, 0f, 5f), Vector3.Zero);
    }
}
