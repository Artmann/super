using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition;
using Super.Ui;

namespace Super;

/// <summary>
/// A model to draw. It only exists until the next frame describes the world again. A
/// pose skins a rigged model; PoseAhead is how many seconds past the last tick the
/// display is, so clips keep moving between ticks. A ghost draws it in one colour. Meshes,
/// when set, shows only the masked meshes of the model; null shows them all.
/// </summary>
public readonly record struct ModelDraw(
    int Key,
    ModelRef Model,
    Matrix4x4 Transform,
    Vector3? Color,
    SwimMotion Swim = default,
    AnimationPose? Pose = null,
    float PoseAhead = 0f,
    Ghost? Ghost = null,
    MeshMask? Meshes = null);

/// <summary>
/// A light as far away as the sun, shining the same way everywhere. One sun casts the
/// shadows: the one marked with <see cref="CastsShadows"/>, or else the first.
/// </summary>
public readonly record struct SunLight(Vector3 Direction, Vector3 Color, bool CastsShadows = false);

/// <summary>
/// Names a model. <see cref="Content"/> hands these out, and there are built-in shapes one
/// unit wide, centred on zero. Build other sizes with <see cref="Shapes"/>.
/// </summary>
public readonly record struct ModelRef(int Index)
{
    /// <summary>A cube one unit wide, centred on zero.</summary>
    public static ModelRef Cube { get; } = new(0);

    /// <summary>A sphere one unit across.</summary>
    public static ModelRef Sphere { get; } = new(-1);

    /// <summary>A capsule standing up, one unit wide and two tall, like a person.</summary>
    public static ModelRef Capsule { get; } = new(-2);

    /// <summary>A cylinder standing up, one unit wide and one tall.</summary>
    public static ModelRef Cylinder { get; } = new(-3);

    /// <summary>A cone pointing up, one unit wide at the base and one tall.</summary>
    public static ModelRef Cone { get; } = new(-4);

    /// <summary>A one-sided square one unit wide, lying flat and facing up.</summary>
    public static ModelRef Plane { get; } = new(-5);

    /// <summary>A one-sided square one unit wide, standing up and facing along z.</summary>
    public static ModelRef Quad { get; } = new(-6);

    /// <summary>A ring lying flat, one unit across, with a tube a quarter unit thick.</summary>
    public static ModelRef Torus { get; } = new(-7);

    /// <summary>Whether this is the cube or one of the other shapes every game has.</summary>
    public bool IsBuiltIn => Index <= 0;
}

/// <summary>
/// What's on screen this frame. Draw describes everything visible, every frame, and
/// this class writes it down. It touches no GPU, so it works the same with no window.
/// </summary>
public sealed class View
{
    private readonly List<ModelDraw> _models = [];
    private readonly List<SunLight> _suns = [];
    private readonly List<Particle> _particles = [];
    private readonly List<LocalLight> _lights = [];
    private readonly ViewMemory _memory = new();
    private long _frame;
    private long _tick;
    private float _tickSeconds;
    private float _timeScale = 1f;

    internal Camera Shot { get; private set; }
    /// <summary>The time on screen, which sits between the last tick and the next.</summary>
    private double Now => (_tick - 1 + (double)Blend) * _tickSeconds;

    internal ReadOnlySpan<SunLight> Suns => CollectionsMarshal.AsSpan(_suns);
    internal UnderwaterSettings WaterSettings { get; private set; }
    internal SkySettings? SkySettings { get; private set; }
    internal PostSettings Post { get; private set; }
    internal float ShadowRadius { get; private set; }
    internal int ShadowCascades { get; private set; }
    internal float SunSize { get; private set; }
    internal float ShadowContact { get; private set; } = ShadowSettings.DefaultContactLength;
    internal OcclusionSettings Occlusion { get; private set; } = new();
    internal ProbeVolumeSettings? LightProbeVolume { get; private set; }
    internal UiTree? Ui { get; set; }

    /// <summary>How the screen moves between pictures this frame. Set by <see cref="Transitions"/>.</summary>
    internal ScreenTransition Transition { get; set; }

    /// <summary>What the game named, so drawing something it unloaded fails where the mistake is.</summary>
    internal Content? Content { get; set; }

    /// <summary>Everything drawn so far this frame.</summary>
    public ReadOnlySpan<ModelDraw> Drawn => CollectionsMarshal.AsSpan(_models);

    /// <summary>Every point and spot light so far this frame.</summary>
    public ReadOnlySpan<LocalLight> DrawnLights => CollectionsMarshal.AsSpan(_lights);

    /// <summary>Every particle drawn so far this frame, in no particular order.</summary>
    public ReadOnlySpan<Particle> DrawnParticles => CollectionsMarshal.AsSpan(_particles);

    /// <summary>How far the display is between the last tick and the next, from 0 to 1.</summary>
    public float Blend { get; private set; }

    /// <summary>Places the camera. Field of view is the vertical angle in radians.</summary>
    public void Camera(Vector3 position, Vector3 lookAt, float fieldOfView = MathF.PI / 4f) =>
        Shot = new Camera(position, lookAt, fieldOfView);

    /// <summary>Places a camera your game keeps, so Update can pick with the same one.</summary>
    public void Camera(in Camera camera) => Shot = camera;

    /// <summary>
    /// Adds a sun. The direction points from the ground towards it, and needn't be one
    /// unit long. Colors above 1 are brighter. One sun casts the shadows: the one added
    /// with <paramref name="shadows"/> set, or else the first. Marking two throws.
    /// </summary>
    public void Sun(Vector3 direction, Vector3 color, bool shadows = false)
    {
        if (shadows)
        {
            foreach (var sun in _suns)
            {
                if (sun.CastsShadows)
                {
                    throw new InvalidOperationException(
                        "Only one sun can cast shadows, and another already does this frame. Pass shadows: true to one sun only.");
                }
            }
        }

        _suns.Add(new SunLight(Vector3.Normalize(direction), color, shadows));
    }

    /// <summary>
    /// Adds a light that shines every way from a point, such as a candle or a torch. The
    /// color is how bright it is on a surface a metre away, and it dims with the square of
    /// the distance, reaching nothing at the range. The key names it across frames, so its
    /// shadow stays as sharp as it moves; use a stable ID. A frame can have up to
    /// <see cref="LocalLight.MaxPerFrame"/>, and lights with shadows share an atlas, so the
    /// biggest on screen get the sharpest ones. The radius is how big the glowing part is:
    /// raise it for a light right beside a wall, such as a torch in a bracket, so the wall
    /// isn't blown out. It also softens the shadows. See "Point and spot lights" in
    /// docs/views.md.
    /// </summary>
    public void PointLight(
        int key, Vector3 position, Vector3 color, float range, bool shadows = true, float radius = LocalLight.DefaultRadius)
    {
        CheckLight(position, color, range, radius);
        _lights.Add(new LocalLight
        {
            Key = key,
            Position = position,
            Radiance = color,
            Range = range,
            Radius = radius,
            CastsShadow = shadows,
        });
    }

    /// <summary>
    /// Adds a light that shines one way in a cone, such as a lantern's beam. It's full
    /// inside the inner angle and fades to nothing at the outer one; both are measured
    /// from the direction, in radians, and the outer must be under a right angle.
    /// Otherwise it's like <see cref="PointLight"/>, though it costs a sixth as much shadow.
    /// </summary>
    public void SpotLight(
        int key,
        Vector3 position,
        Vector3 direction,
        Vector3 color,
        float range,
        float innerAngle,
        float outerAngle,
        bool shadows = true,
        float radius = LocalLight.DefaultRadius)
    {
        CheckLight(position, color, range, radius);
        if (!(direction.LengthSquared() > 1e-12f) || !float.IsFinite(direction.LengthSquared()))
        {
            throw new ArgumentException($"A spot light needs a direction to shine in, not {direction}.", nameof(direction));
        }

        if (!(outerAngle > 0f && outerAngle < MathF.PI / 2f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outerAngle), outerAngle, "A spot light's outer angle must be more than 0 and less than π/2 radians.");
        }

        if (!(innerAngle >= 0f && innerAngle <= outerAngle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(innerAngle), innerAngle, "A spot light's inner angle must be from 0 up to its outer angle.");
        }

        _lights.Add(new LocalLight
        {
            Key = key,
            Position = position,
            Radiance = color,
            Range = range,
            Radius = radius,
            Direction = Vector3.Normalize(direction),
            InnerAngle = innerAngle,
            OuterAngle = outerAngle,
            CastsShadow = shadows,
        });
    }

    private void CheckLight(Vector3 position, Vector3 color, float range, float radius)
    {
        if (_lights.Count >= LocalLight.MaxPerFrame)
        {
            throw new InvalidOperationException(
                $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights. Leave out those too far away to see.");
        }

        if (!(range > 0f) || !float.IsFinite(range))
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "A light's range must be more than 0 metres.");
        }

        if (!(radius > 0f && radius < range))
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius), radius, "A light's radius must be more than 0 metres and less than its range.");
        }

        if (!float.IsFinite(position.X + position.Y + position.Z))
        {
            throw new ArgumentException($"A light can't be at {position}.", nameof(position));
        }

        if (!(color.X >= 0f && color.Y >= 0f && color.Z >= 0f) || !float.IsFinite(color.X + color.Y + color.Z))
        {
            throw new ArgumentException($"A light's color can't be negative, but it was {color}.", nameof(color));
        }
    }

    /// <summary>
    /// Draws a model. The key names it across frames, so use a stable ID from your game.
    /// A color tints the model, as red, green and blue from 0 to 1. It multiplies the
    /// model's own colors and keeps its textures, and costs nothing extra, so every copy
    /// of a model can have its own. The cube is white under a color.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color = null) =>
        Model(key, model, transform, color, default);

    /// <summary>
    /// Draws a model that bends as it swims. Its head must face +z. Like a color, this
    /// costs nothing extra, so every fish can have its own.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, SwimMotion swim) =>
        Model(key, model, transform, null, swim);

    /// <summary>Draws a model with both a color and a swimming motion.</summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color, SwimMotion swim) =>
        Model(key, model, transform, color, swim, null);

    /// <summary>
    /// Draws only some meshes of a model, the ones <see cref="Content.Meshes"/> picked for
    /// the mask. Meshes it leaves out are not drawn, and cost no triangles. The mask must have
    /// been made for this model.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, MeshMask meshes, Vector3? color = null) =>
        Model(key, model, transform, color, default, null, meshes);

    /// <summary>
    /// Draws some meshes of a rigged model in a pose, as <see cref="Model(int, ModelRef, Matrix4x4, AnimationPose, Vector3?)"/>
    /// does, but only the meshes the mask shows. The whole skeleton is still skinned once.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, AnimationPose pose, MeshMask meshes, Vector3? color = null) =>
        Model(key, model, transform, color, default, pose, meshes);

    /// <summary>
    /// Draws a rigged model in a pose from an <see cref="Animator"/>. The model must have
    /// been cooked with a skeleton, and the pose's rig must be the model's own. Clips
    /// keep moving between ticks, like <see cref="Smooth"/> does for transforms.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, AnimationPose pose, Vector3? color = null) =>
        Model(key, model, transform, color, default, pose);

    /// <summary>
    /// Draws an animated character in its current pose. The animator's rig must come from
    /// <see cref="Content.Rig"/>, which knows the model; otherwise pass the model and
    /// <see cref="Animator.Pose"/> instead.
    /// </summary>
    public void Model(int key, Animator animator, Matrix4x4 transform, Vector3? color = null) =>
        DrawAnimated(key, animator, transform, color, null);

    /// <summary>
    /// Draws an animated character in its current pose, with only the meshes the mask shows.
    /// The skeleton is still skinned once for the draw, not once per part.
    /// </summary>
    public void Model(int key, Animator animator, Matrix4x4 transform, MeshMask meshes, Vector3? color = null) =>
        DrawAnimated(key, animator, transform, color, meshes);

    private void DrawAnimated(int key, Animator animator, Matrix4x4 transform, Vector3? color, MeshMask? meshes)
    {
        if (animator.Rig.Model is not { } model)
        {
            throw new InvalidOperationException("The animator's rig wasn't read with content.Rig, so it has no model. Pass the model and the animator's Pose instead.");
        }

        Model(key, model, transform, color, default, animator.Pose, meshes);
    }

    /// <summary>Draws a model with a color, a swimming motion and a pose, any of which may be left out.</summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color, SwimMotion swim, AnimationPose? pose) =>
        Model(key, model, transform, color, swim, pose, null);

    /// <summary>
    /// The full draw: a color, a swimming motion, a pose and the meshes to show, any of which
    /// may be left out. Throws if the mask was made for another model.
    /// </summary>
    public void Model(int key, ModelRef model, Matrix4x4 transform, Vector3? color, SwimMotion swim, AnimationPose? pose, MeshMask? meshes)
    {
        if (model.Index < -Shapes.BuiltInNames.Length)
        {
            throw new ArgumentException($"Model {model.Index} is not a built-in shape. Use ModelRef.Sphere and the others, or a model from Content.", nameof(model));
        }

        if (Content is { } content && content.IsUnloaded(model.Index))
        {
            throw new InvalidOperationException($"Model {model.Index} was unloaded, so it can't be drawn.");
        }

        if (meshes is { } mask && mask.Model != model.Index)
        {
            throw new ArgumentException($"The mesh mask was made for model {mask.Model}, not model {model.Index}. Make it with Content.Meshes for this model.", nameof(meshes));
        }

        _models.Add(new ModelDraw(key, model, transform, color, swim, pose, pose is null ? 0f : Blend * _tickSeconds * _timeScale, null, meshes));
    }

    /// <summary>
    /// Draws a model in one flat colour that glows a little, as a preview of where something
    /// will go: green where a building fits, red where it doesn't. It reads the same over
    /// any model and in any light, since the model's own colours and textures are left out.
    /// An opacity below 1 lets the world show through. A ghost casts no shadow. The colour
    /// is linear, from 0 to 1.
    /// </summary>
    public void Ghost(int key, ModelRef model, Matrix4x4 transform, Vector3 color, float opacity = 1f) =>
        Ghost(key, model, transform, null, color, opacity);

    /// <summary>
    /// Draws only the meshes a <see cref="MeshMask"/> shows as a ghost, such as a preview of a
    /// unit in the look it will have. The mask must have been made for this model.
    /// </summary>
    public void Ghost(int key, ModelRef model, Matrix4x4 transform, MeshMask? meshes, Vector3 color, float opacity = 1f)
    {
        if (!(opacity > 0f && opacity <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "A ghost's opacity must be more than 0 and at most 1.");
        }

        Model(key, model, transform, null, default, null, meshes);
        _models[^1] = _models[^1] with { Ghost = new Ghost(color, opacity) };
    }

    /// <summary>
    /// Draws a particle effect, such as dust or smoke, at a place in the world. A
    /// <see cref="ParticleEmitter"/> can be passed on its own.
    /// The age is how many seconds ago it started, usually
    /// <c>view.SecondsSince(startTick)</c>; each particle is worked out from the age and
    /// the key alone, so the same key and age always look the same, paused, loaded or
    /// recorded. Use a different key for each burst so they don't look alike. Before the
    /// age is 0 or after <see cref="ParticleEffect.Duration"/> nothing is drawn.
    /// </summary>
    public void Particles(int key, ParticleEffect effect, Vector3 position, float age) =>
        Particles(key, effect, Matrix4x4.CreateTranslation(position), age);

    /// <summary>
    /// Draws a burst of particles moved, turned and stretched by a transform. The spawn
    /// shape and speeds go through it; gravity and sizes stay in the world.
    /// </summary>
    public void Particles(int key, ParticleEffect effect, Matrix4x4 transform, float age)
    {
        ArgumentNullException.ThrowIfNull(effect);
        effect.Emit(key, age, transform, _particles);
    }

    /// <summary>
    /// Draws a burst of particles laid along a line, such as dust where a fallen tree hit
    /// the ground. The effect's x axis runs from one end to the other, scaled so a
    /// <see cref="ParticleEmitter.FromLine"/> one metre long covers it exactly, and its y
    /// axis stays upward. Speeds and sizes keep their size, and an emitter's offset of
    /// -0.5 on x is the start of the line and 0.5 the end.
    /// </summary>
    public void Particles(int key, ParticleEffect effect, Vector3 from, Vector3 to, float age)
    {
        var along = to - from;
        float length = along.Length();
        if (length < 1e-4f)
        {
            Particles(key, effect, (from + to) * 0.5f, age);
            return;
        }

        var x = along / length;
        var up = MathF.Abs(x.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var z = Vector3.Normalize(Vector3.Cross(x, up));
        var y = Vector3.Cross(z, x);
        var middle = (from + to) * 0.5f;
        var transform = new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            middle.X, middle.Y, middle.Z, 1f);
        ArgumentNullException.ThrowIfNull(effect);
        effect.Emit(key, age, transform, _particles, length);
    }

    /// <summary>
    /// Sets the sky: a gradient from the ground up through the horizon to straight up, and
    /// haze that fades distant things into it. Without one the world sits in a plain grey
    /// studio. Water, when set, takes over the sky.
    /// </summary>
    public void Sky(in SkySettings settings)
    {
        if (settings.Brightness < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Brightness, "The sky's brightness can't be negative.");
        }

        if (settings.HazeStart < 0f || settings.HazeEnd < settings.HazeStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"The haze must start at zero or further and end no nearer than it starts, but it runs from {settings.HazeStart} to {settings.HazeEnd} metres.");
        }

        SkySettings = settings;
    }

    /// <summary>Puts the whole view underwater.</summary>
    public void Water(in UnderwaterSettings settings) => WaterSettings = settings;

    /// <summary>Sets the look of the finished picture.</summary>
    public void Finish(in PostSettings settings) => Post = settings;

    /// <summary>
    /// Sets how far around the camera's target the sharpest shadows reach, in metres,
    /// and how many maps to draw, 1 to 4. Each map covers three times the radius of the
    /// one before, so a wide world keeps sharp shadows near the camera.
    /// <paramref name="sunSize"/> is how wide the sun looks, in radians, which sets how fast
    /// its shadows blur with distance from what casts them: 0 is hard, the real sun is about
    /// 0.01, and the default of 0.12 is a soft, hazy sun.
    /// <paramref name="contact"/> is how far, in metres, each pixel looks towards the sun on screen
    /// for something touching it, which darkens the last centimetres where things meet the ground
    /// that the maps are too coarse to see. It works only inside the first map's radius; 0 turns it off.
    /// </summary>
    public void Shadows(float radius, int cascades = 1, float sunSize = DefaultSunSize, float contact = ShadowSettings.DefaultContactLength)
    {
        if (!(radius > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The shadow radius must be more than zero.");
        }

        if (cascades is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(cascades), cascades, "There can be 1 to 4 shadow cascades.");
        }

        if (!(sunSize is >= 0f and <= MaxSunSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sunSize), sunSize, $"The sun's size must be from 0 to {MaxSunSize.ToString(CultureInfo.InvariantCulture)} radians.");
        }

        if (!(float.IsFinite(contact) && contact >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contact), contact, "The contact shadows' length must be 0 or more metres, and 0 turns them off.");
        }

        ShadowRadius = radius;
        ShadowCascades = cascades;
        SunSize = sunSize;
        ShadowContact = contact;
    }

    /// <summary>
    /// Sets how nearby surfaces darken the soft light from the sky in corners, creases and under
    /// things. <paramref name="radius"/> is how far, in metres, a point looks for surfaces that hide
    /// the sky from it; <paramref name="strength"/>, from 0 to 1, is how dark they make it. The
    /// defaults suit rooms and streets seen from head height.
    /// </summary>
    public void AmbientOcclusion(float radius = OcclusionSettings.DefaultRadius, float strength = OcclusionSettings.DefaultStrength)
    {
        if (!(radius > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The ambient occlusion radius must be more than zero.");
        }

        if (!(strength is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "The ambient occlusion strength must be from 0 to 1.");
        }

        Occlusion = new OcclusionSettings(radius, strength);
    }

    /// <summary>
    /// Fills the box from <paramref name="min"/> to <paramref name="max"/> with light probes
    /// <paramref name="spacing"/> metres apart, which give the surfaces inside the light bounced
    /// off the walls, floor and everything else around them, in place of the sky's. A probe
    /// keeps track of how far it sees in each direction, so light doesn't leak through walls. The
    /// probes are drawn again over the next frames whenever the sun, the sky or the box changes.
    /// At most <see cref="ProbeGrid.MaxProbes"/> fit. See docs/rendering.md.
    /// </summary>
    public void LightProbes(Vector3 min, Vector3 max, float spacing)
    {
        if (!(float.IsFinite(spacing) && spacing > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "The light probes' spacing must be more than 0 metres.");
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (!(float.IsFinite(min[axis]) && float.IsFinite(max[axis]) && max[axis] > min[axis]))
            {
                string name = axis == 0 ? "x" : axis == 1 ? "y" : "z";
                throw new ArgumentException(
                    $"The light probes' box must reach further along {name} than it starts, but it runs from {min[axis].ToString(CultureInfo.InvariantCulture)} to {max[axis].ToString(CultureInfo.InvariantCulture)}.",
                    nameof(max));
            }
        }

        var settings = new ProbeVolumeSettings(min, max, spacing);
        long total = ProbeGrid.Total(settings);
        if (total > ProbeGrid.MaxProbes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spacing), spacing,
                $"The box holds {total.ToString(CultureInfo.InvariantCulture)} light probes at this spacing, but at most {ProbeGrid.MaxProbes.ToString(CultureInfo.InvariantCulture)} fit. Space them further apart or make the box smaller.");
        }

        LightProbeVolume = settings;
    }

    /// <summary>How wide the sun looks, in radians, unless <see cref="Shadows"/> says otherwise.</summary>
    public const float DefaultSunSize = 0.12f;

    /// <summary>The widest a sun can look, in radians. Wider would blur shadows away.</summary>
    public const float MaxSunSize = 0.5f;

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
    internal void Begin(float blend, long tick = 0, float tickSeconds = 0f, float timeScale = 1f)
    {
        // What the last frame didn't draw goes now, before this frame can touch it.
        _memory.Forget(_frame);
        _frame++;
        _tick = tick;
        _tickSeconds = tickSeconds;
        _timeScale = timeScale;
        Blend = blend;
        _models.Clear();
        _suns.Clear();
        _particles.Clear();
        _lights.Clear();
        WaterSettings = default;
        SkySettings = null;
        Post = new PostSettings { Vignette = 0.3f, Sharpen = 0.4f };
        ShadowRadius = 8f;
        ShadowCascades = 1;
        SunSize = DefaultSunSize;
        ShadowContact = ShadowSettings.DefaultContactLength;
        Occlusion = new OcclusionSettings();
        LightProbeVolume = null;

        // The hold stays, since changing it is what tells the renderer to keep a picture.
        Transition = new ScreenTransition { Hold = Transition.Hold };
        Camera(new Vector3(0f, 0f, 5f), Vector3.Zero);
    }
}
