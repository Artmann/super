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
public sealed class GalleryGame(Content content, Input input, Graphics graphics) : IGame
{
    /// <summary>Key presses for profiling the reflection probes catching up. See <see cref="LiveChanges.Scripts"/>.</summary>
    public IReadOnlyDictionary<string, InputScript> Scripts => LiveChanges.Scripts;

    // The box the light probes fill, which is also the baked space "gallery".
    private static readonly Vector3 ProbesMin = new(-11.8f, -0.4f, -56.6f);
    private static readonly Vector3 ProbesMax = new(7.8f, 8.6f, 10.6f);

    private readonly Tour _tour = new();
    private readonly LiveChanges _changes = new();
    private readonly LightingSwitch _lighting = new(input, graphics);
    private readonly FlyCamera _camera = new(input);
    private Building _building = null!;
    private LampRoom _lampRoom = null!;
    private Chapel _chapel = null!;
    private ModelRef _bust;
    private ModelRef _whale;
    private float _seconds;

    public void Load()
    {
        var materials = Materials(content);
        _building = new Building(content, materials);
        _building.Build();

        _bust = content.Model("models/marble_bust_01/marble_bust_01.gltf");
        _whale = content.Model("models/bronze_whale_statue/bronze_whale_statue.gltf");
        _lampRoom = new LampRoom(content);
        _chapel = new Chapel(content, materials["shade"]);

        _camera.Start(_tour.At(0f).Position);
        _lighting.Use(_lighting.Current);
    }

    public void Update(GameTime time)
    {
        _seconds = time.Tick / (float)time.TicksPerSecond;
        _lighting.Update();
        _changes.Update(input);
        _camera.Update(_tour, _seconds, time.TickSeconds);
    }

    public void Draw(View view)
    {
        float seconds = view.SecondsSince(0);
        _camera.Aim(view, _tour, seconds);

        // Mixed: real-time cascades near the camera, and the bake's mask past the last one.
        view.Lights.Sun(GalleryPlan.SunDirection, new Vector3(7.2f, 6.8f, 6f), mode: LightMode.Mixed);
        Sky(view, _changes.SkyBrightness);

        // A crisp sun with two cascades: the last ends 15 m round the camera's target, well short
        // of the far end of the 42 m corridor.
        view.Lights.Shadows(5f, cascades: 2, sunSize: 0.02f);
        view.Indirect.AmbientOcclusion(0.8f, 1f);

        // One box of probes over the whole gallery. At most 4,096 fit, so they're 1.65 m apart.
        // It's kept on top of the probes that follow the camera: without it the rooms' walls and
        // floors came out blotchy, and the building greyer from the overview.
        view.Indirect.LightProbes(ProbesMin, ProbesMax, 1.65f);

        // The same box is the baked space "gallery": once it has a bake, the building's static
        // pieces take their light from it.
        view.Indirect.BakedLighting("gallery", ProbesMin, ProbesMax, 1.65f);

        ReflectionProbes.Place(view);
        Finish(view);

        var pieces = _building.Pieces;
        for (int i = 0; i < pieces.Count; i++)
        {
            view.Model(1 + i, pieces[i].Mesh, pieces[i].Transform, new ModelOptions { Static = true });
        }

        view.Model(GalleryPlan.DynamicKey, _bust, Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(GalleryPlan.BustAt));
        view.Model(GalleryPlan.DynamicKey + 1, _whale, Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(GalleryPlan.WhaleAt));

        _lampRoom.Draw(view, seconds);
        _chapel.Draw(view, seconds, _seconds, _changes.LanternLit);
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
    private static void Sky(View view, float brightness)
    {
        view.Surroundings.Sky(new SkySettings
        {
            Zenith = new Vector3(0.2f, 0.38f, 0.8f),
            Horizon = new Vector3(0.75f, 0.8f, 0.85f),
            Ground = new Vector3(0.2f, 0.18f, 0.15f),
            Brightness = brightness,
            Turbidity = 2.5f,
            Fog = new FogSettings(Density: 0.004f, Height: 0f, Falloff: 0.1f, SunScatter: 0.6f),
        });
    }

    private static void Finish(View view)
    {
        view.Effects.Finish(new PostSettings
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
}
