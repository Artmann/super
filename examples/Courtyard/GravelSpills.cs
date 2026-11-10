using System.Numerics;

namespace Courtyard;

/// <summary>
/// Where the Courtyard lays its gravel decals: spills of gravel kicked off the flagstones into the mud, along both
/// edges of both arms of the path and round the foot of the barrel. Each box is a <c>gravel.material.cs</c> decal
/// lying on the ground, its +Z towards the side the gravel came from, where the spill has its sharp, thick edge.
/// </summary>
public static class GravelSpills
{
    // The model keeps Blender's origin, so these are art/courtyard.py's own numbers, Blender's x and y.

    /// <summary>The middle line of each arm of the flagstone path (PATH and SPUR).</summary>
    public static readonly Vector2[][] PathArms =
    [
        [new(-0.6f, -9.0f), new(-0.4f, -5.0f), new(0.2f, -2.0f), new(0.9f, 0.2f), new(1.2f, 1.8f), new(2.0f, 2.9f), new(3.4f, 3.25f), new(4.6f, 3.3f)],
        [new(0.5f, -0.9f), new(2.2f, -2.0f), new(3.6f, -3.0f), new(4.6f, -4.4f), new(5.3f, -6.4f), new(5.6f, -9.0f)],
    ];

    /// <summary>How far either side of its middle line the paving reaches (PATH_HALF_WIDTH).</summary>
    public const float PathHalfWidth = 1.05f;

    /// <summary>The puddle's middle and its radii (PUDDLE).</summary>
    public static readonly Vector2 PuddleCentre = new(3.0f, 0.9f);

    /// <summary>The puddle's radii along x and y (PUDDLE).</summary>
    public static readonly Vector2 PuddleRadii = new(1.7f, 0.95f);

    /// <summary>The barrel's middle (build_barrel).</summary>
    public static readonly Vector2 BarrelCentre = new(0.2f, 3.25f);

    /// <summary>The radius of the barrel's foot (build_barrel).</summary>
    public const float BarrelFoot = 0.46f;

    /// <summary>The brick wall's face (WALL_Y).</summary>
    public const float WallY = 4.0f;

    /// <summary>How close to the wall's face a spill's middle may lie.</summary>
    public const float WallGap = 0.3f;

    // The Courtyard's camera is fixed at Blender y -4.5, looking towards the wall. Gravel further back than this is
    // behind it, so it isn't laid.
    private const float NearestY = -5.5f;

    // A spill lies thick from its sharp edge, 0.39 of its length out from its middle towards its +Z end (the image's
    // V of 0.11), back to 0.25 of its length the other way (V 0.75), and thins out beyond that. A path spill's middle
    // is moved this much of its length out from the paving's edge, so its sharp edge lies just on the paving and the
    // rest of it out on the mud.
    private const float ThickOffset = 0.3f;

    /// <summary>
    /// The boxes of every spill, placed at random from <paramref name="seed"/>. Each side of each arm and the ring round
    /// the barrel draw from their own random numbers, so changing how one of them is laid doesn't move the others.
    /// </summary>
    public static List<Matrix4x4> Boxes(int seed)
    {
        var boxes = new List<Matrix4x4>();
        for (int part = 0; part < Parts; part++)
        {
            boxes.AddRange(Spills(seed, part));
        }

        return boxes;
    }

    /// <summary>How many parts the spills are laid in: both sides of each arm of the path, then the ring round the barrel.</summary>
    public static int Parts => PathArms.Length * 2 + 1;

    /// <summary>
    /// The boxes of one part of the spills: for arm <c>a</c>, part <c>2a</c> is its left side and <c>2a + 1</c> its
    /// right, and the last part is the ring round the barrel.
    /// </summary>
    public static List<Matrix4x4> Spills(int seed, int part)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(part, Parts);
        var boxes = new List<Matrix4x4>();
        var random = Stream(seed, part);
        if (part < PathArms.Length * 2)
        {
            Along(boxes, PathArms[part / 2], part % 2 == 0 ? 1f : -1f, random);
        }
        else
        {
            RoundBarrel(boxes, random);
        }

        return boxes;
    }

    // The random numbers for one side of an arm, or the ring, from the game's seed and which one it is. The same seed
    // and stream always give the same numbers, run to run.
    private static Random Stream(int seed, int which)
    {
        return new Random(unchecked(seed * 7919 + which * 104729));
    }

    /// <summary>
    /// Whether a spill's middle can lie at (x, y): off the paving, not in or by the puddle, not against the wall and
    /// not behind the camera.
    /// </summary>
    public static bool OpenMud(Vector2 at)
    {
        Vector2 puddle = (at - PuddleCentre) / PuddleRadii;
        return at.Y > NearestY
            && at.Y < WallY - WallGap
            && puddle.LengthSquared() > 1.5f
            && PathDistance(at) > PathHalfWidth;
    }

    /// <summary>How far a point is from the middle line of the nearest arm of the path (path_distance).</summary>
    public static float PathDistance(Vector2 at)
    {
        float best = float.MaxValue;
        foreach (var arm in PathArms)
        {
            for (int i = 0; i + 1 < arm.Length; i++)
            {
                Vector2 ab = arm[i + 1] - arm[i];
                float t = Math.Clamp(Vector2.Dot(at - arm[i], ab) / ab.LengthSquared(), 0f, 1f);
                best = MathF.Min(best, Vector2.Distance(at, arm[i] + ab * t));
            }
        }

        return best;
    }

    /// <summary>The middle of a box made by <see cref="Boxes"/>, in Blender's x and y.</summary>
    public static Vector2 Centre(Matrix4x4 box)
    {
        return new Vector2(box.M41, -box.M43);
    }

    // Spills of gravel along one edge of an arm of the path, the left for side 1 and the right for -1, each 0.35 to
    // 0.7 m from the last, so they overlap into a ragged band with gaps. Each lies with its thick side just inside the
    // paving's edge and the rest of it out on the mud.
    private static void Along(List<Matrix4x4> boxes, Vector2[] arm, float side, Random random)
    {
        float next = random.NextSingle() * 0.6f;
        float walked = 0f;
        for (int i = 0; i + 1 < arm.Length; i++)
        {
            Vector2 a = arm[i];
            Vector2 b = arm[i + 1];
            float length = Vector2.Distance(a, b);
            Vector2 along = (b - a) / length;
            Vector2 outward = new Vector2(-along.Y, along.X) * side;
            while (next < walked + length)
            {
                float width = Spread(random, 0.5f, 0.8f);
                float deep = width * Spread(random, 0.7f, 0.9f);
                Vector2 middle = a + along * (next - walked);
                Vector2 at = middle + outward * (PathHalfWidth + ThickOffset * deep + Spread(random, -0.1f, 0.15f));
                // Turned a little off square to the path, so the spills don't all point the same way.
                Vector2 toPath = Turn(-outward, Spread(random, -0.4f, 0.4f));
                if (OpenMud(at))
                {
                    boxes.Add(GroundBox(at, toPath, width, deep));
                }

                next += Spread(random, 0.35f, 0.7f);
            }

            walked += length;
        }
    }

    // A ring of gravel round the barrel's foot, thickest against the staves.
    private static void RoundBarrel(List<Matrix4x4> boxes, Random random)
    {
        const int Spills = 14;
        for (int i = 0; i < Spills; i++)
        {
            float angle = MathF.Tau * (i + Spread(random, -0.25f, 0.25f)) / Spills;
            Vector2 outward = new(MathF.Cos(angle), MathF.Sin(angle));
            float width = Spread(random, 0.5f, 0.7f);
            float deep = width * 0.8f;
            // The sharp edge of the thick part is 0.39 of the length from the middle towards the barrel, so a middle
            // 0.42 of a length out from the foot puts that edge against the staves and the rest out on the mud.
            Vector2 at = BarrelCentre + outward * (BarrelFoot + 0.42f * deep + Spread(random, 0f, 0.06f));
            if (OpenMud(at))
            {
                boxes.Add(GroundBox(at, -outward, width, deep));
            }
        }
    }

    // A decal box lying on the ground at Blender's (x, y), `width` across and `length` along `toward`, projecting down
    // onto everything within 20 cm of the yard's zero. The spill's sharp, thick edge is towards the image's V of 0,
    // which lies at the box's +Z, so +Z points along `toward`.
    private static Matrix4x4 GroundBox(Vector2 at, Vector2 toward, float width, float length)
    {
        // Blender's (x, y, z) is (x, z, -y) here.
        var forward = Vector3.Normalize(new Vector3(toward.X, 0f, -toward.Y));
        var up = Vector3.UnitY;
        // +X is up × +Z, so the box isn't mirrored.
        var across = Vector3.Cross(up, forward) * width;
        var depth = up * 0.4f;
        var solid = forward * length;
        return new Matrix4x4(
            across.X, across.Y, across.Z, 0f,
            depth.X, depth.Y, depth.Z, 0f,
            solid.X, solid.Y, solid.Z, 0f,
            at.X, 0f, -at.Y, 1f);
    }

    private static Vector2 Turn(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float Spread(Random random, float min, float max)
    {
        return min + random.NextSingle() * (max - min);
    }
}
