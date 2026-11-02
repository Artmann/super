using System.Numerics;
using Rendition;

namespace Super.Tests.Animation;

/// <summary>
/// A two-joint rig with hand-sized clips: root at the origin and a child one unit up.
/// Clips run at one frame a second, so a clip with three frames lasts two seconds and
/// times can be worked out by hand.
/// </summary>
internal static class TestRig
{
    public const string Root = "root";
    public const string Child = "child";

    public static JointPose RootRest => JointPose.Identity;

    public static JointPose ChildRest => new(new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.One);

    public static Skeleton Skeleton() =>
        new(
            [Root, Child],
            [-1, 0],
            [RootRest, ChildRest],
            [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, -1f, 0f)],
            Matrix4x4.Identity);

    /// <summary>Two frames, both at rest, so it lasts one second and moves nothing.</summary>
    public static AnimationClip Idle() => new("idle", 1f, 2, [RootRest, ChildRest, RootRest, ChildRest]);

    /// <summary>The root slides along x: 0, 2, 4 over two seconds.</summary>
    public static AnimationClip Move() => new("move", 1f, 2,
    [
        RootAt(0f), ChildRest,
        RootAt(2f), ChildRest,
        RootAt(4f), ChildRest,
    ]);

    /// <summary>The child slides along x: 0, 2, 4 over two seconds, staying one unit up.</summary>
    public static AnimationClip Wave() => new("wave", 1f, 2,
    [
        RootRest, ChildAt(0f),
        RootRest, ChildAt(2f),
        RootRest, ChildAt(4f),
    ]);

    public static Rig Rig() => new(Skeleton(), [Idle(), Move(), Wave()]);

    public static JointPose RootAt(float x) => new(new Vector3(x, 0f, 0f), Quaternion.Identity, Vector3.One);

    public static JointPose ChildAt(float x) => new(new Vector3(x, 1f, 0f), Quaternion.Identity, Vector3.One);

    /// <summary>The local joint poses a pose gives, with no time past the tick.</summary>
    public static JointPose[] Evaluate(AnimationPose pose, float ahead = 0f)
    {
        var joints = new JointPose[2];
        pose.Evaluate(ahead, joints, new JointPose[2]);
        return joints;
    }
}
