using Rendition;

namespace Super.Tests.Animation;

public class RigTests
{
    private const float Tolerance = 0.0001f;

    [Fact]
    public void Clip_finds_a_clip_by_name()
    {
        var rig = TestRig.Rig();

        Assert.Equal(new ClipRef(1), rig.Clip("move"));
        Assert.True(rig.HasClip("wave"));
        Assert.False(rig.HasClip("fly"));
    }

    [Fact]
    public void Clip_explains_an_unknown_name_by_listing_the_clips()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() => rig.Clip("fly"));

        Assert.Contains("no clip called fly", problem.Message);
        Assert.Contains("idle, move, wave", problem.Message);
    }

    [Fact]
    public void A_clip_with_the_wrong_joint_count_is_refused()
    {
        var oneJoint = new AnimationClip("short", 1f, 1, [JointPose.Identity]);

        var problem = Assert.Throws<ArgumentException>(() => new Rig(TestRig.Skeleton(), [oneJoint]));

        Assert.Contains("short has 1 joints, but the skeleton has 2", problem.Message);
    }

    [Fact]
    public void Mask_covers_a_joint_and_everything_under_it()
    {
        var rig = TestRig.Rig();

        Assert.Equal([0f, 1f], rig.Mask(TestRig.Child).Weights);
        Assert.Equal([1f, 1f], rig.Mask(TestRig.Root).Weights);
    }

    [Fact]
    public void Mask_explains_an_unknown_joint_by_listing_the_joints()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() => rig.Mask("tail"));

        Assert.Contains("no joint called tail", problem.Message);
        Assert.Contains("root, child", problem.Message);
    }

    [Fact]
    public void Blend_picks_the_clips_around_the_parameter()
    {
        var rig = TestRig.Rig();
        var motion = rig.Blend((rig.Clip("idle"), 0f), (rig.Clip("move"), 4f), (rig.Clip("wave"), 8f));

        Assert.Equal((0, 1, 0.5f), motion.Pick(2f));
        Assert.Equal((1, 2, 0.25f), motion.Pick(5f));
        Assert.Equal((0, 0, 0f), motion.Pick(-3f));
        Assert.Equal((2, 2, 0f), motion.Pick(20f));
    }

    [Fact]
    public void Blend_points_must_rise()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() => rig.Blend((rig.Clip("idle"), 4f), (rig.Clip("move"), 4f)));

        Assert.Contains("must rise", problem.Message);
    }

    [Fact]
    public void Blend_needs_two_clips()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() => rig.Blend((rig.Clip("idle"), 0f)));

        Assert.Contains("at least two clips", problem.Message);
    }

    [Fact]
    public void An_empty_pose_is_the_rest_pose()
    {
        var pose = new AnimationPose(TestRig.Rig());

        var joints = TestRig.Evaluate(pose);

        Assert.Equal(0f, joints[0].Position.X);
        Assert.Equal(1f, joints[1].Position.Y);
    }

    [Fact]
    public void A_layer_plays_its_clip_ahead_of_the_tick_at_its_speed()
    {
        var pose = new AnimationPose(TestRig.Rig());
        pose.Add(new PoseLayer(Clip: 1, Time: 0.5f, Speed: 2f, Amount: 1f, Loops: true, Mask: null));

        // 0.5 s plus 0.25 s at double speed is 1 s into the move, where x is 2.
        Assert.Equal(2f, TestRig.Evaluate(pose, ahead: 0.25f)[0].Position.X, Tolerance);
    }

    [Fact]
    public void A_looping_layer_wraps_past_the_end()
    {
        var pose = new AnimationPose(TestRig.Rig());
        pose.Add(new PoseLayer(Clip: 1, Time: 1.5f, Speed: 1f, Amount: 1f, Loops: true, Mask: null));

        // 2.5 s into a 2 s clip is 0.5 s in, where x is 1.
        Assert.Equal(1f, TestRig.Evaluate(pose, ahead: 1f)[0].Position.X, Tolerance);
    }

    [Fact]
    public void A_one_shot_layer_holds_its_last_frame()
    {
        var pose = new AnimationPose(TestRig.Rig());
        pose.Add(new PoseLayer(Clip: 1, Time: 1.5f, Speed: 1f, Amount: 1f, Loops: false, Mask: null));

        Assert.Equal(4f, TestRig.Evaluate(pose, ahead: 1f)[0].Position.X, Tolerance);
    }

    [Fact]
    public void Layers_blend_in_order_by_their_amount_and_mask()
    {
        var rig = TestRig.Rig();
        var pose = new AnimationPose(rig);
        pose.Add(new PoseLayer(Clip: 1, Time: 1f, Speed: 1f, Amount: 0.5f, Loops: true, Mask: null));
        pose.Add(new PoseLayer(Clip: 2, Time: 2f, Speed: 1f, Amount: 0.5f, Loops: false, Mask: rig.Mask(TestRig.Child)));

        var joints = TestRig.Evaluate(pose);

        // Halfway from rest (0) to the move at 1 s (2), and the child halfway to the wave's end (4).
        Assert.Equal(1f, joints[0].Position.X, Tolerance);
        Assert.Equal(2f, joints[1].Position.X, Tolerance);
    }

    [Fact]
    public void Layers_with_no_amount_are_left_out()
    {
        var pose = new AnimationPose(TestRig.Rig());

        pose.Add(new PoseLayer(Clip: 1, Time: 0f, Speed: 1f, Amount: 0f, Loops: true, Mask: null));

        Assert.Equal(0, pose.LayerCount);
    }

    [Fact]
    public void A_mask_from_another_rig_is_refused()
    {
        var pose = new AnimationPose(TestRig.Rig());
        var other = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() =>
            pose.Add(new PoseLayer(Clip: 1, Time: 0f, Speed: 1f, Amount: 1f, Loops: true, Mask: other.Mask(TestRig.Child))));

        Assert.Contains("another rig", problem.Message);
    }
}
