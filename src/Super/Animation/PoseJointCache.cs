using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// Each joint of a pose in the model, worked out when first asked for and kept until the pose
/// changes: its layers are built again, or the game or physics moves a joint.
/// </summary>
/// <param name="evaluate">Works out the pose's local joints as of the last tick, into the first array, using the second as scratch.</param>
internal sealed class PoseJointCache(Skeleton skeleton, Action<JointPose[], JointPose[]> evaluate)
{
    private Matrix4x4[]? _joints;
    private JointPose[]? _locals;
    private JointPose[]? _scratch;
    private bool _fresh;
    private int _version;

    /// <summary>Forgets the joints, as the layers they came from have changed.</summary>
    public void Invalidate() => _fresh = false;

    /// <summary>Where a joint is in the model, for the pose at this version.</summary>
    public Matrix4x4 InModel(int joint, int version)
    {
        if (!_fresh || _joints is null || _version != version)
        {
            _version = version;
            _joints ??= new Matrix4x4[skeleton.Count];
            _locals ??= new JointPose[skeleton.Count];
            _scratch ??= new JointPose[skeleton.Count];
            var locals = _locals;
            evaluate(locals, _scratch);
            for (int i = 0; i < skeleton.Count; i++)
            {
                _joints[i] = skeleton.Parents[i] >= 0 ? locals[i].Matrix * _joints[skeleton.Parents[i]] : locals[i].Matrix;
            }

            _fresh = true;
        }

        return _joints[joint] * skeleton.RootTransform;
    }
}
