using System.Numerics;
using Super;

namespace OutpostExample;

/// <summary>The playing field: a grid of square tiles, with tile (0, 0) in one corner, and a road across it.</summary>
public sealed class Field
{
    public const int Width = 15;
    public const int Depth = 9;
    public const float TileSize = 2f;

    /// <summary>The tiles where the road turns, from where enemies come in to the base.</summary>
    private static readonly (int X, int Z)[] Corners = [(0, 1), (4, 1), (4, 6), (10, 6), (10, 2), (14, 2)];

    private readonly HashSet<(int X, int Z)> _road = [];

    public Field()
    {
        // Walk from each corner to the next, one tile at a time, marking every tile on the way.
        for (int i = 0; i < Corners.Length - 1; i++)
        {
            var (x, z) = Corners[i];
            var to = Corners[i + 1];
            _road.Add((x, z));
            while ((x, z) != to)
            {
                x += Math.Sign(to.X - x);
                z += Math.Sign(to.Z - z);
                _road.Add((x, z));
            }
        }

        Road = Spline.Through(Corners.Select(corner => Center(corner.X, corner.Z)).ToArray());
    }

    /// <summary>The path enemies walk, through the middle of the road tiles.</summary>
    public Spline Road { get; }

    /// <summary>Where the base stands: the last tile of the road.</summary>
    public Vector3 BaseCenter => Center(Corners[^1].X, Corners[^1].Z);

    /// <summary>The middle of a tile, on the ground. The middle tile, (7, 4), is at the origin.</summary>
    public Vector3 Center(int x, int z) =>
        new((x - Width / 2) * TileSize, 0f, (z - Depth / 2) * TileSize);

    /// <summary>Whether a tile is part of the road.</summary>
    public bool IsRoad(int x, int z) => _road.Contains((x, z));

    /// <summary>The tile a point on the ground is in, or null when it's off the field.</summary>
    public (int X, int Z)? TileAt(Vector3 point)
    {
        int x = (int)MathF.Floor(point.X / TileSize + 0.5f) + Width / 2;
        int z = (int)MathF.Floor(point.Z / TileSize + 0.5f) + Depth / 2;
        if (x < 0 || x >= Width || z < 0 || z >= Depth)
        {
            return null;
        }

        return (x, z);
    }
}
