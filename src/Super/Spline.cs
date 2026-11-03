using System.Globalization;
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
    /// <summary>The way along the spline.</summary>
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);

    /// <summary>Up, tipped by any roll.</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>To the right, looking along the spline.</summary>
    public Vector3 Right => Vector3.Transform(-Vector3.UnitX, Rotation);

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
    /// <summary>How finely each piece is measured, for distances and for frames.</summary>
    private const int Steps = 16;

    private static readonly float GaussNode = MathF.Sqrt(0.6f);

    // Four Bézier control points for each piece.
    private readonly Vector3[] _controls;
    private readonly float[] _rolls;
    private readonly float[] _distances;
    private readonly Vector3[] _samples;
    private readonly Vector3[] _ups;

    /// <summary>For JSON, which writes a spline as either <c>points</c> or <c>knots</c>.</summary>
    [JsonConstructor]
    internal Spline(IReadOnlyList<Vector3>? points = null, IReadOnlyList<SplineKnot>? knots = null, bool closed = false, IReadOnlyList<float>? roll = null, SplineUp up = SplineUp.World)
    {
        if (points is null && knots is null)
        {
            throw new ArgumentException("A spline needs points or knots.");
        }

        if (points is not null && knots is not null)
        {
            throw new ArgumentException("A spline takes points or knots, not both.");
        }

        Points = points is null ? null : [.. points];
        Knots = knots is null ? null : [.. knots];
        Roll = roll is null ? null : [.. roll];
        Closed = closed;
        Up = up;

        if (Points is not null)
        {
            CheckPositions(Points, "point");
            CheckRolls(Points.Count, Roll);
            _controls = CatmullRom(Points, closed);
            _rolls = Roll is null ? new float[Points.Count] : [.. Roll];
        }
        else
        {
            if (roll is not null)
            {
                throw new ArgumentException("Knots carry their own roll, so a spline made of knots takes no roll list.");
            }

            CheckKnots(Knots!);
            _controls = BezierControls(Knots!, closed);
            _rolls = [.. Knots!.Select(knot => knot.Roll)];
        }

        int samples = Segments * Steps;
        _distances = new float[samples + 1];
        _samples = new Vector3[samples + 1];
        for (int k = 0; k <= samples; k++)
        {
            var (segment, amount) = Sample(k);
            _samples[k] = Point(segment, amount);
            if (k > 0)
            {
                // Measure the step that ends here, which belongs to the piece before when k starts a new one.
                var (before, from) = Sample(k - 1);
                _distances[k] = _distances[k - 1] + Arc(before, from, from + 1f / Steps);
            }
        }

        Length = _distances[samples];
        _ups = MinimalUps();
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
    public float Length { get; }

    /// <summary>How many pieces it has: one between each point and the next.</summary>
    [JsonIgnore]
    public int Segments => _controls.Length / 4;

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
    public Vector3 Position(float t)
    {
        var (segment, amount) = Locate(t);
        return Point(segment, amount);
    }

    /// <summary>The way the spline heads at <paramref name="t"/>, one unit long.</summary>
    public Vector3 Tangent(float t)
    {
        var (segment, amount) = Locate(t);
        return Heading(segment, amount);
    }

    /// <summary>Where the spline is at <paramref name="t"/>, and which way it faces there.</summary>
    public SplinePose Frame(float t)
    {
        var (segment, amount) = Locate(t);
        var forward = Heading(segment, amount);
        var up = UpAt(segment, amount, forward);
        float roll = RollAt(segment, amount);
        if (roll != 0f)
        {
            up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, roll));
        }

        return new SplinePose(Point(segment, amount), Facing(forward, up));
    }

    /// <summary>How far along the spline <paramref name="t"/> is, in metres.</summary>
    public float DistanceAt(float t)
    {
        var (segment, amount) = Locate(t);
        int k = Math.Min(segment * Steps + (int)(amount * Steps), segment * Steps + Steps - 1);
        float from = (k - segment * Steps) / (float)Steps;
        return _distances[k] + Arc(segment, from, amount);
    }

    /// <summary>
    /// The t that lies <paramref name="distance"/> metres along the spline. Distances past
    /// the ends stop there on an open spline and go round again on a closed one.
    /// </summary>
    public float TAt(float distance)
    {
        distance = WrapDistance(distance);
        int k = Array.BinarySearch(_distances, distance);
        k = k >= 0 ? k : ~k - 1;
        k = Math.Clamp(k, 0, _distances.Length - 2);
        int segment = k / Steps;
        float from = (k - segment * Steps) / (float)Steps;
        float to = from + 1f / Steps;
        float span = _distances[k + 1] - _distances[k];
        float amount = span > 0f ? from + (distance - _distances[k]) / span * (to - from) : from;

        // A few Newton steps take the guess from the measured line onto the curve itself.
        for (int i = 0; i < 3; i++)
        {
            float speed = Derivative(segment, amount).Length();
            if (speed < 1e-6f)
            {
                break;
            }

            float error = _distances[k] + Arc(segment, from, amount) - distance;
            amount = Math.Clamp(amount - error / speed, from, to);
        }

        return (segment + amount) / Segments;
    }

    /// <summary>Where the spline is <paramref name="distance"/> metres along it.</summary>
    public Vector3 PointAtDistance(float distance) => Position(TAt(distance));

    /// <summary>Where the spline is <paramref name="distance"/> metres along it, and which way it faces there.</summary>
    public SplinePose FrameAtDistance(float distance) => Frame(TAt(distance));

    /// <summary>The place on the spline nearest <paramref name="point"/>.</summary>
    public SplineHit Nearest(Vector3 point)
    {
        int best = 0;
        float bestGap = float.MaxValue;
        for (int k = 0; k < _samples.Length; k++)
        {
            float gap = Vector3.DistanceSquared(_samples[k], point);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = k;
            }
        }

        // The nearest sample brackets the answer to within a sample either side; narrow it down.
        float count = Segments * Steps;
        float low = (best - 1) / count;
        float high = (best + 1) / count;
        if (!Closed)
        {
            low = Math.Max(low, 0f);
            high = Math.Min(high, 1f);
        }

        const float golden = 0.618034f;
        float a = high - golden * (high - low);
        float b = low + golden * (high - low);
        float gapA = Vector3.DistanceSquared(Position(a), point);
        float gapB = Vector3.DistanceSquared(Position(b), point);
        for (int i = 0; i < 40; i++)
        {
            if (gapA < gapB)
            {
                high = b;
                b = a;
                gapB = gapA;
                a = high - golden * (high - low);
                gapA = Vector3.DistanceSquared(Position(a), point);
            }
            else
            {
                low = a;
                a = b;
                gapA = gapB;
                b = low + golden * (high - low);
                gapB = Vector3.DistanceSquared(Position(b), point);
            }
        }

        // Squared gaps stop telling close places apart when the point is far off, so finish with
        // Newton steps on where the line to the point stands square to the curve.
        var (segment, u) = Locate(WrapT((low + high) * 0.5f));
        for (int i = 0; i < 4; i++)
        {
            var offset = Point(segment, u) - point;
            var heading = Derivative(segment, u);
            float slope = Vector3.Dot(heading, heading) + Vector3.Dot(offset, SecondDerivative(segment, u));
            if (slope <= 1e-9f)
            {
                break;
            }

            u = Math.Clamp(u - Vector3.Dot(offset, heading) / slope, 0f, 1f);
        }

        float t = WrapT((segment + u) / Segments);
        var position = Point(segment, u);
        return new SplineHit(t, DistanceAt(t), position, Vector3.Distance(position, point));
    }

    /// <summary>
    /// Places along the spline every <paramref name="spacing"/> metres, starting
    /// <paramref name="start"/> metres in, for fence posts, lamps or trees. An open spline
    /// stops at its end. A closed one spaces them evenly all the way round, nudging the
    /// spacing so the last meets the first.
    /// </summary>
    public SplinePose[] Scatter(float spacing, float start = 0f)
    {
        if (!(spacing > 0f) || !float.IsFinite(spacing))
        {
            throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "The spacing must be above zero.");
        }

        if (!(start >= 0f) || !float.IsFinite(start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start can't be below zero.");
        }

        if (Closed)
        {
            int count = Math.Max(1, (int)MathF.Round(Length / spacing));
            var round = new SplinePose[count];
            for (int i = 0; i < count; i++)
            {
                round[i] = FrameAtDistance(start + Length * i / count);
            }

            return round;
        }

        var poses = new List<SplinePose>();
        for (int i = 0; ; i++)
        {
            float distance = start + i * spacing;
            if (distance > Length + 1e-4f)
            {
                break;
            }

            poses.Add(FrameAtDistance(distance));
        }

        return [.. poses];
    }

    /// <summary>A rotation whose +z is <paramref name="forward"/> and +y is <paramref name="up"/>, which must be square to it.</summary>
    internal static Quaternion Facing(Vector3 forward, Vector3 up)
    {
        var side = Vector3.Cross(up, forward);
        var rotation = new Matrix4x4(
            side.X, side.Y, side.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    private float WrapT(float t) => Closed ? t - MathF.Floor(t) : Math.Clamp(t, 0f, 1f);

    private float WrapDistance(float distance)
    {
        if (!Closed)
        {
            return Math.Clamp(distance, 0f, Length);
        }

        float wrapped = distance - MathF.Floor(distance / Length) * Length;
        return wrapped >= Length ? 0f : wrapped;
    }

    private (int Segment, float Amount) Locate(float t)
    {
        if (!float.IsFinite(t))
        {
            throw new ArgumentOutOfRangeException(nameof(t), t, "t must be a finite number.");
        }

        float scaled = WrapT(t) * Segments;
        int segment = Math.Min((int)scaled, Segments - 1);
        return (segment, scaled - segment);
    }

    /// <summary>The piece and amount of a measuring sample.</summary>
    private (int Segment, float Amount) Sample(int k)
    {
        int segment = Math.Min(k / Steps, Segments - 1);
        return (segment, (k - segment * Steps) / (float)Steps);
    }

    private Vector3 Point(int segment, float u)
    {
        var a = _controls[segment * 4];
        var b = _controls[segment * 4 + 1];
        var c = _controls[segment * 4 + 2];
        var d = _controls[segment * 4 + 3];
        float v = 1f - u;
        return v * v * v * a + 3f * v * v * u * b + 3f * v * u * u * c + u * u * u * d;
    }

    private Vector3 Derivative(int segment, float u)
    {
        var a = _controls[segment * 4];
        var b = _controls[segment * 4 + 1];
        var c = _controls[segment * 4 + 2];
        var d = _controls[segment * 4 + 3];
        float v = 1f - u;
        return 3f * v * v * (b - a) + 6f * v * u * (c - b) + 3f * u * u * (d - c);
    }

    private Vector3 SecondDerivative(int segment, float u)
    {
        var a = _controls[segment * 4];
        var b = _controls[segment * 4 + 1];
        var c = _controls[segment * 4 + 2];
        var d = _controls[segment * 4 + 3];
        return 6f * (1f - u) * (c - 2f * b + a) + 6f * u * (d - 2f * c + b);
    }

    private Vector3 Heading(int segment, float u)
    {
        var heading = Derivative(segment, u);
        if (heading.LengthSquared() < 1e-12f)
        {
            // A handle of no length stops the curve dead at its knot; look either side instead.
            heading = Point(segment, Math.Min(u + 1e-3f, 1f)) - Point(segment, Math.Max(u - 1e-3f, 0f));
        }

        if (heading.LengthSquared() < 1e-12f)
        {
            heading = _controls[segment * 4 + 3] - _controls[segment * 4];
        }

        return Vector3.Normalize(heading);
    }

    /// <summary>The length of a piece between two amounts, by three-point Gauss–Legendre.</summary>
    private float Arc(int segment, float from, float to)
    {
        float half = (to - from) * 0.5f;
        float middle = (to + from) * 0.5f;
        return half * (
            5f / 9f * Derivative(segment, middle - half * GaussNode).Length()
            + 8f / 9f * Derivative(segment, middle).Length()
            + 5f / 9f * Derivative(segment, middle + half * GaussNode).Length());
    }

    private float RollAt(int segment, float u)
    {
        float from = _rolls[segment];
        float to = _rolls[(segment + 1) % _rolls.Length];
        return float.Lerp(from, to, u * u * (3f - 2f * u));
    }

    private Vector3 UpAt(int segment, float u, Vector3 forward)
    {
        if (Up == SplineUp.World)
        {
            var upright = Vector3.UnitY - forward * forward.Y;
            if (upright.LengthSquared() > 1e-6f)
            {
                return Vector3.Normalize(upright);
            }
        }

        float place = (segment + u) * Steps;
        int k = Math.Min((int)place, _ups.Length - 2);
        var blended = Vector3.Lerp(_ups[k], _ups[k + 1], place - k);
        var square = blended - forward * Vector3.Dot(blended, forward);
        if (square.LengthSquared() < 1e-10f)
        {
            square = _ups[k] - forward * Vector3.Dot(_ups[k], forward);
        }

        return Vector3.Normalize(square);
    }

    /// <summary>
    /// The up direction at every sample, carried along by double reflection, which turns it
    /// as little as the curve allows. A closed spline spreads any twist left over evenly.
    /// </summary>
    private Vector3[] MinimalUps()
    {
        var ups = new Vector3[_samples.Length];
        var tangents = new Vector3[_samples.Length];
        for (int k = 0; k < _samples.Length; k++)
        {
            var (segment, amount) = Sample(k);
            tangents[k] = Heading(segment, amount);
        }

        var first = Vector3.UnitY - tangents[0] * tangents[0].Y;
        if (first.LengthSquared() < 1e-6f)
        {
            first = Vector3.UnitZ - tangents[0] * tangents[0].Z;
        }

        ups[0] = Vector3.Normalize(first);
        for (int k = 0; k < _samples.Length - 1; k++)
        {
            var step = _samples[k + 1] - _samples[k];
            float c1 = Vector3.Dot(step, step);
            var up = ups[k];
            var tangent = tangents[k];
            if (c1 > 1e-12f)
            {
                up -= 2f / c1 * Vector3.Dot(step, up) * step;
                tangent -= 2f / c1 * Vector3.Dot(step, tangent) * step;
            }

            var turn = tangents[k + 1] - tangent;
            float c2 = Vector3.Dot(turn, turn);
            if (c2 > 1e-12f)
            {
                up -= 2f / c2 * Vector3.Dot(turn, up) * turn;
            }

            up -= tangents[k + 1] * Vector3.Dot(up, tangents[k + 1]);
            ups[k + 1] = up.LengthSquared() > 1e-12f ? Vector3.Normalize(up) : ups[k];
        }

        if (Closed && Length > 0f)
        {
            var axis = tangents[0];
            var last = ups[^1];
            float twist = MathF.Atan2(Vector3.Dot(Vector3.Cross(last, ups[0]), axis), Vector3.Dot(last, ups[0]));
            for (int k = 1; k < ups.Length; k++)
            {
                var turned = Vector3.Transform(ups[k], Quaternion.CreateFromAxisAngle(tangents[k], twist * _distances[k] / Length));
                ups[k] = Vector3.Normalize(turned - tangents[k] * Vector3.Dot(turned, tangents[k]));
            }
        }

        return ups;
    }

    private static Vector3[] CatmullRom(IReadOnlyList<Vector3> points, bool closed)
    {
        int count = points.Count;
        int segments = closed ? count : count - 1;
        var controls = new Vector3[segments * 4];
        for (int i = 0; i < segments; i++)
        {
            var p1 = points[i];
            var p2 = points[(i + 1) % count];
            var p0 = closed ? points[(i - 1 + count) % count] : i > 0 ? points[i - 1] : 2f * p1 - p2;
            var p3 = closed ? points[(i + 2) % count] : i + 2 < count ? points[i + 2] : 2f * p2 - p1;

            // Centripetal: each step is timed by the square root of its length (Barry and Goldman).
            float d1 = MathF.Sqrt(Vector3.Distance(p0, p1));
            float d2 = MathF.Sqrt(Vector3.Distance(p1, p2));
            float d3 = MathF.Sqrt(Vector3.Distance(p2, p3));
            var leaving = d2 * ((p1 - p0) / d1 - (p2 - p0) / (d1 + d2) + (p2 - p1) / d2);
            var arriving = d2 * ((p2 - p1) / d2 - (p3 - p1) / (d2 + d3) + (p3 - p2) / d3);
            controls[i * 4] = p1;
            controls[i * 4 + 1] = p1 + leaving / 3f;
            controls[i * 4 + 2] = p2 - arriving / 3f;
            controls[i * 4 + 3] = p2;
        }

        return controls;
    }

    private static Vector3[] BezierControls(IReadOnlyList<SplineKnot> knots, bool closed)
    {
        int count = knots.Count;
        int segments = closed ? count : count - 1;
        var controls = new Vector3[segments * 4];
        for (int i = 0; i < segments; i++)
        {
            var from = knots[i];
            var to = knots[(i + 1) % count];
            controls[i * 4] = from.Position;
            controls[i * 4 + 1] = from.Position + from.Out;
            controls[i * 4 + 2] = to.Position + to.In;
            controls[i * 4 + 3] = to.Position;
        }

        return controls;
    }

    private void CheckPositions(IReadOnlyList<Vector3> positions, string word)
    {
        int least = Closed ? 3 : 2;
        if (positions.Count < least)
        {
            throw new ArgumentException(Closed
                ? $"A closed spline needs at least three {word}s."
                : $"A spline needs at least two {word}s.");
        }

        for (int i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            {
                throw new ArgumentException($"Spline {word} {i} must be finite.");
            }

            if (i > 0 && position == positions[i - 1])
            {
                throw new ArgumentException($"Spline {word}s {i - 1} and {i} are in the same place.");
            }
        }

        if (Closed && positions[^1] == positions[0])
        {
            throw new ArgumentException($"The last spline {word} is where the first is. A closed spline joins its ends itself, so leave the last one out.");
        }
    }

    private void CheckKnots(IReadOnlyList<SplineKnot> knots)
    {
        CheckPositions([.. knots.Select(knot => knot.Position)], "knot");
        for (int i = 0; i < knots.Count; i++)
        {
            var knot = knots[i];
            if (!float.IsFinite(knot.In.X) || !float.IsFinite(knot.In.Y) || !float.IsFinite(knot.In.Z)
                || !float.IsFinite(knot.Out.X) || !float.IsFinite(knot.Out.Y) || !float.IsFinite(knot.Out.Z)
                || !float.IsFinite(knot.Roll))
            {
                throw new ArgumentException($"Spline knot {i} must have finite handles and roll.");
            }
        }
    }

    private static void CheckRolls(int points, IReadOnlyList<float>? roll)
    {
        if (roll is null)
        {
            return;
        }

        if (roll.Count != points)
        {
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"A spline through {points} points needs one roll for each, but has {roll.Count}."));
        }

        for (int i = 0; i < roll.Count; i++)
        {
            if (!float.IsFinite(roll[i]))
            {
                throw new ArgumentException($"Spline roll {i} must be finite.");
            }
        }
    }
}
