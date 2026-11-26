using System.Numerics;

namespace Super;

/// <summary>
/// Where a grid sits in the world: the point its cell edges start from and how it is turned. The grid lies flat on its
/// own X and Z, so with no turn it lies on level ground at the origin's height. Give it a new placement each frame to
/// make it follow something that moves, such as a ship's deck. See docs/grids.md.
/// </summary>
/// <param name="Origin">Where the grid's own 0, 0 is in the world.</param>
/// <param name="Rotation">How the grid is turned. The default, no turn, lies flat with its X and Z on the world's.</param>
public readonly record struct GridPlacement(Vector3 Origin, Quaternion Rotation)
{
    /// <summary>A grid lying flat at <paramref name="origin"/>, turned by nothing.</summary>
    public GridPlacement(Vector3 origin)
        : this(origin, Quaternion.Identity)
    {
    }

    /// <summary>The way out of the grid's top side.</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Turn);

    /// <summary>
    /// Where a world position lands on the grid, on the grid's own X and Z. A point above or below the grid lands
    /// straight under or over itself.
    /// </summary>
    public Vector2 OnGrid(Vector3 world)
    {
        var local = Vector3.Transform(world - Origin, Quaternion.Conjugate(Turn));
        return new Vector2(local.X, local.Z);
    }

    /// <summary>The world position of a point on the grid's own X and Z.</summary>
    public Vector3 InWorld(Vector2 onGrid) => Origin + Vector3.Transform(new Vector3(onGrid.X, 0f, onGrid.Y), Turn);

    /// <summary>
    /// Where a ray, such as the mouse's from <see cref="Camera.Ray"/>, meets the grid, on its own X and Z, or nothing
    /// when it runs alongside the grid or points away from it.
    /// </summary>
    public Vector2? Hit(Ray ray) => Along(ray) is { } along ? OnGrid(ray.Origin + ray.Direction * along) : null;

    /// <summary>How far along a ray it meets the grid's plane, in lengths of its direction, or nothing when it doesn't.</summary>
    internal float? Along(Ray ray)
    {
        var up = Up;
        float towards = Vector3.Dot(ray.Direction, up);
        if (MathF.Abs(towards) < 1e-6f)
        {
            return null;
        }

        float along = Vector3.Dot(Origin - ray.Origin, up) / towards;
        return along >= 0f ? along : null;
    }

    /// <summary>The turn, with the default of all zeros read as no turn.</summary>
    private Quaternion Turn => Rotation == default ? Quaternion.Identity : Quaternion.Normalize(Rotation);
}
