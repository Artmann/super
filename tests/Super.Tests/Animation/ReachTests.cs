using System.Numerics;
using Rendition;

namespace Super.Tests.Animation;

public class ReachTests
{
    private const float Close = 0.001f;

    // A body with an arm along +x, bent back at the elbow, holding a sword half a unit long.
    // The model is turned a quarter round y and doubled, so the model's space isn't the skeleton's.
    private static readonly Matrix4x4 Root = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(MathF.PI / 2f);

    private static Rig Arm()
    {
        var bent = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
        JointPose[] rest =
        [
            JointPose.Identity,
            new(new Vector3(0f, 1.5f, 0f), Quaternion.Identity, Vector3.One),
            new(new Vector3(1f, 0f, 0f), bent, Vector3.One),
            new(new Vector3(1f, 0f, 0f), Quaternion.Inverse(bent), Vector3.One),
            new(new Vector3(0.1f, 0f, 0f), Quaternion.Identity, Vector3.One),
        ];
        var skeleton = new Skeleton(["body", "shoulder", "elbow", "wrist", "sword"], [-1, 0, 1, 2, 3], rest,
            [.. Enumerable.Repeat(Matrix4x4.Identity, 5)], Root);
        var swing = new AnimationClip("swing", 1f, 5, [.. rest, .. rest.Select((pose, i) => i == 1
            ? pose with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f) }
            : pose)]);
        return new Rig(skeleton, [swing]);
    }

    [Fact]
    public void Reach_brings_the_held_point_to_the_goal()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        var limb = rig.Limb("shoulder", "elbow", "wrist", tip: "sword");
        var point = new Vector3(0.4f, 0f, 0f);
        var goal = new Vector3(1.2f, 4f, -1.5f);
        var handBefore = Turn(Model(pose)[3]);

        pose.Reach(limb, goal, point: point);
        var joints = Model(pose);

        Assert.True(Vector3.Distance(goal, Vector3.Transform(point, joints[4])) < Close, $"the point is at {Vector3.Transform(point, joints[4])}");

        // The hand keeps the way it was turned, so the sword is brought over and not twisted.
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(handBefore, Turn(joints[3]))), 3);
    }

    [Fact]
    public void Reach_keeps_the_elbow_bent_the_way_it_was()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        var limb = rig.Limb("shoulder", "elbow", "wrist");
        var before = Model(pose);
        var goal = Vector3.Lerp(before[1].Translation, before[3].Translation, 0.8f) + new Vector3(0f, 0.3f, 0f);

        pose.Reach(limb, goal);
        var after = Model(pose);

        Assert.True(Vector3.Distance(goal, after[3].Translation) < Close);
        Assert.True(Vector3.Dot(Bend(before), Bend(after)) > 0f, "the elbow folded the other way");
    }

    [Fact]
    public void A_goal_out_of_reach_stretches_the_arm_towards_it()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        var limb = rig.Limb("shoulder", "elbow", "wrist");
        var shoulder = Model(pose)[1].Translation;
        var goal = shoulder + new Vector3(0f, 0f, 30f);

        pose.Reach(limb, goal);
        var wrist = Model(pose)[3].Translation;

        // Both bones are 2 long in the model, so the wrist ends nearly 4 out along the way to the goal.
        Assert.True(Vector3.Distance(shoulder + new Vector3(0f, 0f, 4f), wrist) < 0.01f, $"the wrist is at {wrist}");
    }

    [Fact]
    public void A_weight_mixes_from_the_clip_to_the_goal_and_none_takes_the_reach_away()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        var limb = rig.Limb("shoulder", "elbow", "wrist");
        var rest = Model(pose)[3].Translation;
        var goal = rest + new Vector3(0f, 1.5f, 0f);

        pose.Reach(limb, goal, weight: 0.5f);
        var half = Model(pose)[3].Translation;
        Assert.InRange(Vector3.Distance(rest, half), 0.2f, 1.3f);
        Assert.InRange(Vector3.Distance(goal, half), 0.2f, 1.3f);

        pose.Reach(limb, goal, weight: 0f);
        Assert.True(Vector3.Distance(rest, Model(pose)[3].Translation) < Close);
    }

    [Fact]
    public void A_turn_swings_a_joint_and_all_under_it_about_the_joint_in_the_models_space()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        var before = Model(pose);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        pose.Turn("shoulder", turn);
        var after = Model(pose);

        var shoulder = before[1].Translation;
        Assert.True(Vector3.Distance(shoulder, after[1].Translation) < Close);
        var expected = shoulder + Vector3.Transform(before[4].Translation - shoulder, turn);
        Assert.True(Vector3.Distance(expected, after[4].Translation) < Close, $"the sword is at {after[4].Translation}, not {expected}");

        pose.Turn("shoulder", Quaternion.Identity);
        Assert.True(Vector3.Distance(before[4].Translation, Model(pose)[4].Translation) < Close);
    }

    [Fact]
    public void Clearing_the_joints_takes_turns_and_reaches_away()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        var rest = Model(pose)[4].Translation;

        pose.Turn("elbow", Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f));
        pose.Reach(rig.Limb("shoulder", "elbow", "wrist"), new Vector3(0f, 5f, 0f));
        pose.ClearJoints();

        Assert.True(Vector3.Distance(rest, Model(pose)[4].Translation) < Close);
    }

    [Fact]
    public void An_animator_sees_a_reach_in_its_joints_straight_away()
    {
        var rig = Arm();
        var animator = new AnimationController<Still, Act>(rig).State(Still.Idle, rig.Clip("swing")).Start(Still.Idle);
        var before = animator.Joint("wrist").Translation;
        var goal = before + new Vector3(0f, 0.5f, 0f);

        animator.Pose.Reach(rig.Limb("shoulder", "elbow", "wrist"), goal);

        Assert.True(Vector3.Distance(goal, animator.Joint("wrist").Translation) < Close);
    }

    [Fact]
    public void A_rig_says_where_a_joint_is_at_a_moment_of_a_clip()
    {
        var rig = Arm();
        var pose = new AnimationPose(rig);
        pose.Add(new PoseLayer(0, Time: 0.5f, Speed: 1f, Amount: 1f, Loops: false, Mask: null));

        var expected = Model(pose)[4].Translation;

        Assert.True(Vector3.Distance(expected, rig.Joint(rig.Clip("swing"), 0.5f, "sword").Translation) < Close);
    }

    [Fact]
    public void A_limb_must_run_down_the_skeleton()
    {
        var rig = Arm();

        var problem = Assert.Throws<ArgumentException>(() => rig.Limb("elbow", "shoulder", "wrist"));

        Assert.Contains("shoulder isn't under elbow", problem.Message);
        Assert.Throws<ArgumentException>(() => rig.Limb("shoulder", "elbow", "hand"));
        Assert.Throws<ArgumentException>(() => new AnimationPose(rig).Reach(Arm().Limb("shoulder", "elbow", "wrist"), Vector3.Zero));
    }

    /// <summary>Each joint in the model, as drawn.</summary>
    private static Matrix4x4[] Model(AnimationPose pose)
    {
        var skeleton = pose.Rig.Skeleton;
        var locals = new JointPose[skeleton.Count];
        pose.Evaluate(0f, locals, new JointPose[skeleton.Count]);
        var globals = new Matrix4x4[skeleton.Count];
        for (int i = 0; i < globals.Length; i++)
        {
            globals[i] = skeleton.Parents[i] >= 0 ? locals[i].Matrix * globals[skeleton.Parents[i]] : locals[i].Matrix;
        }

        return [.. globals.Select(global => global * skeleton.RootTransform)];
    }

    private static Quaternion Turn(Matrix4x4 joint)
    {
        Matrix4x4.Decompose(joint, out _, out var turn, out _);
        return turn;
    }

    /// <summary>Which way the elbow sticks out from the line between shoulder and wrist.</summary>
    private static Vector3 Bend(Matrix4x4[] joints)
    {
        var line = Vector3.Normalize(joints[3].Translation - joints[1].Translation);
        var out_ = joints[2].Translation - joints[1].Translation;
        return out_ - (Vector3.Dot(out_, line) * line);
    }

    private enum Still
    {
        Idle,
    }

    private enum Act
    {
        None,
    }
}
