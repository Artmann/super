using System.Numerics;
using Rendition;
using Super;

namespace ToonExample;

/// <summary>
/// A little garden drawn as a cartoon: a snowman, a mushroom, trees and a ring on a green lawn, cel shaded in flat bands
/// with ink outlines. The low sun casts shadows, a lantern lights the mushroom warmly from the side and a light fog
/// softens the back. One chrome ball keeps the physically based look, to show both can share a scene. T turns the
/// toon shading off, so everything is drawn the physically based way, and O the outlines.
/// </summary>
public sealed class ToonGarden(Content content, Input input) : IGame
{
    private const int LanternKey = 1;

    private readonly List<(ModelRef Toon, ModelRef Plain, Matrix4x4 Place)> _pieces = [];
    private ModelRef _chrome;
    private bool _plain;
    private bool _noOutlines;
    private float _turn;
    private float _previousTurn;

    public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["plain"] = new InputScript().Press(Key.T),
        ["no-outlines"] = new InputScript().Press(Key.O),
    };

    public void Load()
    {
        var snow = Cartoon(new Vector3(0.92f, 0.94f, 1f), steps: 2, rim: new Vector3(0.25f, 0.3f, 0.4f));
        var lawn = Cartoon(new Vector3(0.32f, 0.62f, 0.25f), steps: 2, outline: false);
        var bark = Cartoon(new Vector3(0.45f, 0.28f, 0.16f));
        var leaves = Cartoon(new Vector3(0.2f, 0.5f, 0.22f));
        var cap = Cartoon(new Vector3(0.85f, 0.15f, 0.12f), roughness: 0.3f);
        var stalk = Cartoon(new Vector3(0.95f, 0.9f, 0.8f));
        var gold = Cartoon(new Vector3(1f, 0.75f, 0.3f), roughness: 0.25f, metallic: 1f);
        var carrot = Cartoon(new Vector3(0.95f, 0.45f, 0.1f));

        Add(Shapes.Plane(new Vector2(30f, 30f), surface: lawn), Matrix4x4.Identity);

        // The snowman: three balls and a carrot nose.
        Add(Shapes.Sphere(0.6f, surface: snow), Matrix4x4.CreateTranslation(0f, 0.55f, 0f));
        Add(Shapes.Sphere(0.42f, surface: snow), Matrix4x4.CreateTranslation(0f, 1.38f, 0f));
        Add(Shapes.Sphere(0.3f, surface: snow), Matrix4x4.CreateTranslation(0f, 1.95f, 0f));
        Add(Shapes.Cone(0.06f, 0.35f, surface: carrot), Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 1.97f, 0.42f));

        // A mushroom to its left.
        Add(Shapes.Cylinder(0.14f, 0.18f, 0.6f, surface: stalk), Matrix4x4.CreateTranslation(-1.6f, 0.3f, 0.6f));
        Add(Shapes.Sphere(0.45f, surface: cap), Matrix4x4.CreateScale(1f, 0.55f, 1f) * Matrix4x4.CreateTranslation(-1.6f, 0.62f, 0.6f));

        // Trees behind.
        for (int i = 0; i < 4; i++)
        {
            float x = -3.5f + i * 2.4f;
            float z = -3f - (i % 2) * 1.5f;
            Add(Shapes.Cylinder(0.15f, 0.2f, 0.8f, surface: bark), Matrix4x4.CreateTranslation(x, 0.4f, z));
            Add(Shapes.Cone(0.8f, 2f, surface: leaves), Matrix4x4.CreateTranslation(x, 1.8f, z));
        }

        // A golden ring standing on the lawn to the right, and the chrome ball beside it.
        Add(Shapes.Torus(0.45f, 0.12f, surface: gold), Matrix4x4.CreateRotationY(0.6f) * Matrix4x4.CreateTranslation(1.7f, 0.57f, 0.4f));
        _chrome = content.Mesh(Shapes.Sphere(0.35f, surface: new Surface { Color = new Vector3(0.95f), Metallic = 1f, Roughness = 0.08f }));
    }

    public void Update(GameTime time)
    {
        if (input.Pressed(Key.T))
        {
            _plain = !_plain;
        }

        if (input.Pressed(Key.O))
        {
            _noOutlines = !_noOutlines;
        }

        _previousTurn = _turn;
        _turn += 0.05f * time.TickSeconds;
    }

    public void Draw(View view)
    {
        float turn = float.Lerp(_previousTurn, _turn, view.Blend);
        var eye = new Vector3(MathF.Sin(turn) * 5.5f, 2.4f, MathF.Cos(turn) * 5.5f);
        view.Camera(position: eye, lookAt: new Vector3(0f, 0.9f, 0f), fieldOfView: 0.9f);
        view.Surroundings.Sky(new SkySettings
        {
            Zenith = new Vector3(0.25f, 0.4f, 0.8f),
            Horizon = new Vector3(0.85f, 0.7f, 0.6f),
            Ground = new Vector3(0.2f, 0.25f, 0.15f),
            Brightness = 0.6f,
            Fog = new FogSettings(Density: 0.03f, Height: 0f, Falloff: 0.2f, SunScatter: 0.4f),
        });
        view.Lights.Sun(direction: new Vector3(0.7f, 0.55f, 0.4f), color: new Vector3(4.5f, 4f, 3.3f), shadows: true);
        view.Lights.Shadows(10f, 2);
        view.Lights.Point(LanternKey, new PointLight(new Vector3(-2.5f, 1.3f, 1.6f), new Vector3(6f, 3.5f, 1.5f), 3.5f));
        view.Effects.Outlines(thickness: _noOutlines ? 0f : 1.5f);

        for (int i = 0; i < _pieces.Count; i++)
        {
            var (toon, plain, place) = _pieces[i];
            view.Model(10 + i, _plain ? plain : toon, place);
        }

        view.Model(9, _chrome, Matrix4x4.CreateTranslation(2.4f, 0.35f, 1.5f));
    }

    private static Surface Cartoon(Vector3 color, int steps = 3, float roughness = 0.8f, float metallic = 0f, Vector3 rim = default, bool outline = true) =>
        new()
        {
            Color = color,
            Roughness = roughness,
            Metallic = metallic,
            Toon = new Toon(steps, Rim: rim),
            Features = outline ? SurfaceFeatures.Outline : SurfaceFeatures.None,
        };

    // Each piece twice: toon, and plain for T.
    private void Add(GeneratedMesh mesh, Matrix4x4 place) =>
        _pieces.Add((content.Mesh(mesh), content.Mesh(mesh with { Surface = mesh.Surface with { Toon = null, Features = SurfaceFeatures.None } }), place));
}
