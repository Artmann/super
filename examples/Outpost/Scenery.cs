using System.Numerics;
using Rendition;
using Super;

namespace OutpostExample;

/// <summary>What's around the field: the island of soil it stands on, and trees along its back and sides.</summary>
public sealed class Scenery(Look look)
{
    /// <summary>How far the island reaches past the field at the back and sides, in metres.</summary>
    private const float Margin = 2.5f;

    private static readonly Vector3 Soil = new(0.52f, 0.37f, 0.27f);
    private static readonly Vector3 Bark = new(0.45f, 0.32f, 0.24f);
    private static readonly Vector3 Leaves = new(0.3f, 0.5f, 0.32f);

    private readonly List<(Vector3 Spot, float Size)> _trees = Trees();

    public void Draw(View view)
    {
        // A warm sun with crisp shadows, a dim sky so the sun does most of the lighting, and brown ink lines.
        view.Lights.Sun(direction: new Vector3(0.8f, 1.6f, 1.2f), color: new Vector3(3.4f, 3.1f, 2.7f));
        view.Lights.Shadows(radius: 30f, sunSize: 0.03f);
        view.Surroundings.Sky(new SkySettings
        {
            Zenith = new Vector3(0.45f, 0.6f, 0.8f),
            Horizon = new Vector3(0.85f, 0.88f, 0.9f),
            Ground = new Vector3(0.45f, 0.62f, 0.72f),
            Brightness = 0.5f,
        });
        view.Effects.Outlines(color: new Vector3(0.12f, 0.09f, 0.07f), thickness: 1.5f);
        // A little more contrast clears the haze, and darker corners draw the eye to the field.
        view.Effects.Finish(new PostSettings { Contrast = 0.15f, Vignette = 0.25f });

        // The island runs out past the back and sides of the field, but stops at the front, which is near the camera.
        float halfWidth = Field.Width * Field.TileSize / 2;
        float halfDepth = Field.Depth * Field.TileSize / 2;
        float back = -halfDepth - Margin;
        float front = halfDepth + 0.2f;
        var island = Matrix4x4.CreateScale(2 * (halfWidth + Margin), 1.4f, front - back)
            * Matrix4x4.CreateTranslation(0, -0.9f, (back + front) / 2);
        view.Model(2, look.Cube, island, Soil);

        for (int i = 0; i < _trees.Count; i++)
        {
            var (spot, size) = _trees[i];
            var trunk = Matrix4x4.CreateScale(0.3f * size, 0.6f * size, 0.3f * size) * Matrix4x4.CreateTranslation(spot + new Vector3(0, 0.3f * size, 0));
            var crown = Matrix4x4.CreateScale(1.4f * size, 2f * size, 1.4f * size) * Matrix4x4.CreateTranslation(spot + new Vector3(0, 1.5f * size, 0));
            view.Model(40_000 + i, look.Cylinder, trunk, Bark);
            view.Model(50_000 + i, look.Cone, crown, Leaves);
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
