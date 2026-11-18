using System.Numerics;
using Rendition;

namespace Super;

/// <summary>The bone matrices of a frame's posed draws, one range for each draw, kept in buffers reused every frame.</summary>
internal sealed class PoseSkinning
{
    private readonly JointPose[] _pose = new JointPose[Skeleton.MaxJoints];
    private readonly JointPose[] _poseScratch = new JointPose[Skeleton.MaxJoints];
    private Matrix4x4[] _bones = new Matrix4x4[256];
    private int _count;

    /// <summary>The frame's bone matrices so far.</summary>
    public ReadOnlySpan<Matrix4x4> Bones => _bones.AsSpan(0, _count);

    /// <summary>Starts a new frame with no bones.</summary>
    public void Clear() => _count = 0;

    /// <summary>
    /// Samples a pose into this frame's bone matrices. Once the frame's bones are full,
    /// the rest of the models stand in their bind pose rather than fail.
    /// </summary>
    public BoneRange Skin(AnimationPose pose, float ahead, ModelOnGpu model, int index, Content content)
    {
        int joints = pose.Rig.Skeleton.Count;
        if (model.JointCount == 0)
        {
            string name = UploadedItems.NameOf(index, content, "a generated mesh");
            throw new InvalidOperationException($"Model {index} ({name}) has no skeleton, so it can't be drawn with a pose.");
        }

        if (joints != model.JointCount)
        {
            throw new InvalidOperationException(
                $"The pose's rig has {joints} joints, but model {index} has {model.JointCount}. Use the rig from the same cooked file.");
        }

        if (_count + joints > Renderer.MaxBones)
        {
            return default;
        }

        if (_count + joints > _bones.Length)
        {
            Array.Resize(ref _bones, Math.Min(Math.Max(_bones.Length * 2, _count + joints), Renderer.MaxBones));
        }

        var local = _pose.AsSpan(0, joints);
        pose.Evaluate(ahead, local, _poseScratch.AsSpan(0, joints));
        pose.Rig.Skeleton.SkinMatrices(local, _bones.AsSpan(_count, joints));
        var range = new BoneRange(_count, joints);
        _count += joints;
        return range;
    }
}
