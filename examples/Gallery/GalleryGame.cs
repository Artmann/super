using System.Numerics;
using Examples;
using Rendition;
using Super;
using Super.Ui;

namespace Gallery;

/// <summary>
/// Plaster rooms along one corridor, lit the way the engine lights things today: a sun, a sky,
/// light probes, reflection probes and ordinary point lights. An entrance hall with a long
/// skylight opens onto a corridor whose colonnade throws shadows all the way down it. Off the
/// corridor are a colour room with a marble bust between a red wall and a blue one, a whale
/// room with a bronze whale under a skylight, a dark lamp room, and a chapel lit by a swinging
/// lantern and candles. A courtyard waits at the end behind tinted glass. The camera tours the
/// rooms on its own; F lets you fly it with WASD and the mouse, and F again goes back to the tour.
/// Keys 1, 2 and 3 switch between live lighting, the bake, and direct light only. Where things
/// stand is in <see cref="GalleryPlan"/>.
/// </summary>
public sealed class GalleryGame(World world, Content content, Input input, Graphics graphics) : IGame
{
    /// <summary>Key presses for profiling the reflection probes catching up. See <see cref="LiveChanges.Scripts"/>.</summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => LiveChanges.Scripts;

    // The box the light probes fill, which is also the baked space "gallery".
    private static readonly Vector3 ProbesMin = new(-11.8f, -0.4f, -56.6f);
    private static readonly Vector3 ProbesMax = new(7.8f, 8.6f, 10.6f);

    private readonly Tour _tour = new();
    private readonly LiveChanges _changes = new();
    private readonly LightingSwitch _lighting = new(input, graphics);
    private readonly FlyCamera _flyCamera = new(input);
    private LampRoom _lampRoom = null!;
    private Chapel _chapel = null!;
    private Entity _camera;
    private Entity _sky;

    public void Load()
    {
        var materials = Materials(content);
        new Building(world, content, materials).Build();

        _camera = world.Spawn(_tour.At(0f));

        // Mixed: real-time cascades near the camera, and the bake's mask past the last one. A crisp sun with two
        // cascades: the last ends 15 m round the camera's target, well short of the far end of the 42 m corridor.
        _sky = world.Spawn(
            new Sun(GalleryPlan.SunDirection, new Vector3(7.2f, 6.8f, 6f), Mode: LightMode.Mixed),
            Sky(_changes.SkyBrightness),
            new Shadows(5f, Cascades: 2, SunSize: 0.02f),
            new AmbientOcclusion(0.8f, 1f));
        world.Spawn(Finish());

        var bust = content.Model("models/marble_bust_01/marble_bust_01.gltf");
        var whale = content.Model("models/bronze_whale_statue/bronze_whale_statue.gltf");
        world.Spawn(new Transform(GalleryPlan.BustAt, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One), new Model(bust));
        world.Spawn(new Transform(GalleryPlan.WhaleAt, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), Vector3.One), new Model(whale));
        _lampRoom = new LampRoom(world, content);
        _chapel = new Chapel(world, content, materials["shade"]);

        _lighting.Use(_lighting.Current);
    }

    public void Update(GameTime time)
    {
        float seconds = time.Tick / (float)time.TicksPerSecond;
        _lighting.Update();
        _changes.Update(input);
        world.Get<Camera>(_camera) = _flyCamera.Update(_tour, seconds, time.TickSeconds);

        ref var sky = ref world.Get<Sky>(_sky);
        if (sky.Settings.Brightness != _changes.SkyBrightness)
        {
            sky = Sky(_changes.SkyBrightness);
        }

        Flicker.Update(world, seconds);
        _lampRoom.Roll(seconds);
        _chapel.Swing(seconds);
        _chapel.Light(_changes.LanternLit);
    }

    /// <summary>The light probes, the baked space and the reflection probes.</summary>
    public void Draw(View view)
    {
        // One box of probes over the whole gallery. At most 4,096 fit, so they're 1.65 m apart.
        // It's kept on top of the probes that follow the camera: without it the rooms' walls and
        // floors came out blotchy, and the building greyer from the overview.
        view.Indirect.LightProbes(ProbesMin, ProbesMax, 1.65f);

        // The same box is the baked space "gallery": once it has a bake, the building's static
        // pieces take their light from it.
        view.Indirect.BakedLighting("gallery", ProbesMin, ProbesMax, 1.65f);

        ReflectionProbes.Place(view);
    }

    /// <summary>The lighting modes in the bottom-left corner, with the current one in bold.</summary>
    public UiView? Ui => _lighting.Panel(modes => _changes.AddLines(modes, LightingSwitch.Muted));

    /// <summary>The building's flat matte materials, by name.</summary>
    private static Dictionary<string, Surface> Materials(Content content)
    {
        var plaster = content.Texture(PlasterNoise.Texture(128, 0.08f, 7));
        var faint = content.Texture(PlasterNoise.Texture(128, 0.04f, 11));
        var glow = content.Shader("glow");

        return new Dictionary<string, Surface>
        {
            ["plaster"] = new() { Color = new Vector3(0.8f, 0.79f, 0.76f), ColorTexture = plaster, Roughness = 0.92f },
            ["floor"] = new() { Color = new Vector3(0.52f, 0.5f, 0.47f), ColorTexture = plaster, Roughness = 0.8f },
            ["white"] = new() { Color = new Vector3(0.9f, 0.9f, 0.88f), ColorTexture = faint, Roughness = 0.7f },
            ["red"] = new() { Color = new Vector3(0.72f, 0.05f, 0.04f), ColorTexture = faint, Roughness = 0.92f },
            ["blue"] = new() { Color = new Vector3(0.04f, 0.14f, 0.65f), ColorTexture = faint, Roughness = 0.92f },
            ["gravel"] = new() { Color = new Vector3(0.42f, 0.42f, 0.36f), ColorTexture = plaster, Roughness = 0.95f },
            ["flags"] = new() { Color = new Vector3(0.62f, 0.6f, 0.56f), ColorTexture = plaster, Roughness = 0.85f },
            ["wood"] = new() { Color = new Vector3(0.3f, 0.18f, 0.1f), ColorTexture = faint, Roughness = 0.75f },
            ["frame"] = new() { Color = new Vector3(0.12f), Roughness = 0.5f, Metallic = 1f },
            ["glass"] = new() { Color = new Vector3(0.45f, 0.8f, 0.72f), Roughness = 0.05f, Opacity = 0.2f },
            ["water"] = new() { Color = new Vector3(0.1f, 0.18f, 0.2f), Roughness = 0.05f },
            ["orange"] = new() { Color = new Vector3(0.9f, 0.35f, 0.04f), Roughness = 0.6f },
            ["leaves"] = new() { Color = new Vector3(0.16f, 0.32f, 0.08f), ColorTexture = plaster, Roughness = 0.9f },
            ["panel"] = new() { Color = new Vector3(0.9f, 0.88f, 0.8f), Roughness = 0.5f, Shader = new SurfaceShader(glow) { Parameters = [new Vector4(2.4f, 2.1f, 1.6f, 0f)] } },
            ["lamp"] = new() { Color = new Vector3(1f, 0.95f, 0.85f), Roughness = 0.5f, Shader = new SurfaceShader(glow) { Parameters = [new Vector4(10f, 7.5f, 4.2f, 0f)] } },
            ["shade"] = new() { Color = new Vector3(0.06f, 0.06f, 0.065f), Roughness = 0.4f, Metallic = 1f },
        };
    }

    /// <summary>A blue sky with a light haze, at <paramref name="brightness"/>.</summary>
    private static Sky Sky(float brightness) =>
        new(new SkySettings
        {
            Zenith = new Vector3(0.2f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.8f, 0.85f),
            Ground = new Vector3(0.2f, 0.18f, 0.15f),
            Brightness = brightness,
            Turbidity = 2.5f,
            Fog = new FogSettings(Density: 0.004f, Height: 0f, Falloff: 0.1f, SunScatter: 0.6f),
        });

    private static Finish Finish() =>
        new(new PostSettings
        {
            AutoExposure = true,
            Exposure = 1.1f,
            Contrast = 0.15f,
            Saturation = 0.05f,
            Vignette = 0.25f,
            Bloom = 0.04f,
            Sharpen = 0.15f,
        });
}
