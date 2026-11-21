using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>One square of the grass's lattice that a frame scatters blades over, as Shaders/grass_scatter.comp reads it.</summary>
/// <param name="X">The square's place along x, in squares from the world's origin.</param>
/// <param name="Z">Its place along z.</param>
/// <param name="Level">
/// The finest level of the lattice it keeps: 0 keeps every point, and each level up keeps one point in four, as
/// the square is far enough that the finer ones have faded out.
/// </param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GrassTile(int X, int Z, int Level)
{
    private readonly int _unused;
}

/// <summary>
/// Where grass grows round the camera. Blades stand on a lattice fixed to the world, so they never crawl as the camera
/// moves, and each point of it has a level. Every point is level 0 or more, every second point along both axes level 1
/// or more, every fourth level 2 or more, and every eighth level 3. A level's blades reach out to its radius, shrinking
/// away over the last part of it, so the grass thins by a quarter at each ring. The lattice is cut into squares, and
/// each frame scatters the squares the camera can see. See "Grass" in docs/design.md.
/// </summary>
internal static class GrassField
{
    /// <summary>How many different looks a frame's grass can have.</summary>
    public const int MaxKinds = 8;

    /// <summary>The finest lattice's spacing, in metres.</summary>
    public const float Spacing = 0.125f;

    /// <summary>The finest lattice's points along each side of a square.</summary>
    public const int TilePoints = 32;

    /// <summary>A square's side, in metres.</summary>
    public const float TileSize = Spacing * TilePoints;

    /// <summary>The most squares a frame scatters. Further ones are left bare.</summary>
    public const int MaxTiles = 8192;

    /// <summary>The ground's height map, from above, is this many texels across.</summary>
    public const int CaptureSize = 1024;

    /// <summary>The share of a level's radius over which its blades shrink away.</summary>
    public const float FadeShare = 0.3f;

    /// <summary>How far each level reaches at a detail of 1, in metres.</summary>
    public static readonly Vector4 BaseRadii = new(16f, 32f, 64f, 120f);

    // How far a blade can stand above or reach out of the square its root is in, wind and all.
    private const float BladeReach = 2f;

    /// <summary>How far each level reaches at <paramref name="detail"/>.</summary>
    public static Vector4 Radii(float detail) => BaseRadii * detail;

    /// <summary>
    /// The finest level a square whose nearest point is <paramref name="nearest"/> metres from the camera, across the
    /// ground, keeps, or -1 when it is past the last ring.
    /// </summary>
    public static int TileLevel(float nearest, Vector4 radii)
    {
        for (int level = 0; level < 4; level++)
        {
            if (nearest < radii[level])
            {
                return level;
            }
        }

        return -1;
    }

    /// <summary>
    /// The square of ground whose height map is drawn from above, round the camera: its corner with the least x and z,
    /// and its texels' size. It reaches a square past the last ring, and moves in whole texels, so the heights the
    /// blades are given don't shimmer.
    /// </summary>
    public static (Vector2 Corner, float Texel) CaptureArea(Vector3 camera, Vector4 radii)
    {
        float half = radii.W + TileSize;
        float texel = 2f * half / CaptureSize;
        var middle = new Vector2(MathF.Round(camera.X / texel), MathF.Round(camera.Z / texel)) * texel;
        return (middle - new Vector2(half), texel);
    }

    /// <summary>
    /// The camera's left, right, bottom, top and near planes, from its view and projection with reversed depth, each
    /// with its normal pointing in and of unit length, so a point's distance in front is <c>dot(normal, p) + w</c>.
    /// </summary>
    public static void Planes(in Matrix4x4 viewProjection, Span<Vector4> planes)
    {
        var m = viewProjection;
        var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var z = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var w = new Vector4(m.M14, m.M24, m.M34, m.M44);
        planes[0] = Unit(w + x);
        planes[1] = Unit(w - x);
        planes[2] = Unit(w + y);
        planes[3] = Unit(w - y);
        planes[4] = Unit(w - z);
    }

    /// <summary>
    /// Lists the squares the camera at <paramref name="camera"/> may see grass in, row by row,
    /// into <paramref name="tiles"/>, and returns how many. A square counts as tall as the last ring is far, round
    /// the camera's height, as the ground's height isn't known here.
    /// </summary>
    public static int Plan(Vector3 camera, ReadOnlySpan<Vector4> planes, Vector4 radii, Span<GrassTile> tiles)
    {
        float reach = radii.W;
        int first = (int)MathF.Floor((camera.X - reach) / TileSize);
        int last = (int)MathF.Floor((camera.X + reach) / TileSize);
        int firstRow = (int)MathF.Floor((camera.Z - reach) / TileSize);
        int lastRow = (int)MathF.Floor((camera.Z + reach) / TileSize);
        int count = 0;
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = first; column <= last; column++)
            {
                var low = new Vector2(column, row) * TileSize;
                var high = low + new Vector2(TileSize);
                var at = new Vector2(camera.X, camera.Z);
                float nearest = Vector2.Distance(at, Vector2.Clamp(at, low, high));
                int level = TileLevel(nearest, radii);
                if (level < 0 || !InView(planes, new Vector3(low.X, camera.Y - reach, low.Y), new Vector3(high.X, camera.Y + reach, high.Y)))
                {
                    continue;
                }

                if (count == tiles.Length)
                {
                    return count;
                }

                tiles[count++] = new GrassTile(column, row, level);
            }
        }

        return count;
    }

    // Whether any of the box, grown by how far a blade reaches, is in front of every plane.
    private static bool InView(ReadOnlySpan<Vector4> planes, Vector3 low, Vector3 high)
    {
        foreach (var plane in planes)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            var furthest = Vector3.ConditionalSelect(Vector3.GreaterThan(normal, Vector3.Zero), high, low);
            if (Vector3.Dot(normal, furthest) + plane.W < -BladeReach)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The index of <paramref name="look"/> among the first <paramref name="count"/> of <paramref name="looks"/>,
    /// adding it when it is new. Throws when a frame's grass has more looks than <see cref="MaxKinds"/>.
    /// </summary>
    public static int KindOf(Span<GrassSettings> looks, ref int count, in GrassSettings look)
    {
        for (int i = 0; i < count; i++)
        {
            if (looks[i] == look)
            {
                return i;
            }
        }

        if (count == MaxKinds)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A frame's grass can have at most {MaxKinds} different looks."), "view");
        }

        looks[count] = look;
        return count++;
    }

    private static Vector4 Unit(Vector4 plane) => plane / new Vector3(plane.X, plane.Y, plane.Z).Length();
}
