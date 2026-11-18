using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// The village's paths: a ring road round the top of the hill and winding lanes running out from the middle, and the
/// spots along them where buildings may stand.
/// </summary>
internal static class VillagePaths
{
    /// <summary>The ring road, then the lanes, flat on the ground plane.</summary>
    public static List<Spline> Lay(RandomStream random)
    {
        var paths = new List<Spline>();

        // The ring: round the top, wobbling in and out.
        float wobble1 = random.Angle();
        float wobble2 = random.Angle();
        var ring = new List<Vector3>();
        const int RingPoints = 18;
        for (int i = 0; i < RingPoints; i++)
        {
            float angle = 2f * MathF.PI * i / RingPoints;
            float radius = 29f + 3.5f * MathF.Sin(2f * angle + wobble1) + 2.5f * MathF.Sin(3f * angle + wobble2) + random.Between(-1f, 1f);
            ring.Add(new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius));
        }

        paths.Add(Spline.Through(ring, closed: true));

        // Lanes out from the middle, each winding its own way down the hill.
        int lanes = random.Between(4, 6);
        float start = random.Angle();
        for (int lane = 0; lane < lanes; lane++)
        {
            float heading = start + 2f * MathF.PI * lane / lanes + random.Between(-0.25f, 0.25f);
            float phase = random.Angle();
            float bend = random.Between(0.25f, 0.45f);
            float length = random.Between(62f, 80f);
            var points = new List<Vector3>();
            for (float r = 6.5f; r <= length; r += 7f)
            {
                float angle = heading + bend * MathF.Sin(r * 0.05f + phase) * MathF.Min(r / 20f, 1f);
                points.Add(new Vector3(MathF.Sin(angle) * r, 0f, MathF.Cos(angle) * r));
            }

            paths.Add(Spline.Through(points));
        }

        return paths;
    }

    /// <summary>
    /// Spots every 4.5 metres along each path, on both sides. The first path is the ring; the lanes keep clear of it at
    /// both ends.
    /// </summary>
    public static List<(Spline Path, float Distance, float Side)> Spots(IReadOnlyList<Spline> paths)
    {
        var spots = new List<(Spline Path, float Distance, float Side)>();
        foreach (var path in paths)
        {
            bool ring = path == paths[0];
            for (float s = ring ? 0f : 9f; s < path.Length - (ring ? 0f : 4f); s += 4.5f)
            {
                spots.Add((path, s, 1f));
                spots.Add((path, s, -1f));
            }
        }

        return spots;
    }

    /// <summary>Points every metre along every path, flat on the ground plane.</summary>
    public static List<Vector3> Points(IReadOnlyList<Spline> paths)
    {
        var points = new List<Vector3>();
        foreach (var path in paths)
        {
            for (float s = 0f; s < path.Length; s += 1f)
            {
                points.Add(path.PointAtDistance(s));
            }
        }

        return points;
    }
}

/// <summary>A building's box across the ground, for keeping buildings apart.</summary>
internal static class Footprint
{
    /// <summary>
    /// Whether two buildings' boxes overlap across the ground. Two rectangles are apart when
    /// some side of one has the whole of the other beyond it, so each one's two axes are tried.
    /// </summary>
    public static bool Overlap(BuildingPlan a, Matrix4x4 placementA, BuildingPlan b, Matrix4x4 placementB)
    {
        Span<Vector2> cornersA = stackalloc Vector2[4];
        Span<Vector2> cornersB = stackalloc Vector2[4];
        Corners(a, placementA, cornersA);
        Corners(b, placementB, cornersB);
        Span<Vector2> axes =
        [
            new(placementA.M11, placementA.M13),
            new(placementA.M31, placementA.M33),
            new(placementB.M11, placementB.M13),
            new(placementB.M31, placementB.M33),
        ];
        foreach (var axis in axes)
        {
            var (minA, maxA) = Project(cornersA, axis);
            var (minB, maxB) = Project(cornersB, axis);
            if (maxA <= minB || maxB <= minA)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The four corners of a building's box across the ground, in the world.</summary>
    private static void Corners(BuildingPlan plan, Matrix4x4 placement, Span<Vector2> corners)
    {
        for (int i = 0; i < 4; i++)
        {
            var local = new Vector3((i & 1) == 0 ? plan.Min.X : plan.Max.X, 0f, (i & 2) == 0 ? plan.Min.Z : plan.Max.Z);
            var world = Vector3.Transform(local, placement);
            corners[i] = new Vector2(world.X, world.Z);
        }
    }

    /// <summary>How far along <paramref name="axis"/> the corners reach, least and most.</summary>
    private static (float Min, float Max) Project(ReadOnlySpan<Vector2> corners, Vector2 axis)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (var corner in corners)
        {
            float along = Vector2.Dot(corner, axis);
            min = MathF.Min(min, along);
            max = MathF.Max(max, along);
        }

        return (min, max);
    }
}
