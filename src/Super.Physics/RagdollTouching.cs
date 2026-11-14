using System.Numerics;

namespace Super.Physics;

/// <summary>A capsule round a ragdoll part at rest, in the skeleton's space: the ends of its middle, and its radius.</summary>
internal readonly record struct RestCapsule(Vector3 From, Vector3 To, float Radius);

/// <summary>
/// Which of a ragdoll's parts mustn't collide with each other: each part and its parent, which meet at their joint,
/// and any two whose shapes overlap at rest, which would otherwise start out pushing each other apart. Every other
/// pair collides, so a leg can't pass through the other.
/// </summary>
internal static class RagdollTouching
{
    /// <summary>The pairs of parts kept from colliding, the later part first.</summary>
    public static List<(int A, int B)> Pairs(int[] parents, RestCapsule[] capsules)
    {
        List<(int A, int B)> pairs = [];
        for (int a = 0; a < parents.Length; a++)
        {
            for (int b = 0; b < a; b++)
            {
                bool joined = parents[a] == b || parents[b] == a;
                if (joined || Overlap(capsules[a], capsules[b]))
                {
                    pairs.Add((a, b));
                }
            }
        }

        return pairs;
    }

    /// <summary>A capsule round a part's shape, placed by its joint at rest. For a box it reaches past the corners.</summary>
    public static RestCapsule Covering((Vector3 Centre, Quaternion Turn, RagdollShape Shape, Vector3 HalfSize, float Radius) shape, Matrix4x4 rest)
    {
        var half = shape.HalfSize;
        var (along, radius) = shape.Shape switch
        {
            // A box: along its longest side, as wide as the diagonal across the other two.
            RagdollShape.Box when half.X >= half.Y && half.X >= half.Z => (new Vector3(half.X, 0f, 0f), Diagonal(half.Y, half.Z)),
            RagdollShape.Box when half.Y >= half.Z => (new Vector3(0f, half.Y, 0f), Diagonal(half.X, half.Z)),
            RagdollShape.Box => (new Vector3(0f, 0f, half.Z), Diagonal(half.X, half.Y)),
            _ => (Vector3.Transform(new Vector3(0f, half.Y, 0f), shape.Turn), shape.Radius),
        };
        return new RestCapsule(Vector3.Transform(shape.Centre - along, rest), Vector3.Transform(shape.Centre + along, rest), radius);
    }

    private static float Diagonal(float a, float b) => MathF.Sqrt((a * a) + (b * b));

    private static bool Overlap(RestCapsule a, RestCapsule b)
    {
        float reach = a.Radius + b.Radius;
        var (s, t) = Nearest(a.From, a.To, b.From, b.To);
        return Vector3.DistanceSquared(Vector3.Lerp(a.From, a.To, s), Vector3.Lerp(b.From, b.To, t)) < reach * reach;
    }

    /// <summary>How far along each of two segments, from 0 to 1, their nearest points are, as Ericson's Real-Time Collision Detection works it out.</summary>
    private static (float S, float T) Nearest(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        const float Tiny = 1e-12f;
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        if (a <= Tiny)
        {
            return (0f, e <= Tiny ? 0f : Math.Clamp(f / e, 0f, 1f));
        }

        float c = Vector3.Dot(d1, r);
        if (e <= Tiny)
        {
            return (Math.Clamp(-c / a, 0f, 1f), 0f);
        }

        float b = Vector3.Dot(d1, d2);
        float denominator = (a * e) - (b * b);
        float s = denominator > Tiny ? Math.Clamp(((b * f) - (c * e)) / denominator, 0f, 1f) : 0f;
        float t = ((b * s) + f) / e;
        if (t < 0f)
        {
            return (Math.Clamp(-c / a, 0f, 1f), 0f);
        }

        return t > 1f ? (Math.Clamp((b - c) / a, 0f, 1f), 1f) : (s, t);
    }
}
