using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A pose being worked on in the skeleton's own space, before the root transform: each joint's local pose, and beside
/// it the place and rotation the joint ends up with. Parents come first, so a joint is placed once its parent is.
/// </summary>
internal readonly ref struct SkeletonSpace
{
    public SkeletonSpace(int[] parents, Span<JointPose> pose, Span<Matrix4x4> globals, Span<Quaternion> turned)
    {
        Parents = parents;
        Pose = pose;
        Globals = globals;
        Turned = turned;
    }

    public int[] Parents { get; }

    /// <summary>Each joint's pose relative to its parent.</summary>
    public Span<JointPose> Pose { get; }

    /// <summary>Each joint's place in the skeleton, worked out by <see cref="Place"/>.</summary>
    public Span<Matrix4x4> Globals { get; }

    /// <summary>Each joint's rotation in the skeleton, worked out by <see cref="Place"/>.</summary>
    public Span<Quaternion> Turned { get; }

    /// <summary>Works out a joint's place and rotation in the skeleton from its parent's.</summary>
    public void Place(int joint)
    {
        int parent = Parents[joint];
        Globals[joint] = parent >= 0 ? Pose[joint].Matrix * Globals[parent] : Pose[joint].Matrix;
        Turned[joint] = parent >= 0 ? Quaternion.Normalize(Turned[parent] * Pose[joint].Rotation) : Pose[joint].Rotation;
    }

    /// <summary>Turns a joint about itself by a rotation in the skeleton's space, its parent's place already known.</summary>
    public void Turn(int joint, Quaternion turn)
    {
        int parent = Parents[joint];
        var local = parent >= 0 ? Within(turn, Turned[parent]) : turn;
        Pose[joint].Rotation = Quaternion.Normalize(local * Pose[joint].Rotation);
    }

    /// <summary>
    /// Bends a limb so <paramref name="point"/>, in its tip's space, reaches <paramref name="goal"/> in the skeleton's
    /// space, mixed in by <paramref name="weight"/>. The bend keeps to the side it's on, and the end keeps its turn.
    /// </summary>
    public void Reach(Limb limb, Vector3 point, float weight, Vector3 goal)
    {
        var upper = Globals[limb.Upper].Translation;
        var lower = Globals[limb.Lower].Translation;
        var end = Globals[limb.End].Translation;
        var endTurn = Turned[limb.End];

        // What the tip holds stays where it is relative to the end, which keeps its turn, so
        // the end's goal is the goal less that.
        var held = Vector3.Transform(point, Globals[limb.Tip]) - end;
        var wrist = goal - held;

        float upperLength = Vector3.Distance(upper, lower);
        float lowerLength = Vector3.Distance(lower, end);
        var toGoal = wrist - upper;
        float span = toGoal.Length();
        if (upperLength < 1e-6f || lowerLength < 1e-6f || span < 1e-6f)
        {
            return;
        }

        var along = toGoal / span;
        span = Math.Clamp(span, MathF.Abs(upperLength - lowerLength) * 1.001f, (upperLength + lowerLength) * 0.999f);

        // The bend keeps to the side the layers bend it: the lower joint's way out from the line
        // between the upper joint and the goal.
        var bend = lower - upper - (Vector3.Dot(lower - upper, along) * along);
        if (bend.LengthSquared() < 1e-10f)
        {
            bend = Vector3.Cross(along, MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }

        bend = Vector3.Normalize(bend);
        float cos = Math.Clamp(((upperLength * upperLength) + (span * span) - (lowerLength * lowerLength)) / (2f * upperLength * span), -1f, 1f);
        var elbow = upper + (upperLength * ((cos * along) + (MathF.Sqrt(1f - (cos * cos)) * bend)));

        var first = Quaternion.Slerp(Quaternion.Identity, Between(lower - upper, elbow - upper), weight);
        Turn(limb.Upper, first);
        Refresh(limb.Upper);

        var bent = Globals[limb.Lower].Translation;
        var second = Quaternion.Slerp(Quaternion.Identity, Between(Globals[limb.End].Translation - bent, upper + (span * along) - bent), weight);
        Turn(limb.Lower, second);
        Refresh(limb.Lower);

        // The end turns back to how the layers had it.
        Turn(limb.End, endTurn * Quaternion.Inverse(Turned[limb.End]));
        Refresh(limb.End);
    }

    /// <summary>A rotation given outside a frame, such as the model's, as the same rotation inside it.</summary>
    public static Quaternion Within(Quaternion turn, Quaternion frame) => Quaternion.Inverse(frame) * turn * frame;

    /// <summary>Works out a joint and everything under it again, after it's turned.</summary>
    private void Refresh(int joint)
    {
        Span<bool> under = Parents.Length <= 256 ? stackalloc bool[Parents.Length] : new bool[Parents.Length];
        under[joint] = true;
        Place(joint);
        for (int i = joint + 1; i < Parents.Length; i++)
        {
            if (Parents[i] >= 0 && under[Parents[i]])
            {
                under[i] = true;
                Place(i);
            }
        }
    }

    /// <summary>The shortest rotation that takes one direction to another.</summary>
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float dot = Vector3.Dot(from, to);
        if (dot > 0.99999f)
        {
            return Quaternion.Identity;
        }

        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-10f)
        {
            axis = Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }

        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(dot, -1f, 1f)));
    }
}
