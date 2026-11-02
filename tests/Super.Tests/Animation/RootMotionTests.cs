using System.Numerics;
using Rendition;

namespace Super.Tests.Animation;

public class RootMotionTests
{
    private const float Tolerance = 0.0001f;

    private enum Stance
    {
        Idle,
        Move,
    }

    private enum Deed
    {
        Wave,
        Dash,
        Shuffle,
    }

    private static Animator Start(Rig rig, Stance stance = Stance.Move) =>
        new AnimationController<Stance, Deed>(rig)
            .State(Stance.Idle, rig.Clip("idle"))
            .State(Stance.Move, rig.Clip("move"))
            .Action(Deed.Wave, rig.Clip("wave"), rig.Mask(TestRig.Child), fadeSeconds: 0f)
            .Action(Deed.Dash, rig.Clip("move"), fadeSeconds: 0f)
            .Action(Deed.Shuffle, rig.Clip("move"), rig.Mask(TestRig.Child), fadeSeconds: 0f)
            .Start(stance);

    /// <summary>A one-joint rig whose clip moves the root by these positions, one frame a second.</summary>
    private static Rig OneJoint(Matrix4x4 rootTransform, params Vector3[] positions)
    {
        var skeleton = new Skeleton(["root"], [-1], [JointPose.Identity], [Matrix4x4.Identity], rootTransform);
        var frames = positions.Select(at => new JointPose(at, Quaternion.Identity, Vector3.One)).ToArray();
        return new Rig(skeleton, [new AnimationClip("clip", 1f, 1, frames)]);
    }

    [Fact]
    public void The_travel_leaves_the_clip_and_is_kept_by_the_rig()
    {
        var rig = TestRig.Rig();

        rig.RootMotion(TestRig.Root);

        var move = rig.Clips[rig.Clip("move").Index];
        var pose = new JointPose[2];
        move.Sample(1f, pose);
        Assert.Equal(Vector3.Zero, pose[0].Position);
        Assert.Equal(new Vector3(2f, 0f, 0f), rig.Travel(rig.Clip("move").Index, 1f));
        Assert.Equal(new Vector3(3f, 0f, 0f), rig.Travel(rig.Clip("move").Index, 1.5f));
        Assert.Equal(TestRig.Root, rig.RootMotionJoint);
    }

    [Fact]
    public void Height_stays_in_the_clip()
    {
        var rig = OneJoint(Matrix4x4.Identity, new(0f, 0f, 0f), new(1f, 1f, 3f), new(2f, 0f, 0f));

        rig.RootMotion("root");

        var frame = rig.Clips[0].Frames[1];
        Assert.Equal(new Vector3(0f, 1f, 0f), frame.Position);
        Assert.Equal(new Vector3(1f, 0f, 3f), rig.Travel(0, 1f));
    }

    [Fact]
    public void Travel_is_measured_after_the_root_transform()
    {
        // Turned a quarter round y and doubled, a step along x lands two units along -z.
        var rig = OneJoint(
            Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(MathF.PI / 2f),
            new(0f, 0f, 0f), new(1f, 0f, 0f));

        rig.RootMotion("root");

        var travel = rig.Travel(0, 1f);
        Assert.Equal(0f, travel.X, Tolerance);
        Assert.Equal(-2f, travel.Z, Tolerance);
        Assert.Equal(0f, rig.Clips[0].Frames[1].Position.Length(), Tolerance);
    }

    [Fact]
    public void A_root_transform_that_stands_the_model_up_makes_its_z_the_height()
    {
        // A model made with z up is stood up by its root transform, so its z is height and stays.
        var rig = OneJoint(Matrix4x4.CreateRotationX(-MathF.PI / 2f), new(0f, 0f, 0f), new(0f, 0f, 1f));

        rig.RootMotion("root");

        Assert.Equal(0f, rig.Travel(0, 1f).Length(), Tolerance);
        Assert.Equal(0f, Vector3.Distance(new Vector3(0f, 0f, 1f), rig.Clips[0].Frames[1].Position), Tolerance);
    }

    [Fact]
    public void The_animator_reports_the_travel_of_the_last_tick()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);
        var animator = Start(rig);

        animator.Advance(0.5f);

        Assert.Equal(new Vector3(1f, 0f, 0f), animator.RootMotion);
        Assert.Equal(0f, TestRig.Evaluate(animator.Pose)[0].Position.X, Tolerance);
    }

    [Fact]
    public void A_looping_state_carries_on_across_its_wrap()
    {
        // Two units a second: 2.5 s is one whole lap of four and one unit more.
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);
        var animator = Start(rig);
        animator.Advance(1.5f);

        animator.Advance(1f);

        Assert.Equal(2f, animator.RootMotion.X, Tolerance);
    }

    [Fact]
    public void Speed_scales_the_travel()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);
        var animator = Start(rig);
        animator.Speed = 2f;

        animator.Advance(0.25f);

        Assert.Equal(1f, animator.RootMotion.X, Tolerance);
    }

    [Fact]
    public void An_action_over_the_whole_body_moves_the_character()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);
        var animator = Start(rig, Stance.Idle);
        animator.Play(Deed.Dash);

        animator.Advance(0.5f);

        Assert.Equal(1f, animator.RootMotion.X, Tolerance);
    }

    [Fact]
    public void An_action_ends_where_its_clip_does_and_carries_no_further()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);
        var animator = Start(rig, Stance.Idle);
        animator.Play(Deed.Dash);
        animator.Advance(1.5f);

        animator.Advance(1f);

        Assert.Equal(1f, animator.RootMotion.X, Tolerance);
    }

    [Fact]
    public void An_action_masked_off_the_root_moves_nothing()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);
        var animator = Start(rig, Stance.Idle);
        animator.Play(Deed.Shuffle);

        animator.Advance(0.5f);

        Assert.Equal(Vector3.Zero, animator.RootMotion);
    }

    [Fact]
    public void Without_root_motion_the_animator_reports_none_and_the_clip_moves_the_mesh()
    {
        var animator = Start(TestRig.Rig());

        animator.Advance(0.5f);

        Assert.Equal(Vector3.Zero, animator.RootMotion);
        Assert.Equal(1f, TestRig.Evaluate(animator.Pose)[0].Position.X, Tolerance);
    }

    [Fact]
    public void Asking_again_for_the_same_joint_keeps_the_travel()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);

        rig.RootMotion(TestRig.Root);

        Assert.Equal(new Vector3(2f, 0f, 0f), rig.Travel(rig.Clip("move").Index, 1f));
    }

    [Fact]
    public void Asking_for_a_second_joint_is_refused()
    {
        var rig = TestRig.Rig();
        rig.RootMotion(TestRig.Root);

        var problem = Assert.Throws<InvalidOperationException>(() => rig.RootMotion(TestRig.Child));

        Assert.Equal("The rig already takes its root motion from root, so it can't take it from child too.", problem.Message);
    }

    [Fact]
    public void An_unknown_joint_is_explained()
    {
        var problem = Assert.Throws<ArgumentException>(() => TestRig.Rig().RootMotion("hips"));

        Assert.StartsWith("The rig has no joint called hips. It has: root, child.", problem.Message);
    }

    [Fact]
    public void Joint_says_where_a_joint_is_in_the_model_this_tick()
    {
        var animator = Start(TestRig.Rig());
        animator.Advance(0.5f);
        Assert.Equal(new Vector3(1f, 1f, 0f), animator.Joint(TestRig.Child).Translation);

        animator.Advance(0.5f);

        Assert.Equal(new Vector3(2f, 1f, 0f), animator.Joint(TestRig.Child).Translation);
    }

    [Fact]
    public void Joint_includes_the_root_transform()
    {
        var skeleton = new Skeleton(
            [TestRig.Root, TestRig.Child],
            [-1, 0],
            [TestRig.RootRest, TestRig.ChildRest],
            [Matrix4x4.Identity, Matrix4x4.Identity],
            Matrix4x4.CreateScale(2f));
        var rig = new Rig(skeleton, [TestRig.Idle(), TestRig.Move(), TestRig.Wave()]);
        var animator = Start(rig, Stance.Idle);

        Assert.Equal(new Vector3(0f, 2f, 0f), animator.Joint(TestRig.Child).Translation);
    }

    [Fact]
    public void Joint_follows_a_change_of_state_before_the_next_tick()
    {
        var rig = TestRig.Rig();
        var animator = new AnimationController<Stance, Deed>(rig) { CrossFadeSeconds = 0f }
            .State(Stance.Idle, rig.Clip("idle"))
            .State(Stance.Move, rig.Clip("move"))
            .Start(Stance.Idle);
        animator.Advance(0.5f);
        Assert.Equal(new Vector3(0f, 1f, 0f), animator.Joint(TestRig.Child).Translation);

        // Halfway through idle's cycle is halfway through the move's: one second in, at x 2.
        animator.Set(Stance.Move);

        Assert.Equal(new Vector3(2f, 1f, 0f), animator.Joint(TestRig.Child).Translation);
    }

    [Fact]
    public void An_unknown_joint_name_is_explained()
    {
        var animator = Start(TestRig.Rig());

        var problem = Assert.Throws<ArgumentException>(() => animator.Joint("hand"));

        Assert.StartsWith("The rig has no joint called hand. It has: root, child.", problem.Message);
    }
}
