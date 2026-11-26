using System.Numerics;
using Rendition;

namespace Super.Tests.Animation;

/// <summary>
/// Turns taken out of clips by root motion. Clips run at one frame a second on a one-joint rig, so turns and travel
/// can be worked out by hand. A quarter turn about y carries x onto -z, as <see cref="Matrix4x4.CreateRotationY(float)"/> does.
/// </summary>
public class RootTurnTests
{
    private const float Tolerance = 0.0001f;
    private const float Quarter = MathF.PI / 2f;

    private enum Stance
    {
        Turn,
    }

    private enum Deed
    {
        None,
    }

    private static JointPose At(Vector3 position, float yaw) =>
        new(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), Vector3.One);

    /// <summary>A one-joint rig with one clip per array of frames, named clip0, clip1 and so on.</summary>
    private static Rig OneJoint(Matrix4x4 rootTransform, params JointPose[][] clips)
    {
        var skeleton = new Skeleton(["root"], [-1], [JointPose.Identity], [Matrix4x4.Identity], rootTransform);
        return new Rig(skeleton, [.. clips.Select((frames, i) => new AnimationClip($"clip{i}", 1f, 1, frames))]);
    }

    /// <summary>A quarter turn on the spot over a second: the classic turn clip.</summary>
    private static JointPose[] TurnOnTheSpot() => [At(Vector3.Zero, 0f), At(Vector3.Zero, Quarter)];

    /// <summary>A step along x that turns a quarter as it goes: one side of a square, a lap of a curved run.</summary>
    private static JointPose[] Corner() => [At(Vector3.Zero, 0f), At(new Vector3(1f, 0f, 0f), Quarter)];

    private static Animator Play(Rig rig) =>
        new AnimationController<Stance, Deed>(rig)
            .State(Stance.Turn, rig.Clip("clip0"))
            .Start(Stance.Turn);

    [Fact]
    public void A_turning_clip_keeps_its_turn_and_faces_the_first_way()
    {
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot());

        rig.RootMotion("root", rig.Clip("clip0"));

        Assert.Equal(Quarter, rig.RootTravel!.TurnAt(0, 1f), Tolerance);
        Assert.Equal(Quarter / 2f, rig.RootTravel!.TurnAt(0, 0.5f), Tolerance);
        var last = rig.Clips[0].Frames[1].Rotation;
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(last, Quaternion.Identity)), Tolerance);
    }

    [Fact]
    public void A_clip_not_named_keeps_its_turn_in_the_pose()
    {
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot());

        rig.RootMotion("root");

        Assert.Equal(0f, rig.RootTravel!.TurnAt(0, 1f));
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter);
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(rig.Clips[0].Frames[1].Rotation, expected)), Tolerance);
    }

    [Fact]
    public void A_tilt_stays_in_the_pose_and_only_the_turn_comes_out()
    {
        // Leaning forward a little while turning a quarter: the lean is left.
        var lean = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Quarter);
        var rig = OneJoint(Matrix4x4.Identity, [At(Vector3.Zero, 0f), new JointPose(Vector3.Zero, turn * lean, Vector3.One)]);

        rig.RootMotion("root", rig.Clip("clip0"));

        Assert.Equal(Quarter, rig.RootTravel!.TurnAt(0, 1f), Tolerance);
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(rig.Clips[0].Frames[1].Rotation, lean)), Tolerance);
    }

    [Fact]
    public void A_turn_past_once_round_keeps_counting()
    {
        // A quarter a second for five seconds: 450 degrees, which an angle read from each frame alone takes for -270.
        var frames = Enumerable.Range(0, 6).Select(f => At(Vector3.Zero, f * Quarter)).ToArray();
        var rig = OneJoint(Matrix4x4.Identity, frames);

        rig.RootMotion("root", rig.Clip("clip0"));

        Assert.Equal(5f * Quarter, rig.RootTravel!.TurnAt(0, 5f), Tolerance);
    }

    [Fact]
    public void A_turn_about_a_spot_off_the_origin_moves_nothing()
    {
        // The joint stands a unit ahead of the origin and the model turns a quarter about the origin, which carries
        // it from (0, 0, 1) to (1, 0, 0): all of that is the turn, none of it travel.
        var rig = OneJoint(Matrix4x4.Identity, [At(new Vector3(0f, 0f, 1f), 0f), At(new Vector3(1f, 0f, 0f), Quarter)]);

        rig.RootMotion("root", rig.Clip("clip0"));

        Assert.Equal(0f, rig.RootTravel!.At(0, 1f).Length(), Tolerance);
        Assert.Equal(0f, Vector3.Distance(new Vector3(0f, 0f, 1f), rig.Clips[0].Frames[1].Position), Tolerance);
    }

    [Fact]
    public void The_turn_is_measured_after_the_root_transform()
    {
        // A model made with z up is stood up by its root transform, so a turn about its z is a turn about up.
        var standUp = Matrix4x4.CreateRotationX(-Quarter);
        var aboutZ = new JointPose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Quarter), Vector3.One);
        var rig = OneJoint(standUp, [JointPose.Identity, aboutZ]);

        rig.RootMotion("root", rig.Clip("clip0"));

        Assert.Equal(Quarter, rig.RootTravel!.TurnAt(0, 1f), Tolerance);
        Assert.Equal(1f, MathF.Abs(Quaternion.Dot(rig.Clips[0].Frames[1].Rotation, Quaternion.Identity)), Tolerance);
    }

    [Fact]
    public void The_animator_reports_the_turn_of_the_last_tick()
    {
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot());
        rig.RootMotion("root", rig.Clip("clip0"));
        var animator = Play(rig);

        animator.Advance(0.5f);

        Assert.Equal(Quarter / 2f, animator.RootTurn, Tolerance);
        Assert.Equal(Vector3.Zero, animator.RootMotion);
    }

    [Fact]
    public void A_turning_loop_reports_each_lap_in_the_facing_it_began()
    {
        // Each lap goes one unit forward and turns a quarter, so in her own facing every lap is the same.
        var rig = OneJoint(Matrix4x4.Identity, Corner());
        rig.RootMotion("root", rig.Clip("clip0"));
        var animator = Play(rig);
        animator.Advance(1f);
        Assert.Equal(0f, Vector3.Distance(new Vector3(1f, 0f, 0f), animator.RootMotion), Tolerance);

        animator.Advance(1f);

        Assert.Equal(0f, Vector3.Distance(new Vector3(1f, 0f, 0f), animator.RootMotion), Tolerance);
        Assert.Equal(Quarter, animator.RootTurn, Tolerance);
    }

    [Fact]
    public void Laps_worked_out_at_once_go_round_a_square()
    {
        // Three laps from the start: forward along x, then -z, then -x, facing three quarters round.
        var rig = OneJoint(Matrix4x4.Identity, Corner());
        rig.RootMotion("root", rig.Clip("clip0"));

        var step = rig.RootTravel!.Over(0, 0f, 1f, loops: true, 3f);

        Assert.Equal(0f, Vector3.Distance(new Vector3(0f, 0f, -1f), step.Travel), Tolerance);
        Assert.Equal(3f * Quarter, step.Turn, Tolerance);
    }

    [Fact]
    public void A_character_moved_by_the_steps_ends_where_the_clip_went()
    {
        // Moving by each tick's travel in her facing, then turning, retraces three sides of the square.
        var rig = OneJoint(Matrix4x4.Identity, Corner());
        rig.RootMotion("root", rig.Clip("clip0"));
        var animator = Play(rig);
        var position = Vector3.Zero;
        float facing = 0f;

        for (int tick = 0; tick < 30; tick++)
        {
            animator.Advance(0.1f);
            position += Vector3.Transform(animator.RootMotion, Matrix4x4.CreateRotationY(facing));
            facing += animator.RootTurn;
        }

        Assert.Equal(0f, Vector3.Distance(new Vector3(0f, 0f, -1f), position), 0.01f);
        Assert.Equal(3f * Quarter, facing, Tolerance);
    }

    [Fact]
    public void A_blend_of_turning_clips_turns_by_their_shares()
    {
        // A quarter turn a second, with a half turn a second over it at a quarter share, for half a second:
        // 3/4 of pi/4 and 1/4 of pi/2.
        var faster = new[] { At(Vector3.Zero, 0f), At(Vector3.Zero, 2f * Quarter) };
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot(), faster);
        rig.RootMotion("root", rig.Clip("clip0"), rig.Clip("clip1"));
        var pose = new AnimationPose(rig);
        pose.Add(new PoseLayer(0, 0f, 1f, 1f, false, null));
        pose.Add(new PoseLayer(1, 0f, 1f, 0.25f, false, null));

        var step = pose.StepOver(0.5f);

        Assert.Equal(5f * MathF.PI / 16f, step.Turn, Tolerance);
    }

    [Fact]
    public void Asking_again_with_the_same_clips_does_nothing()
    {
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot());
        rig.RootMotion("root", rig.Clip("clip0"));

        rig.RootMotion("root", rig.Clip("clip0"));

        Assert.Equal(Quarter, rig.RootTravel!.TurnAt(0, 1f), Tolerance);
    }

    [Fact]
    public void Asking_again_with_other_turning_clips_is_refused()
    {
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot());
        rig.RootMotion("root");

        var problem = Assert.Throws<InvalidOperationException>(() => rig.RootMotion("root", rig.Clip("clip0")));

        Assert.Equal("The rig already takes its root motion from root with other clips turning, so name every turning clip in one call.", problem.Message);
    }

    [Fact]
    public void A_clip_from_past_the_rig_is_refused()
    {
        var rig = OneJoint(Matrix4x4.Identity, TurnOnTheSpot());

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => rig.RootMotion("root", new ClipRef(3)));

        Assert.StartsWith("The rig has 1 clips, so it has no clip 3 to turn with.", problem.Message);
    }
}
