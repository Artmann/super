using System.Numerics;
using Rendition;
using Super.Ecs;

namespace Super.Physics;

/// <summary>Where the animation has each part of a ragdoll in the world, now and a step ago, to tell how fast they move.</summary>
internal sealed class RagdollTargets
{
    private readonly JointPose[] _locals;
    private readonly JointPose[] _scratch;
    private readonly Matrix4x4[] _globals;
    private Vector3[] _previousPositions;
    private Quaternion[] _previousRotations;
    private float _previousSeconds;
    private bool _has;

    public RagdollTargets(RagdollBody body)
    {
        int parts = body.PartTree.Joints.Length;
        int joints = body.Rig.Skeleton.Count;
        Positions = new Vector3[parts];
        Rotations = new Quaternion[parts];
        _previousPositions = new Vector3[parts];
        _previousRotations = new Quaternion[parts];
        _locals = new JointPose[joints];
        _scratch = new JointPose[joints];
        _globals = new Matrix4x4[joints];
    }

    public Vector3[] Positions { get; private set; }

    public Quaternion[] Rotations { get; private set; }

    /// <summary>Whether there's a step before this one, so <see cref="Velocity"/> can tell how fast a part moves.</summary>
    public bool HasPrevious { get; private set; }

    /// <summary>Works out where the animation has each part in the world now, keeping the last step's.</summary>
    public void Aim(RagdollBody body, Animator animator, in Transform transform, float seconds)
    {
        if (seconds > 0f && _has)
        {
            (_previousPositions, Positions) = (Positions, _previousPositions);
            (_previousRotations, Rotations) = (Rotations, _previousRotations);
            _previousSeconds = seconds;
            HasPrevious = true;
        }

        var skeleton = body.Rig.Skeleton;
        animator.Pose.Evaluate(0f, _locals, _scratch, null);
        for (int j = 0; j < skeleton.Count; j++)
        {
            int parent = skeleton.Parents[j];
            _globals[j] = parent >= 0 ? _locals[j].Matrix * _globals[parent] : _locals[j].Matrix;
        }

        var toWorld = skeleton.RootTransform * transform.Matrix;
        for (int i = 0; i < body.PartTree.Joints.Length; i++)
        {
            var world = _globals[body.PartTree.Joints[i]] * toWorld;
            Matrix4x4.Decompose(world, out _, out var rotation, out var position);
            Positions[i] = position;
            Rotations[i] = Quaternion.Normalize(rotation);
        }

        _has = true;
    }

    /// <summary>How fast a part's middle, <paramref name="centre"/> from its joint, moves and turns as the animation carries it.</summary>
    public (Vector3 Velocity, Vector3 Spin) Velocity(int part, Vector3 centre)
    {
        var now = Positions[part] + Vector3.Transform(centre, Rotations[part]);
        var before = _previousPositions[part] + Vector3.Transform(centre, _previousRotations[part]);
        var velocity = (now - before) / _previousSeconds;
        var turn = Rotations[part] * Quaternion.Inverse(_previousRotations[part]);
        return (velocity, Turns.AxisAngle(turn) / _previousSeconds);
    }

    /// <summary>Where the clips playing now put the pelvis, and which way its up points, in the entity's space.</summary>
    public (Vector3 Position, Vector3 Up) Pelvis(RagdollBody body, Animator animator)
    {
        var skeleton = body.Rig.Skeleton;
        animator.Pose.Evaluate(0f, _locals, _scratch, null);
        int pelvis = body.PartTree.Joints[0];
        var global = _locals[pelvis].Matrix;
        for (int j = skeleton.Parents[pelvis]; j >= 0; j = skeleton.Parents[j])
        {
            global *= _locals[j].Matrix;
        }

        Matrix4x4.Decompose(global * skeleton.RootTransform, out _, out var rotation, out var position);
        return (position, Vector3.Transform(body.Fitting.PelvisUp, Quaternion.Normalize(rotation)));
    }
}
