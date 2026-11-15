using System.Numerics;
using Rendition;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// The pose a ragdoll hands its animator: the simulated joints where physics has them, between the last step and this
/// one, and once it's off, held where it lay and blending back into the clips.
/// </summary>
internal sealed class RagdollHeldPose
{
    private readonly float[] _held;
    private readonly float[] _before;
    private JointPose[] _from;
    private JointPose[] _to;
    private float _fade;

    public RagdollHeldPose(RagdollBody body)
    {
        int joints = body.Rig.Skeleton.Count;
        _from = new JointPose[joints];
        _to = new JointPose[joints];
        Weights = new float[joints];
        _held = new float[joints];
        _before = new float[joints];
    }

    /// <summary>How much of each joint is the ragdoll's: 1 for a simulated one, 0 for the clips'.</summary>
    public float[] Weights { get; }

    /// <summary>The joint the pose also places, the pelvis while it's simulated, or -1.</summary>
    public int Placed { get; private set; } = -1;

    /// <summary>Whether the animator has a pose from the ragdoll.</summary>
    public bool Showing { get; private set; }

    /// <summary>Takes this step's pose from the parts, keeping the last to show between.</summary>
    public void Step(RagdollBody body, RagdollParts parts, float scale, in Transform transform)
    {
        (_from, _to) = (_to, _from);
        Weights.AsSpan().CopyTo(_before);
        Hold(body, parts, scale, transform);
        if (!Showing)
        {
            _to.AsSpan().CopyTo(_from);
        }

        // A joint that has just joined the simulation, as the mask changed, has no last step to come from.
        for (int j = 0; j < Weights.Length; j++)
        {
            if (Weights[j] > 0f && _before[j] <= 0f)
            {
                _from[j] = _to[j];
            }
        }

        Placed = parts.Dynamic[0] ? body.PartTree.Joints[0] : -1;
    }

    /// <summary>Holds the pose where the ragdoll lay, ready to blend into the clips over <paramref name="seconds"/>.</summary>
    public void Freeze(RagdollBody body, RagdollParts parts, float scale, in Transform transform, float seconds)
    {
        Hold(body, parts, scale, transform);
        _to.AsSpan().CopyTo(_from);
        Weights.AsSpan().CopyTo(_held);
        Placed = parts.Dynamic[0] ? body.PartTree.Joints[0] : -1;
        _fade = seconds;
    }

    /// <summary>Stops a fade, as the ragdoll turns on again.</summary>
    public void StopFading() => _fade = 0f;

    /// <summary>Gives the animator the pose, shown between the last step and this one over <paramref name="seconds"/>.</summary>
    public void Show(Animator animator, float seconds)
    {
        Showing = true;
        animator.Pose.SetPhysical(_from, _to, Weights, Placed, seconds);
    }

    public void Clear(Animator animator)
    {
        animator.Pose.ClearPhysical();
        Showing = false;
    }

    /// <summary>Blends a held pose back into the clips, a step at a time, over <paramref name="blendSeconds"/>.</summary>
    public void Fade(Animator animator, float blendSeconds, float seconds)
    {
        if (!Showing)
        {
            return;
        }

        _fade -= seconds;
        if (_fade <= 0f || blendSeconds <= 0f)
        {
            Clear(animator);
            return;
        }

        float amount = _fade / blendSeconds;
        for (int j = 0; j < Weights.Length; j++)
        {
            Weights[j] = _held[j] * amount;
        }

        Show(animator, 0f);
    }

    /// <summary>Puts each simulated part's joint, where physics has it, into the skeleton's space under this transform.</summary>
    private void Hold(RagdollBody body, RagdollParts parts, float scale, in Transform transform)
    {
        var skeleton = body.Rig.Skeleton;
        Matrix4x4.Invert(skeleton.RootTransform * transform.Matrix, out var toSkeleton);
        Weights.AsSpan().Clear();
        var size = Matrix4x4.CreateScale(scale);
        for (int i = 0; i < parts.Ids.Length; i++)
        {
            if (!parts.Dynamic[i])
            {
                continue;
            }

            int joint = body.PartTree.Joints[i];
            var world = size * Matrix4x4.CreateFromQuaternion(parts.Rotations[i]) * Matrix4x4.CreateTranslation(parts.Positions[i]);
            _to[joint] = JointPose.FromMatrix(world * toSkeleton);
            Weights[joint] = 1f;
        }
    }
}
