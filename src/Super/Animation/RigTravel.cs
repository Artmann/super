using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// How far root motion carries the character over a stretch of a clip, and how far it turns about the up axis,
/// in radians. The travel is in the character's facing at the stretch's start.
/// </summary>
internal readonly record struct RootStep(Vector3 Travel, float Turn)
{
    /// <summary>This step, then <paramref name="next"/> from where this one ends and faces.</summary>
    public RootStep Then(RootStep next) => new(Travel + TurnBy(next.Travel, Turn), Turn + next.Turn);

    /// <summary>Turns a vector about the up axis, as <see cref="Matrix4x4.CreateRotationY(float)"/> does.</summary>
    public static Vector3 TurnBy(Vector3 vector, float radians)
    {
        if (radians == 0f)
        {
            return vector;
        }

        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector3(vector.X * cos + vector.Z * sin, vector.Y, vector.Z * cos - vector.X * sin);
    }
}

/// <summary>
/// A rig's root motion: how far each clip carries the character over the ground, taken out
/// of the clips when <see cref="Rig.RootMotion"/> picks the joint that carries the body, and
/// for the clips it names as turning, how far each turns the character about the up axis.
/// </summary>
internal sealed class RigTravel
{
    private readonly Vector3[][] _tracks;
    private readonly float[]?[] _turns;
    private readonly IReadOnlyList<AnimationClip> _clips;

    /// <summary>
    /// Pins <paramref name="joint"/> over its first frame's spot in every clip, keeping where it would have gone.
    /// In the clips <paramref name="turning"/> names, it also holds the joint to its first frame's facing, keeping
    /// how far it would have turned.
    /// </summary>
    public RigTravel(Skeleton skeleton, IReadOnlyList<AnimationClip> clips, int joint, IReadOnlySet<int> turning)
    {
        var tracks = new Vector3[clips.Count][];
        var turns = new float[]?[clips.Count];
        for (int i = 0; i < clips.Count; i++)
        {
            (tracks[i], turns[i]) = Take(skeleton, clips[i], joint, turning.Contains(i));
        }

        _tracks = tracks;
        _turns = turns;
        _clips = clips;
        CarrierJoint = joint;
        Turning = turning;
    }

    /// <summary>The joint root motion comes from.</summary>
    public int CarrierJoint { get; }

    /// <summary>The clips whose turns come out of the pose.</summary>
    public IReadOnlySet<int> Turning { get; }

    /// <summary>
    /// How far a clip has carried the character over the ground from its first frame,
    /// in the model's space, at a moment held at the ends.
    /// </summary>
    public Vector3 At(int clip, float seconds) => Sample(clip, seconds).Travel;

    /// <summary>How far a turning clip has turned the character from its first frame, at a moment held at the ends.</summary>
    public float TurnAt(int clip, float seconds) => Sample(clip, seconds).Turn;

    /// <summary>
    /// How far a clip carries and turns the character from <paramref name="time"/> on over this much time at this
    /// speed, with the travel in the character's facing at <paramref name="time"/>. A loop carries on from where
    /// its last cycle ended and faced, so each wrap adds a whole cycle.
    /// </summary>
    public RootStep Over(int clip, float time, float speed, bool loops, float seconds)
    {
        var from = Place(clip, time, loops);
        var to = Place(clip, time + seconds * speed, loops);
        return new RootStep(RootStep.TurnBy(to.Travel - from.Travel, -from.Turn), to.Turn - from.Turn);
    }

    /// <summary>Where the clip has carried and turned the character by this moment; a loop goes on lap after lap.</summary>
    private RootStep Place(int clip, float seconds, bool loops)
    {
        float duration = _clips[clip].Duration;
        if (!loops || duration <= 0f)
        {
            return Sample(clip, seconds);
        }

        float cycles = MathF.Floor(seconds / duration);
        return Laps(Sample(clip, duration), cycles).Then(Sample(clip, seconds - cycles * duration));
    }

    /// <summary>
    /// A lap taken this many times over, each from where the last ended and faced. A turning lap sums a geometric
    /// series of turns, worked out at once, since a long-running loop has had many laps. Negative counts go back.
    /// </summary>
    private static RootStep Laps(RootStep lap, float count)
    {
        if (lap.Turn == 0f)
        {
            return new RootStep(lap.Travel * count, 0f);
        }

        // On the ground a turn by a is multiplying x - iz by e^(ia), so the laps' travel is the lap's times
        // 1 + e^(ia) + ... + e^(i(n-1)a) = (1 - e^(ina)) / (1 - e^(ia)), which holds for negative n too.
        double turn = lap.Turn;
        var step = System.Numerics.Complex.FromPolarCoordinates(1, turn);
        var denominator = 1 - step;
        var sum = denominator.Magnitude < 1e-9
            ? new System.Numerics.Complex(count, 0)
            : (1 - System.Numerics.Complex.FromPolarCoordinates(1, turn * count)) / denominator;
        var travel = new System.Numerics.Complex(lap.Travel.X, -lap.Travel.Z) * sum;
        return new RootStep(new Vector3((float)travel.Real, lap.Travel.Y * count, (float)-travel.Imaginary), (float)(turn * count));
    }

    /// <summary>Where a clip has carried and turned the character at a moment, held at the ends.</summary>
    private RootStep Sample(int clip, float seconds)
    {
        var track = _tracks[clip];
        float frame = Math.Clamp(seconds * _clips[clip].FrameRate, 0f, track.Length - 1);
        int first = Math.Min((int)frame, track.Length - 2);
        if (first < 0)
        {
            return new RootStep(track[0], 0f);
        }

        float amount = frame - first;
        float turn = _turns[clip] is { } turns ? float.Lerp(turns[first], turns[first + 1], amount) : 0f;
        return new RootStep(Vector3.Lerp(track[first], track[first + 1], amount), turn);
    }

    /// <summary>
    /// Pins the joint over its first frame's spot on the ground, frame by frame, and returns where the character
    /// would have gone. A turning clip also holds the joint to its first frame's facing and returns how far it
    /// turned, so that the character turned and moved by both, with the pinned pose on it, is the clip as it was.
    /// The sums run in the model's space, after the joint's parents and the root transform, since only there is up
    /// the y axis.
    /// </summary>
    private static (Vector3[] Travel, float[]? Turns) Take(Skeleton skeleton, AnimationClip clip, int joint, bool turns)
    {
        int joints = skeleton.Count;
        var globals = new Matrix4x4[joints];
        var track = new Vector3[clip.FrameCount];
        var turnTrack = turns ? new float[clip.FrameCount] : null;
        Vector3 start = default;
        Quaternion facing = Quaternion.Identity;
        float turned = 0f;
        for (int f = 0; f < clip.FrameCount; f++)
        {
            var frame = clip.Frames.AsSpan(f * joints, joints);
            var toModel = ToModel(skeleton, frame, joint, globals);
            var at = Vector3.Transform(frame[joint].Position, toModel);
            if (f == 0)
            {
                start = at;
                facing = Quaternion.Normalize(JointPose.FromMatrix(toModel).Rotation) * frame[joint].Rotation;
            }

            if (turnTrack is not null)
            {
                turned = Unturn(ref frame[joint], toModel, facing, turned);
                turnTrack[f] = turned;
            }

            // Turned by the turn so far about the model's origin, the pinned spot lands where the joint was.
            track[f] = new Vector3(at.X, 0f, at.Z) - RootStep.TurnBy(new Vector3(start.X, 0f, start.Z), turned);
            if (!Matrix4x4.Invert(toModel, out var toJoint))
            {
                throw new InvalidOperationException($"The joints above {skeleton.Names[joint]} squash it flat in clip {clip.Name}, so its travel can't be taken out.");
            }

            frame[joint].Position = Vector3.Transform(new Vector3(start.X, at.Y, start.Z), toJoint);
        }

        return (track, turnTrack);
    }

    /// <summary>What carries the joint's own space into the model's in this frame: its parents, then the root transform.</summary>
    private static Matrix4x4 ToModel(Skeleton skeleton, ReadOnlySpan<JointPose> frame, int joint, Matrix4x4[] globals)
    {
        for (int i = 0; i < joint; i++)
        {
            globals[i] = skeleton.Parents[i] >= 0 ? frame[i].Matrix * globals[skeleton.Parents[i]] : frame[i].Matrix;
        }

        int parent = skeleton.Parents[joint];
        return (parent >= 0 ? globals[parent] : Matrix4x4.Identity) * skeleton.RootTransform;
    }

    /// <summary>
    /// Turns the joint back about the model's up axis to its first frame's facing, leaving any lean, and returns how far
    /// it had turned, counted on from the frame before so it keeps going past once round.
    /// </summary>
    private static float Unturn(ref JointPose pose, Matrix4x4 toModel, Quaternion facing, float last)
    {
        var toModelTurn = Quaternion.Normalize(JointPose.FromMatrix(toModel).Rotation);
        var inModel = toModelTurn * pose.Rotation;
        float turned = Unwrap(Yaw(inModel * Quaternion.Conjugate(facing)), last);
        var back = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -turned);
        pose.Rotation = Quaternion.Normalize(Quaternion.Conjugate(toModelTurn) * back * inModel);
        return turned;
    }

    /// <summary>How far a rotation turns about the up axis: the twist left once its tilt is taken away.</summary>
    private static float Yaw(Quaternion rotation) => 2f * MathF.Atan2(rotation.Y, rotation.W);

    /// <summary>The angle a whole number of turns from <paramref name="yaw"/> that is nearest the last one.</summary>
    private static float Unwrap(float yaw, float last) => yaw + MathF.Tau * MathF.Round((last - yaw) / MathF.Tau);
}
