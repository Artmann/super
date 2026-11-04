namespace Super.Tests.Animation;

public class AnimatorTests
{
    private const float Tolerance = 0.0001f;

    private enum Stance
    {
        Idle,
        Move,
        Blended,
        Unset,
    }

    private enum Deed
    {
        Wave,
        FadedWave,
        Unset,
    }

    private enum Other
    {
        Thing,
    }

    private static AnimationController<Stance, Deed> Controller(float crossFade = 0f)
    {
        var rig = TestRig.Rig();
        return new AnimationController<Stance, Deed>(rig) { CrossFadeSeconds = crossFade }
            .State(Stance.Idle, rig.Clip("idle"))
            .State(Stance.Move, rig.Clip("move"))
            .State(Stance.Blended, rig.Blend((rig.Clip("idle"), 0f), (rig.Clip("move"), 4f)))
            .Action(Deed.Wave, rig.Clip("wave"), rig.Mask(TestRig.Child), fadeSeconds: 0f)
            .Action(Deed.FadedWave, rig.Clip("wave"), fadeSeconds: 0.5f);
    }

    private static float RootX(Animator animator) => TestRig.Evaluate(animator.Pose)[0].Position.X;

    private static float ChildX(Animator animator) => TestRig.Evaluate(animator.Pose)[1].Position.X;

    [Fact]
    public void Starts_in_its_state_at_the_start_of_the_cycle()
    {
        var animator = Controller().Start(Stance.Move);

        Assert.True(animator.IsIn(Stance.Move));
        Assert.Equal(0f, animator.Phase);
        Assert.Equal(1, animator.Pose.LayerCount);
        Assert.Equal(0f, RootX(animator));
    }

    [Fact]
    public void Advancing_plays_the_state_clip()
    {
        var animator = Controller().Start(Stance.Move);

        animator.Advance(0.5f);

        Assert.Equal(0.25f, animator.Phase, Tolerance);
        Assert.Equal(1f, RootX(animator), Tolerance);
    }

    [Fact]
    public void The_state_loops()
    {
        var animator = Controller().Start(Stance.Move);

        animator.Advance(2.5f);

        Assert.Equal(0.25f, animator.Phase, Tolerance);
        Assert.Equal(1f, RootX(animator), Tolerance);
    }

    [Fact]
    public void The_pose_moves_on_between_ticks()
    {
        var animator = Controller().Start(Stance.Move);
        animator.Advance(0.5f);

        Assert.Equal(2f, TestRig.Evaluate(animator.Pose, ahead: 0.5f)[0].Position.X, Tolerance);
    }

    [Fact]
    public void Speed_scales_the_state()
    {
        var rig = TestRig.Rig();
        var controller = new AnimationController<Stance, Deed>(rig).State(Stance.Move, rig.Clip("move"), speed: 2f);
        var animator = controller.Start(Stance.Move);

        animator.Advance(0.5f);

        Assert.Equal(2f, RootX(animator), Tolerance);
    }

    [Fact]
    public void Setting_a_state_cross_fades_from_the_old_one()
    {
        var animator = Controller(crossFade: 1f).Start(Stance.Idle);

        animator.Set(Stance.Move);
        Assert.True(animator.IsCrossFading);
        animator.Advance(0.5f);

        // Halfway through the fade: halfway from idle (0) to the move at 0.5 s (1).
        Assert.True(animator.IsIn(Stance.Move));
        Assert.Equal(0.5f, RootX(animator), Tolerance);

        animator.Advance(0.5f);

        Assert.False(animator.IsCrossFading);
        Assert.Equal(1, animator.Pose.LayerCount);
        Assert.Equal(2f, RootX(animator), Tolerance);
    }

    [Fact]
    public void Setting_the_same_state_changes_nothing()
    {
        var animator = Controller(crossFade: 1f).Start(Stance.Move);
        animator.Advance(0.5f);

        animator.Set(Stance.Move);

        Assert.False(animator.IsCrossFading);
        Assert.Equal(0.25f, animator.Phase, Tolerance);
    }

    [Fact]
    public void A_blended_state_mixes_clips_in_step()
    {
        var animator = Controller().Start(Stance.Blended);
        animator.Parameter = 2f;

        // Half idle (1 s) and half move (2 s) is a 1.5 s cycle, so 0.75 s is half a cycle.
        animator.Advance(0.75f);

        Assert.Equal(0.5f, animator.Phase, Tolerance);
        Assert.Equal(2, animator.Pose.LayerCount);
        // Half a cycle of the move is 1 s in, where x is 2; half of that is 1.
        Assert.Equal(1f, RootX(animator), Tolerance);

        // A quarter cycle on is 0.375 s. In step, the move runs 2 s per 1.5 s cycle, so it
        // reaches 1.5 s, where x is 3; half of that is 1.5.
        Assert.Equal(1.5f, TestRig.Evaluate(animator.Pose, ahead: 0.375f)[0].Position.X, Tolerance);
    }

    [Fact]
    public void Cross_fading_into_a_blended_state_lands_between_the_two()
    {
        var animator = Controller(crossFade: 1f).Start(Stance.Move);
        animator.Parameter = 2f;

        animator.Set(Stance.Blended);
        animator.Advance(0.5f);

        // The old move is 0.5 s in, at x 1. The blend is a third of its 1.5 s cycle in:
        // idle at 0 and the move at 2/3 s, at x 4/3, so half and half is 2/3. Halfway
        // through the fade is halfway from 1 to 2/3.
        Assert.Equal(5f / 6f, RootX(animator), Tolerance);
    }

    [Fact]
    public void An_action_plays_over_the_state_on_its_masked_joints()
    {
        var animator = Controller().Start(Stance.Move);

        animator.Play(Deed.Wave);
        animator.Advance(0.5f);

        Assert.True(animator.IsPlaying(Deed.Wave));
        Assert.Equal(1f, RootX(animator), Tolerance);
        Assert.Equal(1f, ChildX(animator), Tolerance);
    }

    [Fact]
    public void An_action_ends_when_its_clip_does()
    {
        var animator = Controller().Start(Stance.Idle);
        animator.Play(Deed.Wave);

        animator.Advance(1.5f);
        Assert.True(animator.IsPlaying(Deed.Wave));

        animator.Advance(0.5f);

        Assert.False(animator.IsPlaying(Deed.Wave));
        Assert.Equal(1, animator.Pose.LayerCount);
        Assert.Equal(0f, ChildX(animator), Tolerance);
    }

    [Fact]
    public void Playing_an_action_again_restarts_it()
    {
        var animator = Controller().Start(Stance.Idle);
        animator.Play(Deed.Wave);
        animator.Advance(1f);

        animator.Play(Deed.Wave);

        Assert.Equal(0f, ChildX(animator), Tolerance);
    }

    [Fact]
    public void Stop_ends_an_action_early()
    {
        var animator = Controller().Start(Stance.Idle);
        animator.Play(Deed.Wave);

        animator.Stop(Deed.Wave);

        Assert.False(animator.IsPlaying(Deed.Wave));
        Assert.Equal(1, animator.Pose.LayerCount);
    }

    [Fact]
    public void An_action_fades_in_and_out()
    {
        var animator = Controller().Start(Stance.Idle);
        animator.Play(Deed.FadedWave);

        animator.Advance(0.25f);
        Assert.Equal(0.5f, animator.Pose.Layers[1].Amount, Tolerance);

        animator.Advance(0.75f);
        Assert.Equal(1f, animator.Pose.Layers[1].Amount, Tolerance);

        animator.Advance(0.75f);
        Assert.Equal(0.5f, animator.Pose.Layers[1].Amount, Tolerance);
    }

    [Fact]
    public void Crossed_is_true_on_the_tick_an_action_passes_a_moment()
    {
        var animator = Controller().Start(Stance.Idle);

        animator.Play(Deed.Wave);
        Assert.True(animator.Crossed(Deed.Wave, 0f));
        Assert.False(animator.Crossed(Deed.Wave, 0.5f));

        animator.Advance(0.5f);
        Assert.False(animator.Crossed(Deed.Wave, 0f));
        Assert.True(animator.Crossed(Deed.Wave, 0.25f));
        Assert.True(animator.Crossed(Deed.Wave, 0.5f));
        Assert.False(animator.Crossed(Deed.Wave, 0.75f));

        animator.Advance(0.5f);
        Assert.False(animator.Crossed(Deed.Wave, 0.5f));
    }

    [Fact]
    public void An_action_says_how_far_into_its_clip_it_is_while_it_plays()
    {
        var animator = Controller().Start(Stance.Idle);
        Assert.Null(animator.TimeOf(Deed.Wave));

        animator.Play(Deed.Wave);
        Assert.Equal(0f, animator.TimeOf(Deed.Wave));

        animator.Advance(0.25f);
        Assert.Equal(0.25f, animator.TimeOf(Deed.Wave)!.Value, Tolerance);

        animator.Advance(10f);
        Assert.Null(animator.TimeOf(Deed.Wave));
    }

    [Fact]
    public void Crossed_is_false_for_an_action_never_played()
    {
        var animator = Controller().Start(Stance.Idle);
        animator.Advance(1f);

        Assert.False(animator.Crossed(Deed.Wave, 0.5f));
    }

    [Fact]
    public void Speed_scales_how_far_time_moves_it()
    {
        // The move clip lasts two seconds, so a quarter second at double speed is a quarter cycle.
        var animator = Controller().Start(Stance.Move);
        animator.Speed = 2f;

        animator.Advance(0.25f);

        Assert.Equal(0.25f, animator.Phase, Tolerance);
    }

    [Fact]
    public void A_speed_of_zero_holds_the_pose_still_even_between_ticks()
    {
        var animator = Controller().Start(Stance.Move);
        animator.Advance(0.5f);
        animator.Play(Deed.Wave);
        animator.Speed = 0f;

        animator.Advance(0.5f);

        // Half a second in, the root is at 1 and the wave hasn't moved the child; a display
        // half a second past the tick must not slide them on.
        var joints = TestRig.Evaluate(animator.Pose, ahead: 0.5f);
        Assert.Equal(1f, joints[0].Position.X, Tolerance);
        Assert.Equal(0f, joints[1].Position.X, Tolerance);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_speed_that_isnt_a_number_of_at_least_zero_is_refused(float speed)
    {
        var animator = Controller().Start(Stance.Idle);

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => animator.Speed = speed);

        Assert.StartsWith("The speed must be a number of at least zero.", problem.Message);
    }

    [Fact]
    public void A_state_without_a_clip_is_explained()
    {
        var problem = Assert.Throws<InvalidOperationException>(() => Controller().Start(Stance.Unset));

        Assert.Equal("The controller has no clip for the state Unset.", problem.Message);
    }

    [Fact]
    public void An_action_without_a_clip_is_explained()
    {
        var animator = Controller().Start(Stance.Idle);

        var problem = Assert.Throws<InvalidOperationException>(() => animator.Play(Deed.Unset));

        Assert.Equal("The controller has no clip for the action Unset.", problem.Message);
    }

    [Fact]
    public void The_wrong_enum_is_explained()
    {
        var animator = Controller().Start(Stance.Idle);

        var problem = Assert.Throws<InvalidOperationException>(() => animator.IsIn(Other.Thing));

        Assert.Equal("The animator's states are Stance, not Other.", problem.Message);
    }

    [Fact]
    public void A_mask_from_another_rig_is_refused_by_the_controller()
    {
        var rig = TestRig.Rig();
        var other = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() =>
            new AnimationController<Stance, Deed>(rig).Action(Deed.Wave, rig.Clip("wave"), other.Mask(TestRig.Child)));

        Assert.Contains("another rig", problem.Message);
    }

    [Fact]
    public void A_speed_of_zero_is_refused()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AnimationController<Stance, Deed>(rig).State(Stance.Idle, rig.Clip("idle"), speed: 0f));

        Assert.Contains("more than zero", problem.Message);
    }
}
