using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super;

/// <summary>
/// The effects in a frame, reached through <see cref="View.Effects"/>: particles, decals and fog volumes in the world,
/// and the finish, the ink outlines and the transition on the picture. Like the rest of a view, it holds only what
/// this frame says.
/// </summary>
public sealed class ViewEffects
{
    private readonly List<Particle> _particles = [];
    private readonly KeyedDraws<DecalRequest> _decals = new(DecalBatch.MaxDecals, "decal", "decals");
    private readonly KeyedDraws<FogVolumeDraw> _fogVolumes = new(FogVolumeBatch.MaxVolumes, "fog volume", "fog volumes");

    internal ViewEffects()
    {
    }

    /// <summary>Every particle drawn so far this frame, in no particular order.</summary>
    public ReadOnlySpan<Particle> DrawnParticles => CollectionsMarshal.AsSpan(_particles);

    /// <summary>The decals placed so far this frame, in the order they are drawn.</summary>
    public ReadOnlySpan<DecalRequest> DrawnDecals => _decals.Items;

    /// <summary>The fog volumes placed so far this frame, in the order they were placed.</summary>
    public ReadOnlySpan<FogVolumeDraw> DrawnFogVolumes => _fogVolumes.Items;

    /// <summary>The finish, with the screen effects set with <see cref="Screen"/> laid over its own.</summary>
    internal PostSettings Post => _finish with { Screen = ScreenEffects.Over(_screen, _finish.Screen) };

    private PostSettings _finish;
    private ScreenEffects _screen;
    internal OutlineSettings OutlineSettings { get; private set; } = new();

    /// <summary>How the screen moves between pictures this frame. Set by <see cref="Transitions"/>.</summary>
    internal ScreenTransition Transition { get; set; }

    /// <summary>
    /// Throws for a surface the game's content doesn't know, so a decal that names one fails where the mistake is.
    /// Null when the view has no content, as in a test.
    /// </summary>
    internal Action<Surface>? SurfaceCheck { get; set; }

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
    /// <see cref="ParticleSpawning.FromLine"/> one metre long covers it exactly, and its y
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
    /// Lays a material onto whatever lies inside a box, such as a damp patch on a wall, rust under a bracket or
    /// a puddle's dark ring, without a mesh of its own. The box is the cube from −0.5 to 0.5 moved by
    /// <paramref name="box"/>: the material spans its X and Z, and it projects down the box's −Y onto every
    /// surface it reaches, so the bumps under it still show. Point the box's Y out of the wall or up from the floor.
    /// The material's colour, surface and normal textures are used, and their opacity says where it shows.
    /// <paramref name="blend"/> says how much of each reaches the surface, the whole material when left out.
    /// Decals are drawn in order. The key names the decal, and each one can be placed once a frame.
    /// At most <see cref="DecalBatch.MaxDecals"/> fit. See docs/views.md.
    /// </summary>
    public void Decal(int key, Matrix4x4 box, Surface material, DecalBlend? blend = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        _decals.Check(key);
        var weights = blend ?? new DecalBlend();
        DecalBatch.Check(box, weights);
        if (material.Shader is not null)
        {
            throw new ArgumentException(
                "A decal can't use a surface shader. Give it a surface made of textures, such as a material graph's.", nameof(material));
        }

        SurfaceCheck?.Invoke(material);
        _decals.Add(key, new DecalRequest(key, box, material, weights));
    }

    /// <summary>
    /// Fills a shape with fog of its own for this frame, such as mist pooled in a room, steam over a pool or the
    /// haze in a lamp's beam, lit by the sun and the lights like the scene's fog. The shape is the unit
    /// <paramref name="shape"/> moved by <paramref name="transform"/>, and <paramref name="settings"/> says how thick
    /// and what colour it is. It shows only while the volumetric fog is on. The key names the volume, and each one can
    /// be placed once a frame. At most <see cref="FogVolumeBatch.MaxVolumes"/> fit. See docs/views.md.
    /// </summary>
    public void FogVolume(int key, FogShape shape, Matrix4x4 transform, in FogVolumeSettings settings)
    {
        _fogVolumes.Check(key);
        FogVolumeBatch.Check(shape, transform, settings);
        _fogVolumes.Add(key, new FogVolumeDraw(shape, transform, settings));
    }

    /// <summary>Sets the look of the finished picture.</summary>
    public void Finish(in PostSettings settings)
    {
        float speed = settings.AdaptationSpeed;
        if (!(float.IsFinite(speed) && speed >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The adaptation speed must be above 0 stops a second, or 0 for the default of 2, but it is {speed}."));
        }

        RefuseOutsideOne(settings.Temperature, "temperature");
        RefuseOutsideOne(settings.Tint, "tint");
        FinishChecks.Grade(settings);
        ThinLens.Check(
            settings.Lens.FocusDistance, settings.Lens.Aperture, ThinLens.FocalLength(settings.Lens.FocalLength), nameof(settings));
        FinishChecks.Screen(settings.Screen, nameof(settings));
        _finish = settings;

        static void RefuseOutsideOne(float value, string name)
        {
            if (!(value is >= -1f and <= 1f))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settings),
                    string.Create(CultureInfo.InvariantCulture, $"The white balance {name} must be from -1 to 1, with 0 for no change, but it is {value}."));
            }
        }
    }

    /// <summary>
    /// Sets screen effects driven from play, such as a red edge while the player is hurt or a flash on a hit. Each
    /// effect set here, anything not zero, wins over the same effect in <see cref="Finish"/>, and the rest stay as the
    /// finish has them, so a hit's flash keeps a level's paper. A <see cref="ScreenEffects"/> component on an entity
    /// does the same. Every amount runs from 0 to 1. See docs/rendering.md.
    /// </summary>
    public void Screen(in ScreenEffects effects)
    {
        FinishChecks.Screen(effects, nameof(effects));
        _screen = effects;
    }

    /// <summary>
    /// Sets the ink lines drawn round surfaces with <see cref="SurfaceFeatures.Outline"/>: their linear colour, black when null,
    /// how many pixels of the picture wide they are, from 0 for none to <see cref="OutlineSettings.MostThickness"/>, and
    /// the distances in metres over which they fade out. Without a call they're black, 1.5 pixels wide and fade from
    /// 30 to 60 metres.
    /// </summary>
    public void Outlines(
        Vector3? color = null,
        float thickness = OutlineSettings.DefaultThickness,
        float fadeStart = OutlineSettings.DefaultFadeStart,
        float fadeEnd = OutlineSettings.DefaultFadeEnd)
    {
        var settings = new OutlineSettings(color ?? Vector3.Zero, thickness, fadeStart, fadeEnd);
        if (settings.Problem() is { } problem)
        {
            throw new ArgumentException(problem);
        }

        OutlineSettings = settings;
    }

    /// <summary>
    /// Forgets the last frame's particles, decals and fog volumes, and puts the picture's look back, as a frame starts.
    /// The transition's hold stays, since changing it is what tells the renderer to keep a picture.
    /// </summary>
    internal void Clear()
    {
        _particles.Clear();
        _decals.Clear();
        _fogVolumes.Clear();
        _finish = new PostSettings { Vignette = 0.3f, Sharpen = 0.4f };
        _screen = default;
        OutlineSettings = new OutlineSettings();
        Transition = new ScreenTransition { Hold = Transition.Hold };
    }
}
