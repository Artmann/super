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

    // The transform is rigid (BakeFiles.IsRigid), so its inverse is the transpose of its rotation after taking off
    // the translation. Rows are the chunk's axes in world space, so each local coordinate is a dot product with one.
    // Doubles keep a chunk far from the origin from losing the last centimetres.
    private static bool ChunkHolds(in BakedChunk chunk, Vector3 position)
    {
        var m = chunk.Transform;
        double dx = (double)position.X - m.M41;
        double dy = (double)position.Y - m.M42;
        double dz = (double)position.Z - m.M43;
        double x = dx * m.M11 + dy * m.M12 + dz * m.M13;
        double y = dx * m.M21 + dy * m.M22 + dz * m.M23;
        double z = dx * m.M31 + dy * m.M32 + dz * m.M33;
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
