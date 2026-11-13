using System.Numerics;
using Super;

namespace Hamlet;

/// <summary>
/// One building in the village: a kind's plan under one placement transform, and the lamp
/// timing it was given. The plan, colours and all, never changes per building, so the
/// placement is all that moves it into the world.
/// </summary>
internal sealed record Building(
    int Index,
    BuildingPlan Plan,
    Matrix4x4 Placement,
    float Phase,
    float Dusk)
{
    /// <summary>The middle of the building's box on the ground, in the world.</summary>
    public Vector3 Middle => Vector3.Transform(Plan.Middle, Placement);

    /// <summary>A point in the building's own space, in the world.</summary>
    public Vector3 ToWorld(Vector3 local) => Vector3.Transform(local, Placement);

    /// <summary>A direction in the building's own space, in the world.</summary>
    public Vector3 DirectionToWorld(Vector3 local) => Vector3.TransformNormal(local, Placement);

    /// <summary>Whether a point in the world is inside the building's box, grown by a margin.</summary>
    public bool Contains(Vector3 world, float margin)
    {
        Matrix4x4.Invert(Placement, out var inverse);
        var local = Vector3.Transform(world, inverse);
        return local.X > Plan.Min.X - margin && local.X < Plan.Max.X + margin
            && local.Z > Plan.Min.Z - margin && local.Z < Plan.Max.Z + margin
            && local.Y > Plan.Min.Y - margin && local.Y < Plan.Max.Y + margin;
    }

    /// <summary>How far a point on the ground is outside the building's footprint, or 0 inside it.</summary>
    public float GroundDistance(float x, float z, Matrix4x4 inverse)
    {
        var local = Vector3.Transform(new Vector3(x, 0f, z), inverse);
        float dx = MathF.Max(MathF.Max(Plan.Min.X - local.X, local.X - Plan.Max.X), 0f);
        float dz = MathF.Max(MathF.Max(Plan.Min.Z - local.Z, local.Z - Plan.Max.Z), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// The kinds of building, each built once with its own colours. Cottages come in one plan
/// per furniture layout. All of them are built up front, as their meshes are made static
/// with lightmap UVs, which takes long enough that building one when R asks for a new
/// village would hitch.
/// </summary>
internal sealed class Kinds(Content content, Palette palette)
{
    // The well and the tower are small across, so their bakes blend into the live light over less.
    private const float NarrowEdgeBlend = 0.4f;

    private readonly BuildingPlan[] _cottages =
    [
        .. CottageLayout.All.Select((layout, i) =>
            Coloured(Hamlet.Cottage.Build(content, palette, layout), i) with { Layout = layout }),
    ];

    public BuildingPlan Cottage(int layout) => _cottages[layout];

    public BuildingPlan Workshop { get; } = Coloured(Hamlet.Workshop.Build(content, palette), CottageLayout.All.Length);

    public BuildingPlan Tower { get; } =
        Coloured(Hamlet.Tower.Build(content, palette), CottageLayout.All.Length + 1) with { EdgeBlend = NarrowEdgeBlend };

    public BuildingPlan Well { get; } =
        Coloured(Hamlet.Well.Build(content, palette), CottageLayout.All.Length + 2) with { EdgeBlend = NarrowEdgeBlend };

    private static BuildingPlan Coloured(BuildingPlan plan, int variant)
    {
        var (roof, accent) = Palette.Colours(variant);
        return plan with { RoofColor = roof, AccentColor = accent };
    }
}

/// <summary>
/// A village on a hill, all of it from one seed: the ground, a ring road round the top with
/// winding lanes running out from the middle, and about forty buildings along them, each
/// turned to face its path. A well stands in the middle.
/// </summary>
internal sealed class Village
{
    /// <summary>Half a path's width.</summary>
    public const float PathHalfWidth = 1.4f;

    /// <summary>How far the ground reaches from the middle each way.</summary>
    public const float GroundReach = 192f;

    private const int Wells = 4;
    private const int Towers = 4;
    private const int Workshops = 8;
    private const int Cottages = 24;
    private const float PadBlend = 5f;

    private readonly Noise _noise;
    private readonly List<Building> _buildings = [];
    private readonly List<Matrix4x4> _inverses = [];
    private readonly List<float> _levels = [];
    private readonly List<Spline> _paths = [];

    private Village(int seed, Kinds kinds)
    {
        Seed = seed;
        var random = new GameRandom(seed);
        _noise = new Noise(random.Stream("ground"));
        LayPaths(random.Stream("paths"));
        Place(random.Stream("buildings"), kinds);
    }

    /// <summary>The seed the village was made from.</summary>
    public int Seed { get; }

    /// <summary>The buildings, in the order they were placed.</summary>
    public IReadOnlyList<Building> Buildings => _buildings;

    /// <summary>The paths, flat on the ground plane. The first is the ring road.</summary>
    public IReadOnlyList<Spline> Paths => _paths;

    /// <summary>The ring road round the top of the hill, which the cart drives.</summary>
    public Spline Ring => _paths[0];

    /// <summary>How many different furniture layouts the cottages use.</summary>
    public int CottageLayouts { get; private set; }

    public static Village Generate(int seed, Kinds kinds) => new(seed, kinds);

    /// <summary>The ground's height, levelled under every building.</summary>
    public float Height(float x, float z)
    {
        float height = Hill(x, z);
        for (int i = 0; i < _buildings.Count; i++)
        {
            float distance = _buildings[i].GroundDistance(x, z, _inverses[i]);
            if (distance < PadBlend)
            {
                float t = distance / PadBlend;
                float weight = 1f - t * t * (3f - 2f * t);
                height = float.Lerp(height, _levels[i], weight);
            }
        }

        return height;
    }

    /// <summary>The bare hill, before any building levels it.</summary>
    private float Hill(float x, float z)
    {
        float r2 = x * x + z * z;
        float hill = 15f * MathF.Exp(-r2 / (2f * 52f * 52f));
        float roll = (_noise.Layered(x * 0.018f + 3.1f, z * 0.018f + 7.7f, 4) - 0.5f) * 7f;
        float far = MathF.Max(MathF.Sqrt(r2) - 110f, 0f) * 0.08f;
        return hill + roll + far;
    }

    private void LayPaths(RandomStream random)
    {
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

        _paths.Add(Spline.Through(ring, closed: true));

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

            _paths.Add(Spline.Through(points));
        }
    }

    private void Place(RandomStream random, Kinds kinds)
    {
        // A handful of furniture layouts for this seed, shared by its cottages.
        int[] layouts = [.. Enumerable.Range(0, CottageLayout.All.Length)];
        random.Shuffle(layouts);
        CottageLayouts = random.Between(3, 5);

        var plans = new List<BuildingPlan>();
        for (int i = 0; i < Wells - 1; i++)
        {
            plans.Add(kinds.Well);
        }

        for (int i = 0; i < Towers; i++)
        {
            plans.Add(kinds.Tower);
        }

        for (int i = 0; i < Workshops; i++)
        {
            plans.Add(kinds.Workshop);
        }

        for (int i = 0; i < Cottages; i++)
        {
            plans.Add(kinds.Cottage(layouts[i % CottageLayouts]));
        }

        random.Shuffle(plans);

        // One well in the middle, where the lanes meet.
        Add(random, kinds.Well, Matrix4x4.CreateRotationY(random.Angle()));

        // Spots along every path on both sides, tried in a random order.
        var spots = new List<(Spline Path, float Distance, float Side)>();
        foreach (var path in _paths)
        {
            bool ring = path == _paths[0];
            for (float s = ring ? 0f : 9f; s < path.Length - (ring ? 0f : 4f); s += 4.5f)
            {
                spots.Add((path, s, 1f));
                spots.Add((path, s, -1f));
            }
        }

        random.Shuffle(spots);
        var lanePoints = PathPoints();
        foreach (var plan in plans)
        {
            for (int i = 0; i < spots.Count; i++)
            {
                var (path, distance, side) = spots[i];
                var pose = path.FrameAtDistance(distance);
                var along = Vector3.Normalize(new Vector3(pose.Forward.X, 0f, pose.Forward.Z));
                var outward = new Vector3(along.Z, 0f, -along.X) * side;
                var origin = pose.Position + outward * (PathHalfWidth + 0.8f + plan.Max.Z);
                float yaw = MathF.Atan2(-outward.X, -outward.Z) + random.Between(-0.18f, 0.18f);
                var placement = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(origin);
                if (Fits(plan, placement, lanePoints))
                {
                    spots.RemoveAt(i);
                    Add(random, plan, placement);
                    break;
                }
            }
        }
    }

    private void Add(RandomStream random, BuildingPlan plan, Matrix4x4 placement)
    {
        var middle = Vector3.Transform(plan.Middle, placement);
        float level = Hill(middle.X, middle.Z);
        placement.M42 = level;

        // Buildings once took a random roof and strong colour. Each kind has its own colours now, so every copy
        // shares one bake, but the picks are still drawn so each seed's lamp timings and layout stay as they were.
        _ = random.Pick(Palette.Roofs);
        _ = random.Pick(Palette.Accents);
        var building = new Building(
            _buildings.Count,
            plan,
            placement,
            random.Between(0f, 100f),
            random.Between(-0.07f, 0.06f));
        Matrix4x4.Invert(placement, out var inverse);
        _buildings.Add(building);
        _inverses.Add(inverse);
        _levels.Add(level);
    }

    /// <summary>Whether a building would keep clear of the others and of every path, and stay on the hill.</summary>
    private bool Fits(BuildingPlan plan, Matrix4x4 placement, List<Vector3> pathPoints)
    {
        var middle = Vector3.Transform(plan.Middle, placement);
        if (new Vector2(middle.X, middle.Z).Length() > 82f)
        {
            return false;
        }

        foreach (var other in _buildings)
        {
            if (Vector3.Distance(middle, other.Middle) < (plan.Reach + other.Plan.Reach) * 0.92f + 0.6f)
            {
                return false;
            }

            // Each building's box is its baked chunk, and a part or lamp in two boxes goes to the first one's bake.
            if (Overlap(plan, placement, other.Plan, other.Placement))
            {
                return false;
            }
        }

        Matrix4x4.Invert(placement, out var inverse);
        float margin = PathHalfWidth + 0.4f;
        foreach (var point in pathPoints)
        {
            var local = Vector3.Transform(point, inverse);
            if (local.X > plan.Min.X - margin && local.X < plan.Max.X + margin && local.Z > plan.Min.Z - margin && local.Z < plan.Max.Z + margin)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether two buildings' boxes overlap across the ground. Two rectangles are apart when
    /// some side of one has the whole of the other beyond it, so each one's two axes are tried.
    /// </summary>
    private static bool Overlap(BuildingPlan a, Matrix4x4 placementA, BuildingPlan b, Matrix4x4 placementB)
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

    /// <summary>Points every metre along every path, flat on the ground plane.</summary>
    private List<Vector3> PathPoints()
    {
        var points = new List<Vector3>();
        foreach (var path in _paths)
        {
            for (float s = 0f; s < path.Length; s += 1f)
            {
                points.Add(path.PointAtDistance(s));
            }
        }

        return points;
    }
}
