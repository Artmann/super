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
public sealed class LeavesGame(World world, Content content) : IGame
{
    /// <summary>The cards, cut out by their graphs' opacity.</summary>
    public const string Cards = "models/cards.glb";

    public void Load()
    {
        world.Spawn(new Camera(new Vector3(0.1f, 0.85f, 2.4f), new Vector3(0.1f, 0.45f, -0.3f), 0.66f));

        // An afternoon sun from the front right, high enough to light the ground, and a clear sky to fill the shade.
        world.Spawn(
            new Sun(new Vector3(0.55f, 0.65f, 0.6f), new Vector3(6f, 5.7f, 5.1f)),
            new Sky(new SkySettings
            {
                Zenith = new Vector3(0.25f, 0.4f, 0.75f),
                Horizon = new Vector3(0.75f, 0.78f, 0.82f),
                Ground = new Vector3(0.2f, 0.18f, 0.15f),
                Brightness = 0.8f,
            }),
            new Shadows(4f, 2, SunSize: 0.02f));

        var plaster = new Surface { Color = new Vector3(0.62f, 0.6f, 0.56f), Roughness = 0.9f };
        var timber = new Surface { Color = new Vector3(0.3f, 0.22f, 0.15f), Roughness = 0.8f };
        Place(Shapes.Plane(new Vector2(8f, 6f), surface: new Surface { Color = new Vector3(0.36f, 0.33f, 0.29f), Roughness = 0.95f }), Vector3.Zero);
        Place(Shapes.Box(new Vector3(4f, 1.4f, 0.1f), surface: plaster), new Vector3(0f, 0.7f, -0.65f));
        // The frame the cobweb hangs in: a post at its left and a lintel over it.
        Place(Shapes.Box(new Vector3(0.08f, 1.2f, 0.12f), surface: timber), new Vector3(0.96f, 0.6f, -0.35f));
        Place(Shapes.Box(new Vector3(0.7f, 0.08f, 0.12f), surface: timber), new Vector3(1.31f, 1.19f, -0.35f));
        world.Spawn(Transform.Identity, new Model(content.Model(Cards)));
    }

    public void Update(GameTime time)
    {
    }

    /// <summary>The breeze, which is placed every frame.</summary>
    public void Draw(View view)
    {
        view.Surroundings.Wind(new Vector3(0.6f, 0f, -0.3f), 0.2f);
    }

    private void Place(GeneratedMesh mesh, Vector3 at) => world.Spawn(Transform.At(at), new Model(content.Mesh(mesh)));
}
