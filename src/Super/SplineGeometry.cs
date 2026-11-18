using System.Globalization;
using System.Numerics;

namespace Super;

/// <summary>
/// The measured shape behind a spline: its pieces, how far along each sample
/// is, and which way is up. It reads by t or by distance, and finds the nearest place.
/// </summary>
internal sealed class SplineGeometry
{
    private readonly SplineCurve _curve;
    private readonly SplineMeasure _measure;
    private readonly SplineFrames _frames;
    private readonly bool _closed;

    public SplineGeometry(SplineCurve curve, bool closed, float[] rolls, SplineUp up)
    {
        _curve = curve;
        _closed = closed;
        _measure = new SplineMeasure(curve);
        _frames = new SplineFrames(curve, _measure, closed, rolls, up);
    }

    public float Length => _measure.Length;

    public int Segments => _curve.Segments;

    public Vector3 Position(float t)
    {
        var (segment, amount) = Locate(t);
        return _curve.Point(segment, amount);
    }

    public Vector3 Tangent(float t)
    {
        var (segment, amount) = Locate(t);
        return _curve.Heading(segment, amount);
    }

    public SplinePose Frame(float t)
    {
        var (segment, amount) = Locate(t);
        return _frames.PoseAt(segment, amount);
    }

    public float DistanceAt(float t)
    {
        var (segment, amount) = Locate(t);
        return _measure.DistanceAt(segment, amount);
    }

    public float TAt(float distance) => _measure.TAt(WrapDistance(distance));

    public SplineHit Nearest(Vector3 point)
    {
        // The nearest sample brackets the answer to within a sample either side; narrow it down.
        int best = _measure.NearestSample(point);
        float count = Segments * SplineMeasure.Steps;
        float low = (best - 1) / count;
        float high = (best + 1) / count;
        if (!_closed)
        {
            low = Math.Max(low, 0f);
            high = Math.Min(high, 1f);
        }

        float near = GoldenSearch(low, high, point);

        // Squared gaps stop telling close places apart when the point is far off, so finish with
        // Newton steps on where the line to the point stands square to the curve.
        var (segment, u) = Locate(WrapT(near));
        u = _curve.NewtonSteps(segment, u, point);

        float t = WrapT((segment + u) / Segments);
        var position = _curve.Point(segment, u);
        return new SplineHit(t, DistanceAt(t), position, Vector3.Distance(position, point));
    }

    /// <summary>The middle of the span left after narrowing low to high round the place nearest the point.</summary>
    private float GoldenSearch(float low, float high, Vector3 point)
    {
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

        return (low + high) * 0.5f;
    }

    private float WrapT(float t) => _closed ? t - MathF.Floor(t) : Math.Clamp(t, 0f, 1f);

    private float WrapDistance(float distance)
    {
        float length = Length;
        if (!_closed)
        {
            return Math.Clamp(distance, 0f, length);
        }

        float wrapped = distance - MathF.Floor(distance / length) * length;
        return wrapped >= length ? 0f : wrapped;
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
}

/// <summary>A spline's pieces as cubic Bézier curves, four control points each.</summary>
internal sealed class SplineCurve
{
    private static readonly float GaussNode = MathF.Sqrt(0.6f);

    private readonly Vector3[] _controls;

    private SplineCurve(Vector3[] controls)
    {
        _controls = controls;
    }

    /// <summary>How many pieces there are: one between each point and the next.</summary>
    public int Segments => _controls.Length / 4;

    /// <summary>A centripetal Catmull-Rom curve through the points, as Bézier pieces.</summary>
    public static SplineCurve FromPoints(IReadOnlyList<Vector3> points, bool closed)
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

        return new SplineCurve(controls);
    }

    /// <summary>The Bézier pieces between knots and their handles.</summary>
    public static SplineCurve FromKnots(IReadOnlyList<SplineKnot> knots, bool closed)
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

        return new SplineCurve(controls);
    }

    public Vector3 Point(int segment, float u)
    {
        var a = _controls[segment * 4];
        var b = _controls[segment * 4 + 1];
        var c = _controls[segment * 4 + 2];
        var d = _controls[segment * 4 + 3];
        float v = 1f - u;
        return v * v * v * a + 3f * v * v * u * b + 3f * v * u * u * c + u * u * u * d;
    }

    public Vector3 Derivative(int segment, float u)
    {
        var a = _controls[segment * 4];
        var b = _controls[segment * 4 + 1];
        var c = _controls[segment * 4 + 2];
        var d = _controls[segment * 4 + 3];
        float v = 1f - u;
        return 3f * v * v * (b - a) + 6f * v * u * (c - b) + 3f * u * u * (d - c);
    }

    /// <summary>The way the piece heads at <paramref name="u"/>, one unit long.</summary>
    public Vector3 Heading(int segment, float u)
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
    public float Arc(int segment, float from, float to)
    {
        float half = (to - from) * 0.5f;
        float middle = (to + from) * 0.5f;
        return half * (
            5f / 9f * Derivative(segment, middle - half * GaussNode).Length()
            + 8f / 9f * Derivative(segment, middle).Length()
            + 5f / 9f * Derivative(segment, middle + half * GaussNode).Length());
    }

    /// <summary>Moves <paramref name="u"/> to where the line to the point stands square to the piece.</summary>
    public float NewtonSteps(int segment, float u, Vector3 point)
    {
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

        return u;
    }

    private Vector3 SecondDerivative(int segment, float u)
    {
        var a = _controls[segment * 4];
        var b = _controls[segment * 4 + 1];
        var c = _controls[segment * 4 + 2];
        var d = _controls[segment * 4 + 3];
        return 6f * (1f - u) * (c - 2f * b + a) + 6f * u * (d - 2f * c + b);
    }
}

/// <summary>
/// Samples taken evenly in t along a curve, with how far along each one is, so the curve
/// reads by distance.
/// </summary>
internal sealed class SplineMeasure
{
    /// <summary>How finely each piece is measured, for distances and for frames.</summary>
    public const int Steps = 16;

    private readonly SplineCurve _curve;
    private readonly float[] _distances;

    public SplineMeasure(SplineCurve curve)
    {
        _curve = curve;
        int samples = curve.Segments * Steps;
        _distances = new float[samples + 1];
        SamplePoints = new Vector3[samples + 1];
        for (int k = 0; k <= samples; k++)
        {
            var (segment, amount) = Sample(k);
            SamplePoints[k] = curve.Point(segment, amount);
            if (k > 0)
            {
                // Measure the step that ends here, which belongs to the piece before when k starts a new one.
                var (before, from) = Sample(k - 1);
                _distances[k] = _distances[k - 1] + curve.Arc(before, from, from + 1f / Steps);
            }
        }

        Length = _distances[samples];
    }

    public float Length { get; }

    /// <summary>Where each sample is.</summary>
    public Vector3[] SamplePoints { get; }

    /// <summary>How far along sample <paramref name="k"/> is, in metres.</summary>
    public float DistanceTo(int k) => _distances[k];

    /// <summary>The piece and amount of a measuring sample.</summary>
    public (int Segment, float Amount) Sample(int k)
    {
        int segment = Math.Min(k / Steps, _curve.Segments - 1);
        return (segment, (k - segment * Steps) / (float)Steps);
    }

    public float DistanceAt(int segment, float amount)
    {
        int k = Math.Min(segment * Steps + (int)(amount * Steps), segment * Steps + Steps - 1);
        float from = (k - segment * Steps) / (float)Steps;
        return _distances[k] + _curve.Arc(segment, from, amount);
    }

    /// <summary>The t that lies this many metres along, which must be on the curve.</summary>
    public float TAt(float distance)
    {
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
            float speed = _curve.Derivative(segment, amount).Length();
            if (speed < 1e-6f)
            {
                break;
            }

            float error = _distances[k] + _curve.Arc(segment, from, amount) - distance;
            amount = Math.Clamp(amount - error / speed, from, to);
        }

        return (segment + amount) / _curve.Segments;
    }

    public int NearestSample(Vector3 point)
    {
        int best = 0;
        float bestGap = float.MaxValue;
        for (int k = 0; k < SamplePoints.Length; k++)
        {
            float gap = Vector3.DistanceSquared(SamplePoints[k], point);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = k;
            }
        }

        return best;
    }
}

/// <summary>Which way a spline faces along its length: up, as the spline's <see cref="SplineUp"/> says, and any roll.</summary>
internal sealed class SplineFrames
{
    private readonly SplineCurve _curve;
    private readonly float[] _rolls;
    private readonly SplineUp _up;
    private readonly Vector3[] _ups;

    public SplineFrames(SplineCurve curve, SplineMeasure measure, bool closed, float[] rolls, SplineUp up)
    {
        _curve = curve;
        _rolls = rolls;
        _up = up;
        var tangents = SampleHeadings(curve, measure);
        _ups = CarriedUps(measure.SamplePoints, tangents);
        if (closed && measure.Length > 0f)
        {
            SpreadTwist(_ups, tangents, measure);
        }
    }

    /// <summary>A rotation whose +z is <paramref name="forward"/> and +y is <paramref name="up"/>, which must be square to it.</summary>
    public static Quaternion Facing(Vector3 forward, Vector3 up)
    {
        var side = Vector3.Cross(up, forward);
        var rotation = new Matrix4x4(
            side.X, side.Y, side.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    public SplinePose PoseAt(int segment, float amount)
    {
        var forward = _curve.Heading(segment, amount);
        var up = UpAt(segment, amount, forward);
        float roll = RollAt(segment, amount);
        if (roll != 0f)
        {
            up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, roll));
        }

        return new SplinePose(_curve.Point(segment, amount), Facing(forward, up));
    }

    private static Vector3[] SampleHeadings(SplineCurve curve, SplineMeasure measure)
    {
        var tangents = new Vector3[measure.SamplePoints.Length];
        for (int k = 0; k < tangents.Length; k++)
        {
            var (segment, amount) = measure.Sample(k);
            tangents[k] = curve.Heading(segment, amount);
        }

        return tangents;
    }

    /// <summary>
    /// The up direction at every sample, carried along by double reflection, which turns it
    /// as little as the curve allows.
    /// </summary>
    private static Vector3[] CarriedUps(Vector3[] samples, Vector3[] tangents)
    {
        var ups = new Vector3[samples.Length];
        var first = Vector3.UnitY - tangents[0] * tangents[0].Y;
        if (first.LengthSquared() < 1e-6f)
        {
            first = Vector3.UnitZ - tangents[0] * tangents[0].Z;
        }

        ups[0] = Vector3.Normalize(first);
        for (int k = 0; k < samples.Length - 1; k++)
        {
            var step = samples[k + 1] - samples[k];
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

        return ups;
    }

    /// <summary>Spreads the twist a closed spline's ups are left with evenly round it, so the end meets the start.</summary>
    private static void SpreadTwist(Vector3[] ups, Vector3[] tangents, SplineMeasure measure)
    {
        var axis = tangents[0];
        var last = ups[^1];
        float twist = MathF.Atan2(Vector3.Dot(Vector3.Cross(last, ups[0]), axis), Vector3.Dot(last, ups[0]));
        for (int k = 1; k < ups.Length; k++)
        {
            var turned = Vector3.Transform(ups[k], Quaternion.CreateFromAxisAngle(tangents[k], twist * measure.DistanceTo(k) / measure.Length));
            ups[k] = Vector3.Normalize(turned - tangents[k] * Vector3.Dot(turned, tangents[k]));
        }
    }

    private float RollAt(int segment, float u)
    {
        float from = _rolls[segment];
        float to = _rolls[(segment + 1) % _rolls.Length];
        return float.Lerp(from, to, u * u * (3f - 2f * u));
    }

    private Vector3 UpAt(int segment, float u, Vector3 forward)
    {
        if (_up == SplineUp.World)
        {
            var upright = Vector3.UnitY - forward * forward.Y;
            if (upright.LengthSquared() > 1e-6f)
            {
                return Vector3.Normalize(upright);
            }
        }

        float place = (segment + u) * SplineMeasure.Steps;
        int k = Math.Min((int)place, _ups.Length - 2);
        var blended = Vector3.Lerp(_ups[k], _ups[k + 1], place - k);
        var square = blended - forward * Vector3.Dot(blended, forward);
        if (square.LengthSquared() < 1e-10f)
        {
            square = _ups[k] - forward * Vector3.Dot(_ups[k], forward);
        }

        return Vector3.Normalize(square);
    }
}

/// <summary>The checks a spline's points, knots and rolls must pass, with the messages that explain them.</summary>
internal static class SplineChecks
{
    /// <summary>A spline takes points or knots, but not both.</summary>
    public static void Source(object? points, object? knots)
    {
        if (points is null && knots is null)
        {
            throw new ArgumentException("A spline needs points or knots.");
        }

        if (points is not null && knots is not null)
        {
            throw new ArgumentException("A spline takes points or knots, not both.");
        }
    }

    public static void Positions(IReadOnlyList<Vector3> positions, string word, bool closed)
    {
        CheckEnough(positions.Count, word, closed);
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

        if (closed && positions[^1] == positions[0])
        {
            throw new ArgumentException($"The last spline {word} is where the first is. A closed spline joins its ends itself, so leave the last one out.");
        }
    }

    public static void Knots(IReadOnlyList<SplineKnot> knots, IReadOnlyList<float>? roll, bool closed)
    {
        if (roll is not null)
        {
            throw new ArgumentException("Knots carry their own roll, so a spline made of knots takes no roll list.");
        }

        Positions([.. knots.Select(knot => knot.Position)], "knot", closed);
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

    public static void Rolls(int points, IReadOnlyList<float>? roll)
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

    private static void CheckEnough(int count, string word, bool closed)
    {
        int least = closed ? 3 : 2;
        if (count < least)
        {
            throw new ArgumentException(closed
                ? $"A closed spline needs at least three {word}s."
                : $"A spline needs at least two {word}s.");
        }
    }
}
