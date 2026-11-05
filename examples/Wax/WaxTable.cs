using System.Numerics;
using Rendition;
using Super;

namespace WaxExample;

/// <summary>
/// Skin, candle wax and jade under one hard, low light, each beside a plain twin. A bar
/// casts a shadow across them all: on the plain ones its edge stays sharp and grey, on
/// the others light creeps under it and the edge turns warm. The dark wall behind shows
/// that the spread light stays on its own surface.
/// </summary>
public sealed class WaxTable(Content content) : IGame
{
    /// <summary>Draws the right-hand twins without subsurface too.</summary>
    public static bool Plain { get; set; }

    private static readonly Subsurface Skin = new()
    {
        Radius = 0.012f,
        Color = new Vector3(1f, 0.4f, 0.25f),
        Transmission = 0.3f,
    };

    private static readonly Subsurface Wax = new()
    {
        Radius = 0.03f,
        Color = new Vector3(1f, 0.8f, 0.6f),
        Transmission = 0.6f,
    };

    private static readonly Subsurface Jade = new()
    {
        Radius = 0.02f,
        Color = new Vector3(0.6f, 1f, 0.7f),
        Transmission = 0.4f,
    };

    private readonly List<(ModelRef Model, Vector3 Position)> _pieces = [];
    private ModelRef _table;
    private ModelRef _wall;
    private ModelRef _bar;

    public void Load()
    {
        _table = content.Mesh(Shapes.Box(new Vector3(2.4f, 0.05f, 1.2f), new Surface { Color = new Vector3(0.35f, 0.25f, 0.18f), Roughness = 0.7f }));
        _wall = content.Mesh(Shapes.Box(new Vector3(4f, 2f, 0.05f), new Surface { Color = new Vector3(0.05f, 0.06f, 0.08f), Roughness = 0.9f }));
        _bar = content.Mesh(Shapes.Box(new Vector3(0.02f, 0.02f, 1.6f), new Surface { Color = new Vector3(0.1f), Roughness = 0.6f }));

        // Each pair stands plain on the left and scattering on the right.
        Pair(-0.6f, subsurface => Shapes.Sphere(0.11f, 48, 24, new Surface { Color = new Vector3(0.85f, 0.6f, 0.48f), Roughness = 0.5f, Subsurface = subsurface }), Skin, lift: 0.11f);
        Pair(0f, subsurface => Shapes.Cylinder(0.045f, 0.045f, 0.3f, 48, new Surface { Color = new Vector3(0.92f, 0.86f, 0.72f), Roughness = 0.45f, Subsurface = subsurface }), Wax, lift: 0.15f);
        Pair(0.6f, subsurface => Shapes.Capsule(0.06f, 0.22f, 48, 12, new Surface { Color = new Vector3(0.35f, 0.62f, 0.42f), Roughness = 0.25f, Subsurface = subsurface }), Jade, lift: 0.11f);
    }

    public void Update(GameTime time)
    {
    }

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0f, 0.45f, 1.25f), lookAt: new Vector3(0f, 0.12f, 0f), fieldOfView: 0.75f);
        view.Sun(direction: new Vector3(-1f, 0.55f, 0.5f), color: new Vector3(4f));
        view.Shadows(1.5f);
        view.Model(0, _table, Matrix4x4.CreateTranslation(0f, -0.025f, 0f));
        view.Model(1, _wall, Matrix4x4.CreateTranslation(0f, 0.5f, -0.45f));

        // Out of shot, between the light and the pieces, so its shadow crosses them all.
        view.Model(2, _bar, Matrix4x4.CreateRotationX(0.6f) * Matrix4x4.CreateTranslation(-1.1f, 0.75f, 0.45f));
        for (int i = 0; i < _pieces.Count; i++)
        {
            view.Model(i + 3, _pieces[i].Model, Matrix4x4.CreateTranslation(_pieces[i].Position));
        }
    }

    private void Pair(float x, Func<Subsurface, GeneratedMesh> make, Subsurface subsurface, float lift)
    {
        _pieces.Add((content.Mesh(make(default)), new Vector3(x - 0.13f, lift, 0f)));
        _pieces.Add((content.Mesh(make(Plain ? default : subsurface)), new Vector3(x + 0.13f, lift, 0f)));
    }
}
