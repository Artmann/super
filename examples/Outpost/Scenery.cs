using System.Numerics;
using Rendition;
using Super;

namespace OutpostExample;

/// <summary>
/// Everything that stays put: the camera and the light, the field's tiles, the base, the island of soil the field
/// stands on, and trees along its back and sides.
/// </summary>
public sealed class Scenery(Field field, Look look)
{
    /// <summary>How far the island reaches past the field at the back and sides, in metres.</summary>
    private const float Margin = 2.5f;

    private static readonly Vector3 Grass = new(0.42f, 0.66f, 0.34f);
    private static readonly Vector3 Sand = new(0.9f, 0.76f, 0.52f);
    private static readonly Vector3 Stone = new(0.55f, 0.53f, 0.58f);
    private static readonly Vector3 BaseRoof = new(0.85f, 0.42f, 0.36f);
    private static readonly Vector3 Soil = new(0.52f, 0.37f, 0.27f);
    private static readonly Vector3 Bark = new(0.45f, 0.32f, 0.24f);
    private static readonly Vector3 Leaves = new(0.3f, 0.5f, 0.32f);

    /// <summary>Where the picture is taken from, looking down at the middle tile.</summary>
    public Camera Camera { get; } = new(new Vector3(0, 22, 16), Vector3.Zero) { WideAs = 16f / 9f };

    /// <summary>Spawns the scenery into a scene. Everything a game spawns while it's open goes with it.</summary>
    public void Build(Scene scene)
    {
        scene.Spawn(Camera);
        Light(scene);
        Ground(scene);
        Island(scene);
    }

    /// <summary>
    /// A warm sun with crisp shadows, a dim sky so the sun does most of the lighting, and brown ink lines. A little
    /// more contrast clears the haze, and darker corners draw the eye to the field.
    /// </summary>
    private static void Light(Scene scene)
    {
        scene.Spawn(
            new Sun(Direction: new Vector3(0.8f, 1.6f, 1.2f), Color: new Vector3(3.4f, 3.1f, 2.7f)),
            new Shadows(Radius: 30f, SunSize: 0.03f),
            new Sky(new SkySettings
            {
                Zenith = new Vector3(0.45f, 0.6f, 0.8f),
                Horizon = new Vector3(0.85f, 0.88f, 0.9f),
                Ground = new Vector3(0.45f, 0.62f, 0.72f),
                Brightness = 0.5f,
            }),
            new Outlines(Color: new Vector3(0.12f, 0.09f, 0.07f), Thickness: 1.5f));
        scene.Spawn(new Finish(new PostSettings { Contrast = 0.15f, Vignette = 0.25f }));
    }

    /// <summary>The tiles of the field, grass or sand, and the base at the end of the road.</summary>
    private void Ground(Scene scene)
    {
        for (int z = 0; z < Field.Depth; z++)
        {
            for (int x = 0; x < Field.Width; x++)
            {
                var tile = new Transform(field.Center(x, z) - new Vector3(0, 0.1f, 0), Quaternion.Identity, new Vector3(1.96f, 0.2f, 1.96f));
                scene.Spawn(tile, new Model(look.Tile, field.IsRoad(x, z) ? Sand : Grass));
            }
        }

        scene.Spawn(Transform.At(field.BaseCenter + new Vector3(0, 0.8f, 0), 1.6f), new Model(look.Cube, Stone));
        scene.Spawn(
            new Transform(field.BaseCenter + new Vector3(0, 2.2f, 0), Quaternion.Identity, new Vector3(2.1f, 1.2f, 2.1f)),
            new Model(look.Cone, BaseRoof));
    }

    /// <summary>The island of soil under the field, and the trees on it.</summary>
    private void Island(Scene scene)
    {
        // The island runs out past the back and sides of the field, but stops at the front, which is near the camera.
        float halfWidth = Field.Width * Field.TileSize / 2;
        float halfDepth = Field.Depth * Field.TileSize / 2;
        float back = -halfDepth - Margin;
        float front = halfDepth + 0.2f;
        var island = new Transform(new Vector3(0, -0.9f, (back + front) / 2), Quaternion.Identity, new Vector3(2 * (halfWidth + Margin), 1.4f, front - back));
        scene.Spawn(island, new Model(look.Cube, Soil));

        foreach (var (spot, size) in Trees())
        {
            var trunk = new Transform(spot + new Vector3(0, 0.3f * size, 0), Quaternion.Identity, new Vector3(0.3f * size, 0.6f * size, 0.3f * size));
            var crown = new Transform(spot + new Vector3(0, 1.5f * size, 0), Quaternion.Identity, new Vector3(1.4f * size, 2f * size, 1.4f * size));
            scene.Spawn(trunk, new Model(look.Cylinder, Bark));
            scene.Spawn(crown, new Model(look.Cone, Leaves));
        }
    }

    /// <summary>A row of trees behind the field and down each side, not quite in line, so they look planted by hand.</summary>
    private static List<(Vector3 Spot, float Size)> Trees()
    {
        float edgeX = Field.Width * Field.TileSize / 2 + Margin / 2;
        float edgeZ = Field.Depth * Field.TileSize / 2 + Margin / 2;
        var trees = new List<(Vector3, float)>();
        for (int i = 0; i < 10; i++)
        {
            trees.Add((new Vector3(-14f + i * 3.1f, 0, -edgeZ + i % 2 * 0.5f), 0.8f + i % 3 * 0.2f));
        }

        for (int i = 0; i < 5; i++)
        {
            float z = -6f + i * 3.4f;
            trees.Add((new Vector3(-edgeX - i % 2 * 0.3f, 0, z), 0.9f + i % 2 * 0.2f));
            trees.Add((new Vector3(edgeX + (i + 1) % 2 * 0.3f, 0, z + 1f), 0.8f + i % 3 * 0.2f));
        }

        return trees;
    }
}
