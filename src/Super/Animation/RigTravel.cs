using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// A rig's root motion: how far each clip carries the character over the ground, taken out
/// of the clips when <see cref="Rig.RootMotion"/> picks the joint that carries the body.
/// </summary>
internal sealed class RigTravel
{
    private readonly Vector3[][] _tracks;
    private readonly IReadOnlyList<AnimationClip> _clips;

    /// <summary>Pins <paramref name="joint"/> over its first frame's spot in every clip, keeping where it would have gone.</summary>
    public RigTravel(Skeleton skeleton, IReadOnlyList<AnimationClip> clips, int joint)
    {
        var tracks = new Vector3[clips.Count][];
        for (int i = 0; i < clips.Count; i++)
        {
            tracks[i] = Take(skeleton, clips[i], joint);
        }

        _tracks = tracks;
        _clips = clips;
        CarrierJoint = joint;
    }

    /// <summary>The joint root motion comes from.</summary>
    public int CarrierJoint { get; }

    /// <summary>
    /// How far a clip has carried the character over the ground from its first frame,
    /// in the model's space, at a moment held at the ends.
    /// </summary>
    public Vector3 At(int clip, float seconds)
    {
        var track = _tracks[clip];
        var data = _clips[clip];
        float frame = Math.Clamp(seconds * data.FrameRate, 0f, track.Length - 1);
        int first = Math.Min((int)frame, track.Length - 2);
        if (first < 0)
        {
            return track[0];
        }

        return Vector3.Lerp(track[first], track[first + 1], frame - first);
    }

    /// <summary>
    /// How far a clip carries the character from <paramref name="time"/> on over this much
    /// time at this speed. A loop carries on from where its last cycle ended, so each wrap
    /// adds a whole cycle.
    /// </summary>
    public Vector3 Over(int clip, float time, float speed, bool loops, float seconds)
    {
        float duration = _clips[clip].Duration;
        float from = time;
        float to = from + seconds * speed;
        if (!loops || duration <= 0f)
        {
            return At(clip, to) - At(clip, from);
        }

        float cycles = MathF.Floor(to / duration);
        var lap = At(clip, duration);
        return At(clip, to - cycles * duration) + cycles * lap - At(clip, from);
    }

    /// <summary>
    /// Pins the joint over its first frame's spot on the ground, frame by frame, and
    /// returns where it would have gone. The sums run in the model's space, after the
    /// joint's parents and the root transform, since only there is up the y axis.
    /// </summary>
    private static Vector3[] Take(Skeleton skeleton, AnimationClip clip, int joint)
    {
        int joints = skeleton.Count;
        var globals = new Matrix4x4[joints];
        var track = new Vector3[clip.FrameCount];
        Vector3 start = default;
        for (int f = 0; f < clip.FrameCount; f++)
        {
            var frame = clip.Frames.AsSpan(f * joints, joints);
            for (int i = 0; i < joint; i++)
            {
                globals[i] = skeleton.Parents[i] >= 0 ? frame[i].Matrix * globals[skeleton.Parents[i]] : frame[i].Matrix;
            }

            int parent = skeleton.Parents[joint];
            var toModel = (parent >= 0 ? globals[parent] : Matrix4x4.Identity) * skeleton.RootTransform;
            var at = Vector3.Transform(frame[joint].Position, toModel);
            if (f == 0)
            {
                start = at;
            }

            var travel = new Vector3(at.X - start.X, 0f, at.Z - start.Z);
            track[f] = travel;
            if (!Matrix4x4.Invert(toModel, out var toJoint))
            {
                throw new InvalidOperationException($"The joints above {skeleton.Names[joint]} squash it flat in clip {clip.Name}, so its travel can't be taken out.");
            }

            frame[joint].Position = Vector3.Transform(at - travel, toJoint);
        }

        return track;
    }
}
