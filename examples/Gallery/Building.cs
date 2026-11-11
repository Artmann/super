using System.Numerics;
using Super;

namespace Gallery;

/// <summary>
/// The gallery's walls, floors, roofs and furniture: plain boxes and cylinders in flat matte
/// materials, built once in Load. North is -z. The corridor runs north from the entrance hall,
/// with the rooms off its west side and the colonnade on its east side, and ends at the
/// courtyard behind a glass screen.
/// </summary>
public sealed class Building
{
    /// <summary>Plaster and the other textures repeat every two metres.</summary>
    private const float UvPerMetre = 0.5f;

    private const float Wall = 0.3f;

    private readonly Content _content;
    private readonly Dictionary<(Vector3 Size, string Material), ModelRef> _boxes = [];
    private readonly List<(ModelRef Mesh, Matrix4x4 Transform)> _pieces = [];
    private readonly Dictionary<string, Surface> _materials;

    public Building(Content content, Dictionary<string, Surface> materials)
    {
        _content = content;
        _materials = materials;
    }

    /// <summary>Everything to draw, each with its own place.</summary>
    public IReadOnlyList<(ModelRef Mesh, Matrix4x4 Transform)> Pieces => _pieces;

    /// <summary>A hole in a wall or a roof, from one corner to the other in the wall's own two axes.</summary>
    public readonly record struct Hole(float From, float To, float Bottom, float Top);

    public void Build()
    {
        BuildFloors();
        BuildHall();
        BuildCorridor();
        BuildRooms();
        BuildCourtyard();
    }

    private void BuildFloors()
    {
        // One floor under everything, then lighter or darker floors laid over it.
        Box(new(-11.6f, -0.3f, -56.6f), new(7.6f, 0f, 10.6f), "floor");
        Box(new(-11f, 0f, -10f + Wall / 2f), new(-2.3f, 0.01f, -Wall), "white");
        Box(new(2.6f, 0f, -42f), new(7f, 0.01f, -Wall), "gravel");
        Box(new(-11.3f, 0f, -56f), new(7.3f, 0.01f, -42.3f), "flags");
    }

    private void BuildHall()
    {
        // The entrance hall, 12 m wide and 6 m tall, with a door in the south wall and the
        // corridor leaving from the middle of its north wall.
        WallAlongX(10f, 10f + Wall, -6.3f, 6.3f, 6f, "plaster", new Hole(-1.2f, 1.2f, 0f, 3f));
        WallAlongZ(-6.3f, -6f, -Wall, 10f, 6f, "plaster");
        WallAlongZ(6f, 6.3f, -Wall, 10f, 6f, "plaster");

        // Its north wall runs on west as the colour room's south wall, so it reaches that room's height.
        WallAlongX(-Wall, 0f, -11.3f, 6.3f, 7f, "plaster", new Hole(-2f, 2.6f, 0f, 4.5f));
        // East of the hall it's only the garden's low wall.
        WallAlongX(-Wall, 0f, 6.3f, 7.3f, 1.2f, "plaster");

        // The roof, with a long skylight off to the east, so the sun's stripe lands in the middle of the floor.
        Roof(-6.3f, 6.3f, -Wall, 10f + Wall, 6f, new Hole(GalleryGame.HallSkylight.Min.X, GalleryGame.HallSkylight.Max.X, GalleryGame.HallSkylight.Min.Z, GalleryGame.HallSkylight.Max.Z));

        // Two benches facing each other across the stripe.
        Box(new(-4.6f, 0f, 2.5f), new(-4.1f, 0.45f, 7.5f), "wood");
        Box(new(3.3f, 0f, 2.5f), new(3.8f, 0.45f, 7.5f), "wood");
    }

    private void BuildCorridor()
    {
        // The corridor roof covers the walkway and the colonnade, and stops at the glass screen.
        Roof(-2.3f, 2.6f, -42f, -Wall, 4.5f);

        // Columns every 2.5 m along the east side, under a beam, open to the garden.
        Box(new(1.95f, 4.15f, -42f), new(2.55f, 4.5f, -Wall), "plaster");
        for (float z = -1.5f; z > -42f; z -= 2.5f)
        {
            Cylinder(new Vector3(2.25f, 0f, z), 0.2f, 4.15f, "plaster");
            Box(new(2.0f, 0f, z - 0.25f), new(2.5f, 0.25f, z + 0.25f), "plaster");
        }

        // The garden's low walls, on the far side from the colonnade and at its north end.
        WallAlongZ(7f, 7.3f, -42.3f, 0f, 1.2f, "plaster");
        WallAlongX(-42.3f, -42f, 2.6f, 7f, 1.2f, "plaster");

        // The tinted glass screen across the end of the corridor, in a frame.
        Box(new(-2f, 0f, GalleryGame.ScreenZ - 0.02f), new(2.6f, 4.5f, GalleryGame.ScreenZ + 0.02f), "glass");
        foreach (float x in new[] { -2f, -0.45f, 1.1f, 2.6f })
        {
            Box(new(x - 0.06f, 0f, GalleryGame.ScreenZ - 0.06f), new(x + 0.06f, 4.5f, GalleryGame.ScreenZ + 0.06f), "frame");
        }

        Box(new(-2f, 2.2f, GalleryGame.ScreenZ - 0.05f), new(2.6f, 2.3f, GalleryGame.ScreenZ + 0.05f), "frame");
    }

    private void BuildRooms()
    {
        // The rooms' east walls make the corridor's west wall. Each room has a door onto the
        // corridor; the colour room also has a clerestory window above the corridor roof.
        var window = GalleryGame.ColourWindow;
        WallAlongZ(-2.3f, -2f, -10f, -Wall, 7f, "plaster",
            new Hole(-5.8f, -3.8f, 0f, 3f),
            new Hole(window.Min.Z, window.Max.Z, window.Min.Y, window.Max.Y));
        WallAlongZ(-2.3f, -2f, -20f, -10f, 7f, "plaster", new Hole(-15.7f, -13.7f, 0f, 3f));
        WallAlongZ(-2.3f, -2f, -30f, -20f, 4.5f, "plaster", new Hole(-26f, -24f, 0f, 3f));
        WallAlongZ(-2.3f, -2f, -42f, -30f, 8f, "plaster", new Hole(-37f, -35f, 0f, 3f));

        // The west wall, a little taller where each room is taller.
        WallAlongZ(-11.3f, -11f, -10f, -Wall, 7f, "plaster");
        WallAlongZ(-11.3f, -11f, -20f, -10f, 7f, "plaster");
        WallAlongZ(-11.3f, -11f, -30f, -20f, 4.5f, "plaster");
        WallAlongZ(-11.3f, -11f, -42.3f, -30f, 8f, "plaster");

        // Walls between the rooms, as tall as the taller room either side.
        WallAlongX(-10f - Wall / 2f, -10f + Wall / 2f, -11f, -2.3f, 7f, "plaster");
        WallAlongX(-20f - Wall / 2f, -20f + Wall / 2f, -11f, -2.3f, 7f, "plaster");
        WallAlongX(-30f - Wall / 2f, -30f + Wall / 2f, -11f, -2.3f, 8f, "plaster");
        WallAlongX(-42.3f, -42f, -11.3f, -2f, 8f, "plaster");

        // The colour room: a red wall to the north and a blue one to the south, laid over the plaster.
        Box(new(-11f, 0.01f, -10f + Wall / 2f), new(-2.3f, 7f, -10f + Wall / 2f + 0.03f), "red");
        Box(new(-11f, 0.01f, -Wall - 0.03f), new(-2.3f, 7f, -Wall), "blue");
        Roof(-11.3f, -2f, -10f, -Wall, 7f);
        var bust = GalleryGame.BustAt;
        Box(new(bust.X - 0.2f, 0f, bust.Z - 0.2f), new(bust.X + 0.2f, bust.Y, bust.Z + 0.2f), "plaster");
        Box(new(bust.X - 0.26f, 0f, bust.Z - 0.26f), new(bust.X + 0.26f, 0.12f, bust.Z + 0.26f), "plaster");

        // The whale room, with a skylight placed so the sun lands on the whale.
        var skylight = GalleryGame.WhaleSkylight;
        Roof(-11.3f, -2f, -20f, -10f, 7f, new Hole(skylight.Min.X, skylight.Max.X, skylight.Min.Z, skylight.Max.Z));
        var whale = GalleryGame.WhaleAt;
        Box(new(whale.X - 0.8f, 0f, whale.Z - 1.1f), new(whale.X + 0.8f, whale.Y, whale.Z + 1.1f), "plaster");

        // The lamp room is shut but for its door. A strip of glowing panels runs along the floor,
        // and three lamps hang from the roof: a dark shade over a glowing disc.
        Roof(-11.3f, -2f, -30f, -20f, 4.5f);
        for (int i = 0; i < 10; i++)
        {
            float x = -10.4f + i * 0.75f;
            Box(new(x, 0f, -28.2f), new(x + 0.6f, 0.03f, -27.6f), "panel");
        }

        foreach (var lamp in GalleryGame.Lamps)
        {
            Cylinder(lamp + new Vector3(0f, 0.31f, 0f), 0.008f, 4.5f - lamp.Y - 0.31f, "shade");
            Cone(lamp + new Vector3(0f, 0.01f, 0f), 0.3f, 0.06f, 0.3f, "shade");
            Cylinder(lamp - new Vector3(0f, 0.01f, 0f), 0.27f, 0.02f, "lamp");
        }

        // The chapel: tall, with an altar at the west end and benches either side of the aisle.
        Roof(-11.3f, -2f, -42.3f, -30f, 8f);
        Box(new(-10.7f, 0f, -37.6f), new(-9.6f, 1f, -34.4f), "plaster");
        Box(new(-10.9f, 1f, -37.8f), new(-9.4f, 1.08f, -34.2f), "plaster");
        for (int row = 0; row < 4; row++)
        {
            float x = -8.4f + row * 1.3f;
            foreach (var (from, to) in new[] { (-40.8f, -37.8f), (-34.2f, -31.2f) })
            {
                Box(new(x - 0.2f, 0.4f, from), new(x + 0.2f, 0.47f, to), "wood");
                Box(new(x + 0.15f, 0.47f, from), new(x + 0.2f, 0.95f, to), "wood");
                Box(new(x - 0.15f, 0f, from + 0.1f), new(x + 0.15f, 0.4f, from + 0.2f), "wood");
                Box(new(x - 0.15f, 0f, to - 0.2f), new(x + 0.15f, 0.4f, to - 0.1f), "wood");
            }
        }
    }

    private void BuildCourtyard()
    {
        // Open to the sky, walled on three sides, with a basin in the middle.
        WallAlongZ(-11.6f, -11.3f, -56.3f, -42.3f, 4f, "plaster");
        WallAlongZ(7.3f, 7.6f, -56.3f, -42.3f, 4f, "plaster");
        WallAlongX(-56.3f, -56f, -11.6f, 7.6f, 4f, "plaster");

        Cylinder(new Vector3(-1f, 0f, -49.5f), 1.8f, 0.55f, "plaster");
        Cylinder(new Vector3(-1f, 0.55f, -49.5f), 1.55f, 0.01f, "water");
        Cylinder(new Vector3(-1f, 0f, -49.5f), 0.25f, 1.6f, "plaster");

        Box(new(3.2f, 0f, -53.4f), new(4.2f, 1f, -52.4f), "plaster");
        Sphere(new Vector3(3.7f, 1.5f, -52.9f), 0.5f, "orange");
        Box(new(-8.8f, 0f, -53.5f), new(-6.8f, 0.45f, -52.9f), "wood");
        Cylinder(new Vector3(-8f, 0f, -46f), 0.18f, 2.2f, "wood");
        Sphere(new Vector3(-8f, 2.9f, -46f), 1.1f, "leaves");
    }

    /// <summary>A wall running along x, from <paramref name="z0"/> to <paramref name="z1"/> thick, with holes measured along x and up.</summary>
    private void WallAlongX(float z0, float z1, float x0, float x1, float height, string material, params Hole[] holes)
    {
        foreach (var (a0, a1, b0, b1) in Subtract(x0, x1, 0f, height, holes))
        {
            Box(new(a0, b0, z0), new(a1, b1, z1), material);
        }
    }

    /// <summary>A wall running along z, from <paramref name="x0"/> to <paramref name="x1"/> thick, with holes measured along z and up.</summary>
    private void WallAlongZ(float x0, float x1, float z0, float z1, float height, string material, params Hole[] holes)
    {
        foreach (var (a0, a1, b0, b1) in Subtract(z0, z1, 0f, height, holes))
        {
            Box(new(x0, b0, a0), new(x1, b1, a1), material);
        }
    }

    /// <summary>A flat roof 0.3 m thick on top of <paramref name="y"/>, with holes measured along x and z.</summary>
    private void Roof(float x0, float x1, float z0, float z1, float y, params Hole[] holes)
    {
        foreach (var (a0, a1, b0, b1) in Subtract(x0, x1, z0, z1, holes))
        {
            Box(new(a0, y, b0), new(a1, y + Wall, b1), "plaster");
        }
    }

    /// <summary>
    /// Cuts holes out of a rectangle, giving the rectangles left. It slices the rectangle at
    /// every hole's sides, then in each slice keeps what no hole covers.
    /// </summary>
    public static List<(float A0, float A1, float B0, float B1)> Subtract(float a0, float a1, float b0, float b1, IReadOnlyList<Hole> holes)
    {
        var cuts = new SortedSet<float> { a0, a1 };
        foreach (var hole in holes)
        {
            cuts.Add(Math.Clamp(hole.From, a0, a1));
            cuts.Add(Math.Clamp(hole.To, a0, a1));
        }

        var left = new List<(float, float, float, float)>();
        var edges = cuts.ToArray();
        for (int i = 0; i + 1 < edges.Length; i++)
        {
            float from = edges[i];
            float to = edges[i + 1];
            if (to - from < 1e-4f)
            {
                continue;
            }

            float mid = (from + to) / 2f;
            var gaps = holes.Where(h => h.From < mid && h.To > mid).OrderBy(h => h.Bottom).ToList();
            float bottom = b0;
            foreach (var gap in gaps)
            {
                if (gap.Bottom > bottom + 1e-4f)
                {
                    left.Add((from, to, bottom, Math.Min(gap.Bottom, b1)));
                }

                bottom = Math.Max(bottom, gap.Top);
            }

            if (b1 > bottom + 1e-4f)
            {
                left.Add((from, to, bottom, b1));
            }
        }

        return left;
    }

    private void Box(Vector3 min, Vector3 max, string material)
    {
        var size = max - min;
        var key = (Round(size), material);
        if (!_boxes.TryGetValue(key, out var mesh))
        {
            mesh = _content.Mesh(Shapes.Box(size, _materials[material], uvPerMetre: UvPerMetre));
            _boxes[key] = mesh;
        }

        _pieces.Add((mesh, Matrix4x4.CreateTranslation((min + max) / 2f)));
    }

    private void Cylinder(Vector3 foot, float radius, float height, string material) =>
        Cone(foot, radius, radius, height, material);

    /// <summary>A cylinder that can narrow towards its top, standing on <paramref name="foot"/>.</summary>
    private void Cone(Vector3 foot, float bottom, float top, float height, string material)
    {
        var key = (new Vector3(bottom, top, height), "round " + material);
        if (!_boxes.TryGetValue(key, out var mesh))
        {
            mesh = _content.Mesh(Shapes.Cylinder(top, bottom, height, 32, _materials[material], uvPerMetre: UvPerMetre));
            _boxes[key] = mesh;
        }

        _pieces.Add((mesh, Matrix4x4.CreateTranslation(foot + new Vector3(0f, height / 2f, 0f))));
    }

    private void Sphere(Vector3 centre, float radius, string material)
    {
        var mesh = _content.Mesh(Shapes.Sphere(radius, 48, 24, _materials[material], uvPerMetre: UvPerMetre));
        _pieces.Add((mesh, Matrix4x4.CreateTranslation(centre)));
    }

    private static Vector3 Round(Vector3 size) =>
        new(MathF.Round(size.X, 3), MathF.Round(size.Y, 3), MathF.Round(size.Z, 3));
}
