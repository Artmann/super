using System.Numerics;
using Super;

namespace Courtyard;

/// <summary>
/// The leaves: the cut-out graphs on the cards the courtyard will use, from <c>models/cards.glb</c>. Along the
/// back wall each graph hangs whole and flat: the two ivy leaves, the three grasses and the three dead leaves. In
/// front, ivy climbs the wall at the left, a tuft of each grass stands on the ground, dead leaves lie in front of
/// them, and a cobweb fills the corner of a frame at the right, all with the rules the courtyard's sidecar gives
/// them, in a light breeze.
/// </summary>
public sealed class LeavesGame(Content content) : IGame
{
    /// <summary>The cards, cut out by their graphs' opacity.</summary>
    public const string Cards = "models/cards.glb";

    private ModelRef _cards;
    private ModelRef _ground;
    private ModelRef _wall;
    private ModelRef _post;
    private ModelRef _lintel;

    public void Load()
    {
        _cards = content.Model(Cards);
        _ground = content.Mesh(Shapes.Plane(new Vector2(8f, 6f), surface: new Surface { Color = new Vector3(0.36f, 0.33f, 0.29f), Roughness = 0.95f }));
        var plaster = new Surface { Color = new Vector3(0.62f, 0.6f, 0.56f), Roughness = 0.9f };
        _wall = content.Mesh(Shapes.Box(new Vector3(4f, 1.4f, 0.1f), surface: plaster));
        var timber = new Surface { Color = new Vector3(0.3f, 0.22f, 0.15f), Roughness = 0.8f };
        _post = content.Mesh(Shapes.Box(new Vector3(0.08f, 1.2f, 0.12f), surface: timber));
        _lintel = content.Mesh(Shapes.Box(new Vector3(0.7f, 0.08f, 0.12f), surface: timber));
    }

    public void Update(GameTime time)
    {
    }

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0.1f, 0.85f, 2.4f), lookAt: new Vector3(0.1f, 0.45f, -0.3f), fieldOfView: 0.66f);

        // An afternoon sun from the front right, high enough to light the ground, and a clear sky to fill the shade.
        view.Sun(direction: new Vector3(0.55f, 0.65f, 0.6f), color: new Vector3(6f, 5.7f, 5.1f));
        view.Sky(new SkySettings
        {
            Zenith = new Vector3(0.25f, 0.4f, 0.75f),
            Horizon = new Vector3(0.75f, 0.78f, 0.82f),
            Ground = new Vector3(0.2f, 0.18f, 0.15f),
            Brightness = 0.8f,
        });
        view.Shadows(4f, 2, sunSize: 0.02f);
        view.Wind(new Vector3(0.6f, 0f, -0.3f), 0.2f);

        view.Model(0, _ground, Matrix4x4.Identity);
        view.Model(1, _wall, Matrix4x4.CreateTranslation(0f, 0.7f, -0.65f));
        // The frame the cobweb hangs in: a post at its left and a lintel over it.
        view.Model(2, _post, Matrix4x4.CreateTranslation(0.96f, 0.6f, -0.35f));
        view.Model(3, _lintel, Matrix4x4.CreateTranslation(1.31f, 1.19f, -0.35f));
        view.Model(4, _cards, Matrix4x4.Identity);
    }
}
