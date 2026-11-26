using System.Numerics;

namespace Super;

/// <summary>
/// Lines, shapes and labels in the world for seeing what a game thinks: where an enemy is heading, what a ray hit,
/// how big a trigger is. <see cref="View.Debug"/> draws them for one frame, and <see cref="DebugDraw"/> keeps them
/// across frames. A shape in a hidden channel costs only the check of its channel. See docs/debugging.md.
/// </summary>
public abstract class DebugCanvas
{
    /// <summary>How many straight pieces make a circle.</summary>
    public const int CircleSegments = 32;

    private static readonly Vector2[] CirclePoints = MakeCirclePoints();

    // So only DebugDraw and ViewDebug are canvases.
    private protected DebugCanvas()
    {
    }

    /// <summary>A line from one point to another.</summary>
    public void Line(Vector3 from, Vector3 to, Color color, DebugLook look = default)
    {
        if (Begin(look))
        {
            Add(new DebugLine(from, to, color, look));
        }
    }

    /// <summary>A line from a point along a direction, as long as the direction.</summary>
    public void Ray(Vector3 origin, Vector3 direction, Color color, DebugLook look = default) =>
        Line(origin, origin + direction, color, look);

    /// <summary>A line with a head at <paramref name="to"/>, a quarter of the line long.</summary>
    public void Arrow(Vector3 from, Vector3 to, Color color, DebugLook look = default)
    {
        if (!Begin(look))
        {
            return;
        }

        Add(new DebugLine(from, to, color, look));
        var along = to - from;
        float length = along.Length();
        if (length <= 0f)
        {
            return;
        }

        var back = along / length * (length * 0.25f);
        var side = Perpendicular(along / length) * (length * 0.125f);
        Add(new DebugLine(to, to - back + side, color, look));
        Add(new DebugLine(to, to - back - side, color, look));
    }

    /// <summary>Lines joining the points in order, closed back to the first when <paramref name="closed"/> is set.</summary>
    public void Path(ReadOnlySpan<Vector3> points, Color color, bool closed = false, DebugLook look = default)
    {
        if (!Begin(look))
        {
            return;
        }

        for (int i = 1; i < points.Length; i++)
        {
            Add(new DebugLine(points[i - 1], points[i], color, look));
        }

        if (closed && points.Length > 2)
        {
            Add(new DebugLine(points[^1], points[0], color, look));
        }
    }

    /// <summary>The twelve edges of a box lined up with the axes, from its centre and its size along each.</summary>
    public void Box(Vector3 center, Vector3 size, Color color, DebugLook look = default) =>
        Box(Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(center), color, look);

    /// <summary>
    /// The twelve edges of a box turned and stretched by a transform: the cube one unit across, centred on zero, moved
    /// by it. A model's <see cref="Transform"/> matrix shows its unit cube.
    /// </summary>
    public void Box(in Matrix4x4 transform, Color color, DebugLook look = default)
    {
        if (!Begin(look))
        {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var unit = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
            corners[i] = Vector3.Transform(unit, transform);
        }

        // Each corner joins the corners one bit away from it.
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    Add(new DebugLine(corners[i], corners[i | bit], color, look));
                }
            }
        }
    }

    /// <summary>A circle around a centre, in the plane facing <paramref name="normal"/>, in <see cref="CircleSegments"/> pieces.</summary>
    public void Circle(Vector3 center, Vector3 normal, float radius, Color color, DebugLook look = default)
    {
        if (Begin(look))
        {
            var (first, second) = Plane(normal);
            Arc(center, (first * radius, second * radius), CircleSegments, color, look);
        }
    }

    /// <summary>A ball as three circles, one around each axis.</summary>
    public void Sphere(Vector3 center, float radius, Color color, DebugLook look = default)
    {
        if (!Begin(look))
        {
            return;
        }

        Arc(center, (Vector3.UnitX * radius, Vector3.UnitY * radius), CircleSegments, color, look);
        Arc(center, (Vector3.UnitY * radius, Vector3.UnitZ * radius), CircleSegments, color, look);
        Arc(center, (Vector3.UnitZ * radius, Vector3.UnitX * radius), CircleSegments, color, look);
    }

    /// <summary>
    /// A capsule between the centres of its two ends: a circle around each end, four lines along its sides, and the
    /// two half circles that cap each end.
    /// </summary>
    public void Capsule(Vector3 from, Vector3 to, float radius, Color color, DebugLook look = default)
    {
        if (!Begin(look))
        {
            return;
        }

        var along = to - from;
        var axis = along.LengthSquared() > 0f ? Vector3.Normalize(along) : Vector3.UnitY;
        var (first, second) = Plane(axis);
        Arc(from, (first * radius, second * radius), CircleSegments, color, look);
        Arc(to, (first * radius, second * radius), CircleSegments, color, look);
        foreach (var side in (ReadOnlySpan<Vector3>)[first, -first, second, -second])
        {
            Add(new DebugLine(from + side * radius, to + side * radius, color, look));
        }

        // Half circles from one side over the end to the other: away from the middle at each end.
        Arc(to, (first * radius, axis * radius), CircleSegments / 2, color, look);
        Arc(to, (second * radius, axis * radius), CircleSegments / 2, color, look);
        Arc(from, (first * radius, -axis * radius), CircleSegments / 2, color, look);
        Arc(from, (second * radius, -axis * radius), CircleSegments / 2, color, look);
    }

    /// <summary>
    /// The three axes of a transform from its origin, <paramref name="size"/> long before the transform's own scale:
    /// x in red, y in green and z in blue.
    /// </summary>
    public void Axes(in Matrix4x4 transform, float size = 1f, DebugLook look = default)
    {
        if (!Begin(look))
        {
            return;
        }

        var origin = transform.Translation;
        Add(new DebugLine(origin, Vector3.Transform(Vector3.UnitX * size, transform), Color.Red, look));
        Add(new DebugLine(origin, Vector3.Transform(Vector3.UnitY * size, transform), Color.Green, look));
        Add(new DebugLine(origin, Vector3.Transform(Vector3.UnitZ * size, transform), Color.Blue, look));
    }

    /// <summary>
    /// Text over a place in the world, the same size however far away it is, on a dark backing so it reads over
    /// anything. It's left out while the place is behind the camera. Only the look's channel is used.
    /// </summary>
    public void Text(Vector3 position, string text, Color color, DebugLook look = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Showing(look.Channel))
        {
            Add(new DebugLabel(position, text, color, look.Channel));
        }
    }

    /// <summary>Whether shapes in this channel are drawn. Shapes with no channel always are.</summary>
    private protected abstract bool Showing(string? channel);

    private protected abstract void Add(in DebugLine line);

    private protected abstract void Add(in DebugLabel label);

    /// <summary>Whether a shape with this look is drawn, after checking the look can be.</summary>
    private bool Begin(in DebugLook look)
    {
        if (!Showing(look.Channel))
        {
            return false;
        }

        look.Check();
        return true;
    }

    /// <summary>
    /// <paramref name="segments"/> pieces of a circle around a centre, starting at the first spoke and turning towards
    /// the second. <see cref="CircleSegments"/> pieces go all the way round.
    /// </summary>
    private void Arc(Vector3 center, (Vector3 First, Vector3 Second) spokes, int segments, Color color, in DebugLook look)
    {
        var previous = center + spokes.First;
        for (int i = 1; i <= segments; i++)
        {
            var point = CirclePoints[i % CircleSegments];
            var next = center + spokes.First * point.X + spokes.Second * point.Y;
            Add(new DebugLine(previous, next, color, look));
            previous = next;
        }
    }

    /// <summary>Two directions at right angles to each other and to <paramref name="normal"/>, each one unit long.</summary>
    private static (Vector3 First, Vector3 Second) Plane(Vector3 normal)
    {
        var facing = normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitY;
        var first = Perpendicular(facing);
        return (first, Vector3.Cross(facing, first));
    }

    /// <summary>A direction at right angles to a unit direction, one unit long: level when it can be.</summary>
    private static Vector3 Perpendicular(Vector3 direction)
    {
        var across = Vector3.Cross(direction, Vector3.UnitY);
        if (across.LengthSquared() < 1e-6f)
        {
            across = Vector3.Cross(direction, Vector3.UnitX);
        }

        return Vector3.Normalize(across);
    }

    private static Vector2[] MakeCirclePoints()
    {
        var points = new Vector2[CircleSegments];
        for (int i = 0; i < CircleSegments; i++)
        {
            float angle = i * MathF.Tau / CircleSegments;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        return points;
    }
}
