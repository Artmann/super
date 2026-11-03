using System.Globalization;
using System.Text.Json.Serialization;

namespace Super;

/// <summary>How a curve leaves and enters one of its keys.</summary>
public enum CurveTangent
{
    /// <summary>
    /// Smooth through the key, heading from the key before to the key after. The first and
    /// last keys are flat, so a curve eases in and out of its ends.
    /// </summary>
    Auto,

    /// <summary>Straight lines to the keys either side, so two linear keys are joined by a line.</summary>
    Linear,

    /// <summary>Holds this key's value until the next key, then jumps.</summary>
    Constant,

    /// <summary>Uses the key's own <see cref="CurveKey.In"/> and <see cref="CurveKey.Out"/> slopes.</summary>
    Custom,
}

/// <summary>
/// A point a <see cref="Curve"/> passes through: a value at a time. <see cref="In"/> and
/// <see cref="Out"/> are slopes, in value per second, for a <see cref="CurveTangent.Custom"/> key.
/// </summary>
public readonly record struct CurveKey(float Time, float Value, CurveTangent Tangent = CurveTangent.Auto, float In = 0f, float Out = 0f);

/// <summary>
/// A value that changes over time, drawn by its keys and smooth between them, like an
/// animation curve in an editor. Load one from JSON content as a field of a record, or
/// make one in code. A <see cref="Tween{T}"/> can follow one instead of an easing.
/// </summary>
public sealed class Curve
{
    private readonly CurveKey[] _keys;
    private readonly float[] _in;
    private readonly float[] _out;

    /// <summary>A curve through these keys, which must be in order of time.</summary>
    public Curve(params CurveKey[] keys)
        : this((IReadOnlyList<CurveKey>?)keys, null)
    {
    }

    /// <summary>For JSON, which writes a curve as either <c>keys</c> or an <c>easing</c>.</summary>
    [JsonConstructor]
    internal Curve(IReadOnlyList<CurveKey>? keys = null, Easing? easing = null)
    {
        if (keys is null && easing is null)
        {
            throw new ArgumentException("A curve needs keys or an easing.");
        }

        if (keys is not null && easing is not null)
        {
            throw new ArgumentException("A curve takes keys or an easing, not both.");
        }

        Easing = easing;
        if (easing is not null)
        {
            _keys = [];
            _in = [];
            _out = [];
            Start = 0f;
            End = 1f;
            return;
        }

        _keys = [.. keys!];
        Check(_keys);
        (_in, _out) = Slopes(_keys);
        Start = _keys[0].Time;
        End = _keys[^1].Time;
    }

    /// <summary>The keys, in order of time. A curve made from an easing has none.</summary>
    public IReadOnlyList<CurveKey> Keys => _keys;

    /// <summary>The easing this curve follows, if it was made from one rather than keys.</summary>
    public Easing? Easing { get; }

    /// <summary>The time of the first key. Before it, the curve holds the first value.</summary>
    [JsonIgnore]
    public float Start { get; }

    /// <summary>The time of the last key. After it, the curve holds the last value.</summary>
    [JsonIgnore]
    public float End { get; }

    /// <summary>The time from the first key to the last.</summary>
    [JsonIgnore]
    public float Duration => End - Start;

    /// <summary>An easing as a curve, from 0 to 1 over a second, to hand to anything that takes a curve.</summary>
    public static Curve From(Easing easing) => new(null, easing);

    /// <summary>The curve's value at a time.</summary>
    public float Evaluate(float time)
    {
        if (Easing is { } easing)
        {
            return Ease.Apply(easing, time);
        }

        if (time <= Start || _keys.Length == 1)
        {
            return _keys[0].Value;
        }

        if (time >= End)
        {
            return _keys[^1].Value;
        }

        int segment = Segment(time);
        var from = _keys[segment];
        var to = _keys[segment + 1];
        if (from.Tangent == CurveTangent.Constant)
        {
            return from.Value;
        }

        float length = to.Time - from.Time;
        float s = (time - from.Time) / length;
        float s2 = s * s;
        float s3 = s2 * s;

        // Cubic Hermite: the two values, and the slopes leaving one and entering the other.
        return (2f * s3 - 3f * s2 + 1f) * from.Value
            + (s3 - 2f * s2 + s) * length * _out[segment]
            + (-2f * s3 + 3f * s2) * to.Value
            + (s3 - s2) * length * _in[segment + 1];
    }

    /// <summary>The curve's value at an amount of the way from its first key to its last, from 0 to 1.</summary>
    public float Sample(float amount) => Evaluate(Start + Math.Clamp(amount, 0f, 1f) * Duration);

    /// <summary>The key that starts the stretch holding this time, which is inside the curve.</summary>
    private int Segment(float time)
    {
        int low = 0;
        int high = _keys.Length - 2;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_keys[middle].Time <= time)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static void Check(CurveKey[] keys)
    {
        if (keys.Length == 0)
        {
            throw new ArgumentException("A curve needs at least one key.");
        }

        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!float.IsFinite(key.Time) || !float.IsFinite(key.Value) || !float.IsFinite(key.In) || !float.IsFinite(key.Out))
            {
                throw new ArgumentException($"Curve key {i} must have a finite time, value and slopes.");
            }

            if (key.Tangent != CurveTangent.Custom && (key.In != 0f || key.Out != 0f))
            {
                throw new ArgumentException($"Curve key {i} sets in or out slopes, which only a custom tangent uses.");
            }

            if (i > 0 && key.Time <= keys[i - 1].Time)
            {
                throw new ArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Curve keys must be in order of time, but key {i} at {key.Time} comes after {keys[i - 1].Time}."));
            }
        }
    }

    /// <summary>The slope into and out of every key, worked out once.</summary>
    private static (float[] In, float[] Out) Slopes(CurveKey[] keys)
    {
        int count = keys.Length;
        float[] into = new float[count];
        float[] outOf = new float[count];
        for (int i = 0; i < count; i++)
        {
            var key = keys[i];
            float? before = i > 0 ? (key.Value - keys[i - 1].Value) / (key.Time - keys[i - 1].Time) : null;
            float? after = i < count - 1 ? (keys[i + 1].Value - key.Value) / (keys[i + 1].Time - key.Time) : null;
            switch (key.Tangent)
            {
                case CurveTangent.Auto:
                    float through = before is null || after is null
                        ? 0f
                        : (keys[i + 1].Value - keys[i - 1].Value) / (keys[i + 1].Time - keys[i - 1].Time);
                    into[i] = through;
                    outOf[i] = through;
                    break;
                case CurveTangent.Linear:
                    into[i] = before ?? after ?? 0f;
                    outOf[i] = after ?? before ?? 0f;
                    break;
                case CurveTangent.Custom:
                    into[i] = key.In;
                    outOf[i] = key.Out;
                    break;
                default:
                    into[i] = 0f;
                    outOf[i] = 0f;
                    break;
            }
        }

        return (into, outOf);
    }
}
