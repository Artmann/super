using System.Numerics;
using Super;

namespace PlotsExample;

/// <summary>The ground with its mound, the deck on its posts and the field's rocks, which the grids lie on.</summary>
internal static class PlotsScenery
{
    private static readonly Vector3 Mound = new(-8f, 0f, -3f);

    /// <summary>Spawns the scenery, with the deck's top centred on its middle, turned and as wide as its size in metres.</summary>
    public static void Spawn(World world, Content content, (Vector3 Middle, Quaternion Turn, float Size) deck, HexGrid field, Hex[] rocks)
    {
        var ground = content.Mesh(Shapes.Plane(new Vector2(40f, 30f), surface: new Surface { Color = new Vector3(0.35f, 0.45f, 0.3f), Roughness = 0.9f }));
        world.Spawn(Transform.Identity, new Model(ground));
        world.Spawn(new Transform(Mound, Quaternion.Identity, new Vector3(7f, 1.4f, 6f)), new Model(ModelRef.Sphere, new Vector3(0.4f, 0.5f, 0.32f)));

        var board = content.Mesh(Shapes.Box(new Vector3(deck.Size, 0.25f, deck.Size)));
        world.Spawn(new Transform(deck.Middle - new Vector3(0f, 0.125f, 0f), deck.Turn, Vector3.One), new Model(board, new Vector3(0.55f, 0.4f, 0.28f)));
        float top = deck.Middle.Y;
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var corner = deck.Middle + Vector3.Transform(new Vector3(x, 0f, z) * (deck.Size / 2f - 0.3f), deck.Turn);
            var post = new Transform(new Vector3(corner.X, top / 2f, corner.Z), deck.Turn, new Vector3(0.25f, top, 0.25f));
            world.Spawn(post, new Model(ModelRef.Cylinder, new Vector3(0.4f, 0.3f, 0.2f)));
        }

        foreach (var rock in rocks)
        {
            var at = field.Center(rock);
            world.Spawn(new Transform(at + new Vector3(0f, 0.2f, 0f), Quaternion.Identity, new Vector3(0.9f, 0.4f, 0.8f)), new Model(ModelRef.Sphere, new Vector3(0.45f, 0.43f, 0.4f)));
        }
    }
}
