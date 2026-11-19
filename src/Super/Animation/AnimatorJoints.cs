using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// An animator's joints in the model, worked out when first asked for: as of the tick, which Update, physics and sound
/// see, and a set ahead of it, as the skinned model is drawn between ticks.
/// </summary>
internal sealed class AnimatorJoints(Skeleton skeleton, AnimationPose pose)
{
    private readonly PoseJointCache _tick = new(skeleton, (_, locals, scratch) => pose.Evaluate(0f, locals, scratch, 1f));
    private readonly PoseJointCache _drawn = new(skeleton, (ahead, locals, scratch) => pose.Evaluate(ahead, locals, scratch));

    /// <summary>A joint <paramref name="ahead"/> seconds past the last tick, or as of the tick when that's null.</summary>
    internal Matrix4x4 At(int joint, float? ahead) =>
        ahead is { } seconds ? _drawn.InModel(joint, pose.Version, seconds) : _tick.InModel(joint, pose.Version, 0f);

    /// <summary>Forgets every joint, for when the pose is laid out again.</summary>
    internal void Invalidate()
    {
        _tick.Invalidate();
        _drawn.Invalidate();
    }
}
