using System.Numerics;

namespace Rendition;

/// <summary>A sphere around something to be baked, such as a draw's part.</summary>
public readonly record struct BakeSphere(Vector3 Center, float Radius);

/// <summary>
/// Decides which baked area of a frame owns a draw or a light: the frame's <see cref="BakedSpace"/>, one of its
/// <see cref="BakedChunk"/>s by index, or nothing. Pure and allocation-free, so it can run per draw per frame.
/// </summary>
public static class BakeAreas
{
    /// <summary>Owned by no baked area.</summary>
    public const int None = -2;

    /// <summary>Owned by the frame's baked space.</summary>
    public const int Space = -1;

    /// <summary>
    /// The space if <paramref name="world"/> overlaps the space's box, else the first chunk whose box holds its
    /// centre, else <see cref="None"/>. The space comes first because it is baked as it stands, sun included.
    /// </summary>
    public static int OwnerOfDraw(BakeSphere world, BakedSpace? space, ReadOnlySpan<BakedChunk> chunks)
    {
        if (space is { } box && SphereTouchesBox(world, box.Min, box.Max))
        {
            return Space;
        }

        return ChunkHolding(world.Center, chunks);
    }

    /// <summary>
    /// The owner of a whole model, from its shown parts' spheres in world space, in the model's part order:
    /// <see cref="OwnerOfDraw"/> of their <see cref="Union"/>, so a model is never split between areas. Super gives
    /// bake ids by it and <see cref="BakeFreshness.Resolve"/> files the ids by it, so the two always agree.
    /// </summary>
    public static int OwnerOfModel(ReadOnlySpan<BakeSphere> worldParts, BakedSpace? space, ReadOnlySpan<BakedChunk> chunks) =>
        OwnerOfDraw(Union(worldParts), space, chunks);

    /// <summary>The space if <paramref name="position"/> is in its box, else the first chunk holding it, else <see cref="None"/>.</summary>
    public static int OwnerOfLight(Vector3 position, BakedSpace? space, ReadOnlySpan<BakedChunk> chunks)
    {
        if (space is { } box && PointInBox(position, box.Min, box.Max))
        {
            return Space;
        }

        return ChunkHolding(position, chunks);
    }

    /// <summary>A sphere holding every one of <paramref name="parts"/>; the point at the origin when there are none.</summary>
    public static BakeSphere Union(ReadOnlySpan<BakeSphere> parts)
    {
        if (parts.IsEmpty)
        {
            return default;
        }

        var union = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            union = Union(union, parts[i]);
        }

        return union;
    }

    /// <summary>
    /// <paramref name="local"/> moved by <paramref name="transform"/>. The radius grows by the transform's largest
    /// axis scale, so the sphere still holds what it held.
    /// </summary>
    public static BakeSphere Transform(BakeSphere local, in Matrix4x4 transform)
    {
        float scale = MathF.Sqrt(MathF.Max(
            new Vector3(transform.M11, transform.M12, transform.M13).LengthSquared(),
            MathF.Max(
                new Vector3(transform.M21, transform.M22, transform.M23).LengthSquared(),
                new Vector3(transform.M31, transform.M32, transform.M33).LengthSquared())));
        return new BakeSphere(Vector3.Transform(local.Center, transform), local.Radius * scale);
    }

    private static BakeSphere Union(BakeSphere a, BakeSphere b)
    {
        float distance = Vector3.Distance(a.Center, b.Center);
        if (distance + b.Radius <= a.Radius)
        {
            return a;
        }

        if (distance + a.Radius <= b.Radius)
        {
            return b;
        }

        // Neither holds the other, so distance > 0. The new sphere spans from a's far side to b's far side.
        float radius = (distance + a.Radius + b.Radius) * 0.5f;
        var center = a.Center + (b.Center - a.Center) * ((radius - a.Radius) / distance);
        return new BakeSphere(center, radius);
    }

    private static int ChunkHolding(Vector3 position, ReadOnlySpan<BakedChunk> chunks)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            if (ChunkHolds(chunks[i], position))
            {
                return i;
            }
        }

        return None;
    }

    /// <summary>
    /// A world point (<paramref name="w"/> 1) or direction (<paramref name="w"/> 0) in the own space of the rigid
    /// transform <paramref name="chunk"/> (<see cref="BakeFiles.IsRigid"/>). This is the one inverse every bake uses:
    /// for which chunk holds a point, for a chunk's bake ids, and for its lights' hashes.
    /// </summary>
    /// <remarks>
    /// The inverse of a rigid transform is the translation taken off, for a point, and then the transpose of the
    /// rotation. Rows are the chunk's axes in world space, so each local coordinate is a dot product with one. Doubles
    /// keep a chunk far from the origin from losing the last centimetres.
    /// </remarks>
    public static (double X, double Y, double Z) IntoChunk(in Matrix4x4 chunk, double x, double y, double z, double w)
    {
        double dx = x - w * chunk.M41;
        double dy = y - w * chunk.M42;
        double dz = z - w * chunk.M43;
        return (
            dx * chunk.M11 + dy * chunk.M12 + dz * chunk.M13,
            dx * chunk.M21 + dy * chunk.M22 + dz * chunk.M23,
            dx * chunk.M31 + dy * chunk.M32 + dz * chunk.M33);
    }

    /// <summary>
    /// <paramref name="world"/> as seen from a rigid chunk: world times the chunk's inverse, row by row through
    /// <see cref="IntoChunk"/>. Worked in doubles, so a chunk far from the origin gives the same numbers as one at it.
    /// A chunk's bake ids and its bake both take its draws by this rule.
    /// </summary>
    public static Matrix4x4 Relative(in Matrix4x4 world, in Matrix4x4 chunk)
    {
        var result = default(Matrix4x4);
        for (int row = 0; row < 4; row++)
        {
            // A row with a 1 in its last column is a point, and takes off the chunk's translation; a direction doesn't.
            double w = world[row, 3];
            var (x, y, z) = IntoChunk(chunk, world[row, 0], world[row, 1], world[row, 2], w);
            result[row, 0] = (float)x;
            result[row, 1] = (float)y;
            result[row, 2] = (float)z;
            result[row, 3] = (float)w;
        }

        return result;
    }

    private static bool ChunkHolds(in BakedChunk chunk, Vector3 position)
    {
        var (x, y, z) = IntoChunk(chunk.Transform, position.X, position.Y, position.Z, 1.0);
        return x >= chunk.Min.X && x <= chunk.Max.X
            && y >= chunk.Min.Y && y <= chunk.Max.Y
            && z >= chunk.Min.Z && z <= chunk.Max.Z;
    }

    private static bool PointInBox(Vector3 point, Vector3 min, Vector3 max) =>
        point.X >= min.X && point.X <= max.X
        && point.Y >= min.Y && point.Y <= max.Y
        && point.Z >= min.Z && point.Z <= max.Z;

    private static bool SphereTouchesBox(BakeSphere sphere, Vector3 min, Vector3 max)
    {
        var nearest = Vector3.Clamp(sphere.Center, min, max);
        return Vector3.DistanceSquared(nearest, sphere.Center) <= sphere.Radius * sphere.Radius;
    }
}
