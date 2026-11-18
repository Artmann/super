using System.Numerics;
using Super;

namespace Gallery;

/// <summary>
/// Places plain boxes, cylinders, cones and spheres in flat matte materials, and plaster walls and roofs with holes cut
/// in them. Boxes and cylinders of the same size and material share one mesh. Everything is static.
/// </summary>
public sealed class GalleryShapes(Content content, Dictionary<string, Surface> materials)
{
    /// <summary>How thick the walls and roofs are.</summary>
    public const float Wall = 0.3f;

    /// <summary>Plaster and the other textures repeat every two metres.</summary>
    private const float UvPerMetre = 0.5f;

    private readonly Dictionary<(Vector3 Size, string Material), ModelRef> _boxes = [];
    private readonly List<(ModelRef Mesh, Matrix4x4 Transform)> _pieces = [];

    /// <summary>Everything placed, each with its own place.</summary>
    public IReadOnlyList<(ModelRef Mesh, Matrix4x4 Transform)> Pieces => _pieces;

    /// <summary>A hole in a wall or a roof, from one corner to the other in the wall's own two axes.</summary>
    public readonly record struct Hole(float From, float To, float Bottom, float Top);

    /// <summary>A plaster wall running along x, from <paramref name="z0"/> to <paramref name="z1"/> thick, with holes measured along x and up.</summary>
    public void WallAlongX(float z0, float z1, float x0, float x1, float height, params Hole[] holes)
    {
        foreach (var (a0, a1, b0, b1) in Subtract(x0, x1, 0f, height, holes))
        {
            Box(new(a0, b0, z0), new(a1, b1, z1), "plaster");
        }
    }

    /// <summary>A plaster wall running along z, from <paramref name="x0"/> to <paramref name="x1"/> thick, with holes measured along z and up.</summary>
    public void WallAlongZ(float x0, float x1, float z0, float z1, float height, params Hole[] holes)
    {
        foreach (var (a0, a1, b0, b1) in Subtract(z0, z1, 0f, height, holes))
        {
            Box(new(x0, b0, a0), new(x1, b1, a1), "plaster");
        }
    }

    /// <summary>A flat plaster roof 0.3 m thick on top of <paramref name="y"/>, with holes measured along x and z.</summary>
    public void Roof(float x0, float x1, float z0, float z1, float y, params Hole[] holes)
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

    /// <summary>A box from one corner to the other, lined up with the axes.</summary>
    public void Box(Vector3 min, Vector3 max, string material)
    {
        var size = max - min;
        var key = (Round(size), material);
        if (!_boxes.TryGetValue(key, out var mesh))
        {
            mesh = content.Mesh(Shapes.Box(size, materials[material], uvPerMetre: UvPerMetre), isStatic: true);
            _boxes[key] = mesh;
        }

        _pieces.Add((mesh, Matrix4x4.CreateTranslation((min + max) / 2f)));
    }

    /// <summary>A cylinder standing on <paramref name="foot"/>.</summary>
    public void Cylinder(Vector3 foot, float radius, float height, string material) =>
        Cone(foot, radius, radius, height, material);

    /// <summary>A cylinder that can narrow towards its top, standing on <paramref name="foot"/>.</summary>
    public void Cone(Vector3 foot, float bottom, float top, float height, string material)
    {
        var key = (new Vector3(bottom, top, height), "round " + material);
        if (!_boxes.TryGetValue(key, out var mesh))
        {
            mesh = content.Mesh(Shapes.Cylinder(top, bottom, height, 32, materials[material], uvPerMetre: UvPerMetre), isStatic: true);
            _boxes[key] = mesh;
        }

        _pieces.Add((mesh, Matrix4x4.CreateTranslation(foot + new Vector3(0f, height / 2f, 0f))));
    }

    /// <summary>A sphere around <paramref name="centre"/>.</summary>
    public void Sphere(Vector3 centre, float radius, string material)
    {
        var mesh = content.Mesh(Shapes.Sphere(radius, 48, 24, materials[material], uvPerMetre: UvPerMetre), isStatic: true);
        _pieces.Add((mesh, Matrix4x4.CreateTranslation(centre)));
    }

    private static Vector3 Round(Vector3 size) =>
        new(MathF.Round(size.X, 3), MathF.Round(size.Y, 3), MathF.Round(size.Z, 3));
}
