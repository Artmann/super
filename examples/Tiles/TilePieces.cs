using System.Numerics;
using Rendition;
using Super;

namespace TilesExample;

/// <summary>
/// The tiles, made in code: walls that join up and floor slabs for the dungeon, and land that slopes down to the
/// water on its open sides for the island. There's one tile for every pattern of joined sides that a turn can't
/// make from another, 6 for squares and 14 for hexes, so painting always finds an exact fit.
/// </summary>
internal static class TilePieces
{
    private const float WallHeight = 2.4f;
    private const float WallThickness = 0.7f;
    private const float LandHeight = 0.5f;
    private const float ShoreHeight = 0.12f;

    private static readonly Surface Stone = new() { Roughness = 0.85f };

    /// <summary>The dungeon's walls, its floor slabs in three shades and a crate.</summary>
    public static Tile[] Dungeon(Content content, SquareGrid grid)
    {
        var sides = SideDirections(grid);
        float half = grid.CellSize.X / 2f;
        var tiles = Patterns(sides.Length)
            .Select(joins => new Tile(
                $"wall-{Name(joins, sides.Length)}",
                content.Mesh(Flat(Wall(sides, joins, half)), isStatic: true),
                "walls",
                Name(joins, sides.Length),
                Color: new Vector3(0.42f, 0.4f, 0.38f)))
            .ToList();

        var slab = content.Mesh(Shapes.Box(new Vector3(grid.CellSize.X - 0.04f, 0.2f, grid.CellSize.Y - 0.04f), Stone), isStatic: true);
        tiles.Add(new Tile("floor-light", slab, "floor", Offset: new Vector3(0f, 0.1f, 0f), Color: new Vector3(0.5f, 0.47f, 0.42f)));
        tiles.Add(new Tile("floor-dark", slab, "floor", Offset: new Vector3(0f, 0.08f, 0f), Color: new Vector3(0.36f, 0.34f, 0.31f)));
        tiles.Add(new Tile("floor-moss", slab, "floor", Offset: new Vector3(0f, 0.09f, 0f), Color: new Vector3(0.38f, 0.41f, 0.33f)));

        var crate = content.Mesh(Shapes.Box(new Vector3(0.9f, 0.9f, 1.2f), Stone), isStatic: true);
        tiles.Add(new Tile("crate", crate, Offset: new Vector3(0f, 0.65f, 0f), Color: new Vector3(0.55f, 0.38f, 0.2f)));
        return [.. tiles];
    }

    /// <summary>The island's land, green inland and sandy on the coast, the water round it and the trees on top.</summary>
    public static Tile[] Island(Content content, HexGrid grid)
    {
        var sides = SideDirections(grid);
        Span<Vector3> corners = stackalloc Vector3[6];
        grid.MovedTo(default).Corners(default, corners);
        var middle = grid.MovedTo(default).Center(default);
        var outline = corners.ToArray().Select(corner => corner - middle).ToArray();

        int inland = (1 << sides.Length) - 1;
        var tiles = Patterns(sides.Length)
            .Select(joins => new Tile(
                $"land-{Name(joins, sides.Length)}",
                content.Mesh(Flat(Land(sides, outline, joins)), isStatic: true),
                "land",
                Name(joins, sides.Length),
                Color: joins == inland ? new Vector3(0.3f, 0.5f, 0.22f) : new Vector3(0.78f, 0.7f, 0.48f)))
            .ToList();

        var water = content.Mesh(Shapes.Hex(grid.Size, 0.1f, new Surface { Roughness = 0.15f }), isStatic: true);
        tiles.Add(new Tile("water", water, "water", Offset: new Vector3(0f, 0.1f, 0f), Color: new Vector3(0.1f, 0.3f, 0.45f)));

        var tree = content.Mesh(Shapes.Cone(0.45f, 1.4f, 7, Stone), isStatic: true);
        tiles.Add(new Tile("tree", tree, Offset: new Vector3(0f, 0.7f, 0f), Color: new Vector3(0.15f, 0.35f, 0.15f)));
        return [.. tiles];
    }

    // The patterns of joined sides that no turn of another one makes, each the smallest number among its turns.
    private static IEnumerable<int> Patterns(int sides) =>
        Enumerable.Range(0, 1 << sides).Where(joins => Enumerable.Range(1, sides - 1).All(turn => Turned(joins, turn, sides) >= joins));

    private static int Turned(int joins, int turn, int sides) => ((joins >> turn) | (joins << (sides - turn))) & ((1 << sides) - 1);

    private static string Name(int joins, int sides) =>
        string.Concat(Enumerable.Range(0, sides).Select(side => (joins & (1 << side)) != 0 ? '1' : '0'));

    // Which way each of a cell's sides faces on the grid's own X and Z, in Sides order.
    private static Vector3[] SideDirections<TCell>(IGrid<TCell> grid)
        where TCell : struct, IGridCell<TCell>
    {
        var flat = grid.MovedTo(default);
        var middle = flat.Center(default);
        var sides = TCell.Sides;
        var directions = new Vector3[sides.Length];
        for (int side = 0; side < sides.Length; side++)
        {
            directions[side] = Vector3.Normalize(flat.Center(sides[side]) - middle);
        }

        return directions;
    }

    // A post in the middle and an arm out to every joined side.
    private static List<Vector3> Wall(Vector3[] sides, int joins, float half)
    {
        var triangles = new List<Vector3>();
        float thick = WallThickness / 2f;
        Box(triangles, new Vector3(-thick, 0f, -thick), new Vector3(thick, WallHeight, thick));
        for (int side = 0; side < sides.Length; side++)
        {
            if ((joins & (1 << side)) != 0)
            {
                var reach = sides[side] * half;
                var across = new Vector3(sides[side].Z, 0f, sides[side].X) * thick;
                var min = Vector3.Min(reach, Vector3.Zero) - Vector3.Abs(across);
                var max = Vector3.Max(reach, Vector3.Zero) + Vector3.Abs(across);
                Box(triangles, min, max with { Y = WallHeight });
            }
        }

        return triangles;
    }

    // A top that stays high at the middle and drops to the shore at every corner next to an open side, standing on
    // the cell's outline. Two joined cells agree on the corners they share, so the land runs on without a seam.
    private static List<Vector3> Land(Vector3[] sides, Vector3[] outline, int joins)
    {
        int count = outline.Length;
        var top = new Vector3[count];
        for (int corner = 0; corner < count; corner++)
        {
            bool open = Enumerable.Range(0, sides.Length)
                .Where(side => (joins & (1 << side)) == 0)
                .Any(side => Vector3.Dot(Vector3.Normalize(outline[corner]), sides[side]) > 0.5f);
            top[corner] = outline[corner] with { Y = open ? ShoreHeight : LandHeight };
        }

        var triangles = new List<Vector3>();
        var peak = new Vector3(0f, LandHeight, 0f);
        for (int corner = 0; corner < count; corner++)
        {
            int next = (corner + 1) % count;
            Face(triangles, peak, top[corner], top[next]);
            Face(triangles, top[corner], outline[corner], outline[next]);
            Face(triangles, top[corner], outline[next], top[next]);
        }

        return triangles;
    }

    private static void Box(List<Vector3> triangles, Vector3 min, Vector3 max)
    {
        Vector3 Corner(int i) => new((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        int[][] faces = [[0, 2, 6, 4], [1, 5, 7, 3], [0, 4, 5, 1], [2, 3, 7, 6], [0, 1, 3, 2], [4, 6, 7, 5]];
        foreach (var face in faces)
        {
            Face(triangles, Corner(face[0]), Corner(face[1]), Corner(face[2]));
            Face(triangles, Corner(face[0]), Corner(face[2]), Corner(face[3]));
        }
    }

    // Adds a triangle whose points go round clockwise seen from the side it faces, as the corners above are listed.
    // Shapes.Flat faces it along cross(b − a, c − a), so b and c swap places.
    private static void Face(List<Vector3> triangles, Vector3 a, Vector3 b, Vector3 c)
    {
        triangles.Add(a);
        triangles.Add(c);
        triangles.Add(b);
    }

    private static GeneratedMesh Flat(List<Vector3> triangles)
    {
        var vertices = triangles.Select(point => new Vertex(point, Vector3.UnitY, Vector4.Zero, new Vector2(point.X + point.Y, point.Z))).ToArray();
        var indices = Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray();
        return Shapes.Flat(new GeneratedMesh(vertices, indices, Stone));
    }
}
