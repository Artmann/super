using System.Numerics;
using System.Text.Json.Serialization;

namespace Super;

/// <summary>Which way is up along a <see cref="Spline"/>, which decides how its frames turn.</summary>
public enum SplineUp
{
    /// <summary>
    /// Up leans towards the sky (+y), as on a road, a rail or a patrol route, so nothing
    /// tilts sideways unless a roll says so. A stretch going straight up or down has no sky
    /// to lean to, so use <see cref="Minimal"/> for loops.
    /// </summary>
    World,

    /// <summary>
    /// Up turns as little as it can from one moment to the next, so it never flips, even
    /// through a loop or a corkscrew. On a closed spline any twist is spread evenly round it.
    /// </summary>
    Minimal,
}

/// <summary>
/// A point a Bézier <see cref="Spline"/> passes through, with handles that pull the curve.
/// <see cref="In"/> and <see cref="Out"/> are offsets from the position: the curve arrives
/// from the way <see cref="In"/> points and leaves the way <see cref="Out"/> points, and
/// longer handles pull harder. Roll is in radians; a positive roll tips the top to the right.
/// </summary>
public readonly record struct SplineKnot(Vector3 Position, Vector3 In = default, Vector3 Out = default, float Roll = 0f)
{
    /// <summary>A knot the curve passes smoothly through, heading along <paramref name="tangent"/>.</summary>
    public static SplineKnot Smooth(Vector3 position, Vector3 tangent, float roll = 0f) => new(position, -tangent, tangent, roll);
}

/// <summary>
/// A place on a spline and which way it faces there: +z along the spline, +y up and +x to
/// the left, the same as a model facing +z. <see cref="Matrix"/> places a model there.
/// </summary>
public readonly record struct SplinePose(Vector3 Position, Quaternion Rotation)
{
    // The axes are read from the rotation matrix's rows, which is what Vector3.Transform of a unit
    // axis gives. Calling Vector3.Transform here would make Roe tie this type to Super's Transform.

    /// <summary>The way along the spline.</summary>
    public Vector3 Forward
    {
        get
        {
            var turn = Matrix4x4.CreateFromQuaternion(Rotation);
            return new Vector3(turn.M31, turn.M32, turn.M33);
        }
    }

    /// <summary>Up, tipped by any roll.</summary>
    public Vector3 Up
    {
        get
        {
            var turn = Matrix4x4.CreateFromQuaternion(Rotation);
            return new Vector3(turn.M21, turn.M22, turn.M23);
        }
    }

    /// <summary>To the right, looking along the spline.</summary>
    public Vector3 Right
    {
        get
        {
            var turn = Matrix4x4.CreateFromQuaternion(Rotation);
            return new Vector3(-turn.M11, -turn.M12, -turn.M13);
        }
    }

    /// <summary>The pose as a matrix, to draw a model there.</summary>
    public Matrix4x4 Matrix => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>A pose part way between two others, for drawing between ticks.</summary>
    public static SplinePose Lerp(in SplinePose from, in SplinePose to, float amount) => new(
        Vector3.Lerp(from.Position, to.Position, amount),
        Quaternion.Slerp(from.Rotation, to.Rotation, amount));
}

/// <summary>
/// The place on a spline nearest a point: its <see cref="T"/>, how far along the spline it
/// is in metres, where it is, and the <see cref="Gap"/> from the point to it.
/// </summary>
public readonly record struct SplineHit(float T, float Distance, Vector3 Position, float Gap);

/// <summary>
/// A smooth curve through points, for camera moves, rails, roads, rivers and patrol
/// routes. Make one through points with <see cref="Through"/>, or from knots with handles
/// with <see cref="Bezier"/>. Read it by <c>t</c>, from 0 at the start to 1 at the end,
/// or by distance in metres, which is even: the same distance is the same length of curve
/// wherever it is. A spline doesn't change once made, so one can be shared freely.
/// </summary>
public sealed class Spline
{
    private readonly SplineGeometry _geometry;

    /// <summary>For JSON, which writes a spline as either <c>points</c> or <c>knots</c>.</summary>
    [JsonConstructor]
    internal Spline(IReadOnlyList<Vector3>? points = null, IReadOnlyList<SplineKnot>? knots = null, bool closed = false, IReadOnlyList<float>? roll = null, SplineUp up = SplineUp.World)
    {
        SplineChecks.Source(points, knots);
        Points = points is null ? null : [.. points];
        Knots = knots is null ? null : [.. knots];
        Roll = roll is null ? null : [.. roll];
        Closed = closed;
        Up = up;

        if (Points is not null)
        {
            SplineChecks.Positions(Points, "point", closed);
            SplineChecks.Rolls(Points.Count, Roll);
            float[] rolls = Roll is null ? new float[Points.Count] : [.. Roll];
            _geometry = new SplineGeometry(SplineCurve.FromPoints(Points, closed), closed, rolls, up);
        }
        else
        {
            SplineChecks.Knots(Knots!, roll, closed);
            _geometry = new SplineGeometry(SplineCurve.FromKnots(Knots!, closed), closed, [.. Knots!.Select(knot => knot.Roll)], up);
        }
    }

    /// <summary>The points a Catmull-Rom spline goes through, or none if it's made of knots.</summary>
    public IReadOnlyList<Vector3>? Points { get; }

    /// <summary>The knots of a Bézier spline, or none if it goes through points.</summary>
    public IReadOnlyList<SplineKnot>? Knots { get; }

    /// <summary>Whether the end joins back to the start, making a loop.</summary>
    public bool Closed { get; }

    /// <summary>The roll at each point, in radians, if any were given.</summary>
    public IReadOnlyList<float>? Roll { get; }

    /// <summary>Which way is up along it.</summary>
    public SplineUp Up { get; }

    /// <summary>How long it is, in metres.</summary>
    [JsonIgnore]
    public float Length => _geometry.Length;

    /// <summary>How many pieces it has: one between each point and the next.</summary>
    [JsonIgnore]
    public int Segments => _geometry.Segments;

    /// <summary>
    /// A smooth curve through every point, as a centripetal Catmull-Rom spline, which stays
    /// close to its points: it doesn't overshoot sharp turns or tie itself in loops. A
    /// closed spline joins its last point back to its first. Roll, in radians, tips the top
    /// to the right at each point, so give one per point.
    /// </summary>
    public static Spline Through(IReadOnlyList<Vector3> points, bool closed = false, IReadOnlyList<float>? roll = null, SplineUp up = SplineUp.World)
    {
        ArgumentNullException.ThrowIfNull(points);
        return new Spline(points, null, closed, roll, up);
    }

    /// <summary>
    /// A curve through knots whose handles shape it, as in a drawing program. A closed spline
    /// joins its last knot back to its first.
    /// </summary>
    public static Spline Bezier(IReadOnlyList<SplineKnot> knots, bool closed = false, SplineUp up = SplineUp.World)
    {
        ArgumentNullException.ThrowIfNull(knots);
        return new Spline(null, knots, closed, null, up);
    }

    /// <summary>Where the spline is at <paramref name="t"/>. Each piece takes an equal share of t, whatever its length.</summary>
    public Vector3 Position(float t) => _geometry.Position(t);

    /// <summary>The way the spline heads at <paramref name="t"/>, one unit long.</summary>
    public Vector3 Tangent(float t) => _geometry.Tangent(t);

    /// <summary>Where the spline is at <paramref name="t"/>, and which way it faces there.</summary>
    public SplinePose Frame(float t) => _geometry.Frame(t);

    /// <summary>How far along the spline <paramref name="t"/> is, in metres.</summary>
    public float DistanceAt(float t) => _geometry.DistanceAt(t);

    /// <summary>
    /// The t that lies <paramref name="distance"/> metres along the spline. Distances past
    /// the ends stop there on an open spline and go round again on a closed one.
    /// </summary>
    public float TAt(float distance) => _geometry.TAt(distance);

    /// <summary>Where the spline is <paramref name="distance"/> metres along it.</summary>
    public Vector3 PointAtDistance(float distance) => Position(TAt(distance));

    /// <summary>Where the spline is <paramref name="distance"/> metres along it, and which way it faces there.</summary>
    public SplinePose FrameAtDistance(float distance) => Frame(TAt(distance));

    /// <summary>The place on the spline nearest <paramref name="point"/>.</summary>
    public SplineHit Nearest(Vector3 point) => _geometry.Nearest(point);
}

/// <summary>Things made from a <see cref="Spline"/> through its public reading calls.</summary>
public static class SplineExtensions
{
    /// <summary>
    /// Places along the spline every <paramref name="spacing"/> metres, starting
    /// <paramref name="start"/> metres in, for fence posts, lamps or trees. An open spline
    /// stops at its end. A closed one spaces them evenly all the way round, nudging the
    /// spacing so the last meets the first.
    /// </summary>
    public static SplinePose[] Scatter(this Spline spline, float spacing, float start = 0f)
    {
        ArgumentNullException.ThrowIfNull(spline);
        if (!(spacing > 0f) || !float.IsFinite(spacing))
        {
            throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "The spacing must be above zero.");
        }

        if (!(start >= 0f) || !float.IsFinite(start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start can't be below zero.");
        }

        float length = spline.Length;
        if (spline.Closed)
        {
            int count = Math.Max(1, (int)MathF.Round(length / spacing));
            var round = new SplinePose[count];
            for (int i = 0; i < count; i++)
            {
                round[i] = spline.FrameAtDistance(start + length * i / count);
            }

            return round;
        }

        var poses = new List<SplinePose>();
        for (int i = 0; ; i++)
        {
            float distance = start + i * spacing;
            if (distance > length + 1e-4f)
            {
                break;
            }

            poses.Add(spline.FrameAtDistance(distance));
        }

        return [.. poses];
    }
}
