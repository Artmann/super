using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Joints a physics body holds over an <see cref="AnimationPose"/>, such as a ragdoll's: where each was in the
/// skeleton's space at the tick before and at this one, and how much it counts over the layers. Only the turn is
/// taken, but for the one joint whose place is held too, so bones keep their length.
/// </summary>
internal sealed class HeldJoints
{
    private JointPose[]? _from;
    private JointPose[]? _to;
    private float[]? _weights;
    private int _placed = -1;
    private float _tick;

    public bool IsActive { get; private set; }

    /// <summary>Takes the joints' places at the tick before and this one, which are <paramref name="tick"/> seconds apart.</summary>
    public void Set(ReadOnlySpan<JointPose> from, ReadOnlySpan<JointPose> to, ReadOnlySpan<float> weights, int placed, float tick)
    {
        _from ??= new JointPose[from.Length];
        _to ??= new JointPose[from.Length];
        _weights ??= new float[from.Length];
        from.CopyTo(_from);
        to.CopyTo(_to);
        weights.CopyTo(_weights);
        _placed = placed;
        _tick = tick;
        IsActive = true;
    }

    /// <summary>Lets go of every joint, saying whether any were held.</summary>
    public bool Clear()
    {
        bool was = IsActive;
        IsActive = false;
        return was;
    }

    /// <summary>How far from the tick before to the last the joints are, <paramref name="ahead"/> seconds after the last tick.</summary>
    public float Progress(float ahead) => _tick > 0f ? Math.Clamp(ahead / _tick, 0f, 1f) : 1f;

    /// <summary>
    /// Puts the held joints over the pose, <paramref name="amount"/> of the way from the tick before to the last.
    /// Parents come first, so each joint's turn is made relative to where its parent ended up, whether physics or the
    /// layers put it there.
    /// </summary>
    public void Apply(int[] parents, float amount, Span<JointPose> pose)
    {
        int count = parents.Length;
        Span<Matrix4x4> globals = count <= 128 ? stackalloc Matrix4x4[count] : new Matrix4x4[count];
        Span<Quaternion> turned = count <= 128 ? stackalloc Quaternion[count] : new Quaternion[count];
        var space = new SkeletonSpace(parents, pose, globals, turned);
        for (int i = 0; i < count; i++)
        {
            float weight = _weights![i];
            if (weight > 0f)
            {
                var held = Held(space, i, amount);
                pose[i] = weight >= 1f ? held : JointPose.Lerp(in pose[i], in held, weight);
            }

            space.Place(i);
        }
    }

    /// <summary>A held joint's pose relative to its parent, which is already placed.</summary>
    private JointPose Held(SkeletonSpace space, int joint, float amount)
    {
        var target = JointPose.Lerp(in _from![joint], in _to![joint], amount);
        int parent = space.Parents[joint];
        var held = space.Pose[joint];
        held.Rotation = parent >= 0 ? Quaternion.Normalize(Quaternion.Inverse(space.Turned[parent]) * target.Rotation) : target.Rotation;
        if (joint == _placed)
        {
            held.Position = target.Position;
            if (parent >= 0 && Matrix4x4.Invert(space.Globals[parent], out var fromParent))
            {
                held.Position = Vector3.Transform(target.Position, fromParent);
            }
        }

        return held;
    }
}
