using System.Numerics;
using Super;

namespace Courtyard;

/// <summary>
/// The lineup: a row for each material graph, with a sphere, a cube and a flat tile 1 m
/// across, all at the graph's own width, under a photographed meadow sky turned so its low sun
/// comes from the side and the bumps read.
/// </summary>
public sealed class LineupGame(World world, Content content) : IGame
{
    /// <summary>The graphs on show: the first three are the left block and the rest the right, each from the back row to the front.</summary>
    public static readonly string[] Materials =
    [
        "materials/bricks.material.cs",
        "materials/flagstones.material.cs",
        "materials/dirt.material.cs",
        "materials/wood.material.cs",
        "materials/leather.material.cs",
        "materials/rusty-metal.material.cs",
    ];

    private const float Spacing = 1.45f;
    private const int RowsPerBlock = 3;

    public void Load()
    {
        // Meadow 2 by Sergej Majboroda, CC0, from Poly Haven. Turned so its sun stands where the old one did, to the
        // right and a little towards the camera.
        var sky = content.Sky("skies/meadow_2.hdr");
        float rotation = Azimuth(new Vector3(1f, 0f, 0.25f)) - Azimuth(sky.SunDirection);

        world.Spawn(new Camera(new Vector3(0f, 6f, 8.2f), new Vector3(0f, 0.15f, 0f), 0.6f));

        // The photographed sun, from the right, so the normals read, and the meadow's sky light to fill.
        world.Spawn(
            new Sun(sky.SunDirectionAt(rotation), new Vector3(4f)),
            new Sky(new SkySettings { Image = sky, Rotation = rotation, Brightness = 1f }),
            new Shadows(10f, 2));

        Place(Shapes.Plane(new Vector2(30f, 30f), surface: new Surface { Color = new Vector3(0.45f), Roughness = 0.9f }), Vector3.Zero);

        for (int i = 0; i < Materials.Length; i++)
        {
            var surface = content.Surface(Materials[i]);

            // Two blocks of three rows side by side, so the lineup fills a wide screen.
            float blockX = i < RowsPerBlock ? -2.35f : 2.35f;
            float z = (i % RowsPerBlock - 1) * Spacing;
            Place(Shapes.Sphere(0.5f, 48, 24, surface: surface), new Vector3(blockX - Spacing, 0.5f, z));
            Place(Shapes.Box(Vector3.One, surface: surface), new Vector3(blockX, 0.5f, z));
            Place(Shapes.Plane(Vector2.One, surface: surface), new Vector3(blockX + Spacing, 0.01f, z));
        }
    }

    public void Update(GameTime time)
    {
    }

    private void Place(GeneratedMesh mesh, Vector3 at) => world.Spawn(Transform.At(at), new Model(content.Mesh(mesh)));

    // The angle round the up axis, counterclockwise seen from above, from +X; a sky turned by the difference of two
    // of these carries the one direction to the other.
    private static float Azimuth(Vector3 direction) => MathF.Atan2(-direction.Z, direction.X);
}
