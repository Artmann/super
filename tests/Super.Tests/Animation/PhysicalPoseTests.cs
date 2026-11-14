using System.Numerics;
using Rendition;

namespace Super.Tests.Animation;

/// <summary>Joints physics holds, such as a ragdoll's, laid over the clips with SetPhysical.</summary>
public class PhysicalPoseTests
{
    private const float Tick = 0.1f;

    private static Quaternion AboutZ(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    private static JointPose At(float x, Quaternion turn) => new(new Vector3(x, 0f, 0f), turn, Vector3.One);

    private static void AssertTurn(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 0.9999f, $"expected {expected} but got {actual}");

    /// <summary>A pose of the two-joint rig holding the root at x 0, then 2, and the child turned 0, then 90 degrees.</summary>
    private static AnimationPose Held(float childWeight = 1f, int placed = 0)
    {
        var pose = new AnimationPose(TestRig.Rig());
        pose.SetPhysical(
            [At(0f, Quaternion.Identity), At(0f, Quaternion.Identity)],
            [At(2f, Quaternion.Identity), new(new Vector3(5f, 5f, 5f), AboutZ(90f), Vector3.One)],
            [1f, childWeight],
            placed,
            Tick);
        return pose;
    }

    [Fact]
    public void Between_ticks_the_held_joints_move_from_the_tick_before_to_the_last()
    {
        var joints = TestRig.Evaluate(Held(), ahead: Tick / 2f);

        Assert.Equal(1f, joints[0].Position.X, 4);
        AssertTurn(AboutZ(45f), joints[1].Rotation);
    }

    [Fact]
    public void The_latest_pose_is_the_last_tick_s()
    {
        var pose = Held();
        var joints = new JointPose[2];

        pose.Evaluate(0f, joints, new JointPose[2], 1f);

        Assert.Equal(2f, joints[0].Position.X, 4);
        AssertTurn(AboutZ(90f), joints[1].Rotation);
    }

    [Fact]
    public void Only_the_placed_joint_moves_so_bones_keep_their_length()
    {
        var joints = TestRig.Evaluate(Held(), ahead: Tick);

        // The child is held at (5, 5, 5), but only its turn is taken; it stays one unit above the root.
        Assert.Equal(TestRig.ChildRest.Position, joints[1].Position);
    }

    [Fact]
    public void Nothing_placed_leaves_every_joint_where_the_clips_have_it()
    {
        var joints = TestRig.Evaluate(Held(placed: -1), ahead: Tick);

        Assert.Equal(Vector3.Zero, joints[0].Position);
    }

    [Fact]
    public void A_joint_turns_relative_to_its_held_parent()
    {
        var pose = new AnimationPose(TestRig.Rig());
        JointPose[] held = [At(0f, AboutZ(90f)), At(0f, AboutZ(120f))];
        pose.SetPhysical(held, held, [1f, 1f], 0, Tick);

        var joints = TestRig.Evaluate(pose);

        AssertTurn(AboutZ(90f), joints[0].Rotation);
        AssertTurn(AboutZ(30f), joints[1].Rotation);
    }

    [Fact]
    public void A_weight_mixes_the_clips_with_physics()
    {
        var joints = TestRig.Evaluate(Held(childWeight: 0.5f), ahead: Tick);

        AssertTurn(AboutZ(45f), joints[1].Rotation);
    }

    [Fact]
    public void Clearing_hands_the_joints_back_to_the_clips()
    {
        var pose = Held();
        int version = pose.Version;

        pose.ClearPhysical();
        var joints = TestRig.Evaluate(pose, ahead: Tick);

        Assert.False(pose.IsPhysical);
        Assert.NotEqual(version, pose.Version);
        Assert.Equal(Vector3.Zero, joints[0].Position);
        AssertTurn(Quaternion.Identity, joints[1].Rotation);
    }
}
