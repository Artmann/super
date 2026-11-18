using Rendition;

namespace Super.Tests.Animation;

public class AnimationEventTests
{
    private const float Tolerance = 0.0001f;

    private enum Stance
    {
        Idle,
        Move,
        Blended,
    }

    private enum Deed
    {
        Wave,
    }

    /// <summary>The test rig's clips as states and an action, with events added to the rig first.</summary>
    private static AnimationController<Stance, Deed> Controller(Action<Rig> events, float crossFade = 0f)
    {
        var rig = TestRig.Rig();
        events(rig);
        return new AnimationController<Stance, Deed>(rig) { CrossFadeSeconds = crossFade }
            .State(Stance.Idle, rig.Clip("idle"))
            .State(Stance.Move, rig.Clip("move"))
            .State(Stance.Blended, rig.Blend((rig.Clip("idle"), 0f), (rig.Clip("move"), 4f)))
            .Action(Deed.Wave, rig.Clip("wave"), rig.Mask(TestRig.Child), fadeSeconds: 0f);
    }

    private static List<(string Name, AnimationEventKind Kind)> Read(Animator animator)
    {
        var read = new List<(string, AnimationEventKind)>();
        foreach (var e in animator.Events)
        {
            read.Add((e.Name, e.Kind));
        }

        return read;
    }

    [Fact]
    public void A_looped_clip_fires_its_event_once_a_loop()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("move"), "beat", 0.5f)).Start(Stance.Move);

        int fired = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            animator.Advance(0.25f);
            Assert.True(animator.Events.Length <= 1, $"Tick {tick} fired {animator.Events.Length} events.");
            fired += animator.Events.Length;
        }

        // Ten seconds of a two-second clip pass half a second in five times: 0.5, 2.5, 4.5, 6.5 and 8.5.
        // Quarter-second ticks end right on each of them, which must count once, not on both ticks.
        Assert.Equal(5, fired);
    }

    [Fact]
    public void A_big_tick_fires_every_event_it_skips_in_order()
    {
        var animator = Controller(rig =>
        {
            rig.Event(rig.Clip("move"), "a", 0.5f);
            rig.Event(rig.Clip("move"), "b", 1.5f);
        }).Start(Stance.Move);

        animator.Advance(5f);

        // Five seconds of a two-second clip: 0.5, 1.5, 2.5, 3.5 and 4.5.
        Assert.Equal(["a", "b", "a", "b", "a"], Read(animator).Select(e => e.Name));
        Assert.All(Read(animator), e => Assert.Equal(AnimationEventKind.Moment, e.Kind));
    }

    [Fact]
    public void A_clip_fading_out_below_the_threshold_does_not_fire()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("move"), "beat", 1f), crossFade: 2f).Start(Stance.Move);
        animator.Set(Stance.Idle);

        animator.Advance(0.6f);
        animator.Advance(0.6f);

        // The move passes one second on the second tick, when the fade is 1.2 of 2 seconds in, leaving it 0.4.
        Assert.Equal(0, animator.Events.Length);
    }

    [Fact]
    public void A_clip_fading_out_above_the_threshold_fires_at_its_weight()
    {
        var controller = Controller(rig => rig.Event(rig.Clip("move"), "beat", 1f), crossFade: 2f);
        controller.EventThreshold = 0.3f;
        var animator = controller.Start(Stance.Move);
        animator.Set(Stance.Idle);

        animator.Advance(0.6f);
        animator.Advance(0.6f);

        var beat = Assert.Single(animator.Events.ToArray());
        Assert.Equal("beat", beat.Name);
        Assert.Equal(0.4f, beat.Weight, Tolerance);
    }

    [Fact]
    public void The_event_threshold_must_be_a_share()
    {
        var controller = Controller(_ => { });

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => controller.EventThreshold = 1.5f);

        Assert.Contains("The event threshold must be from 0 to 1.", problem.Message);
    }

    [Fact]
    public void A_window_begins_stays_active_and_ends()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("wave"), "swing", 0.3f, 0.5f)).Start(Stance.Idle);
        animator.Moves.Play(Deed.Wave);

        animator.Advance(0.4f);
        Assert.Equal([("swing", AnimationEventKind.Begin)], Read(animator));
        Assert.True(animator.IsActive("swing"));

        animator.Advance(0.05f);
        Assert.Empty(Read(animator));
        Assert.True(animator.IsActive("swing"));

        animator.Advance(0.2f);
        Assert.Equal([("swing", AnimationEventKind.End)], Read(animator));
        Assert.False(animator.IsActive("swing"));
    }

    [Fact]
    public void A_window_passed_in_one_tick_begins_and_ends()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("wave"), "swing", 0.3f, 0.5f)).Start(Stance.Idle);
        animator.Moves.Play(Deed.Wave);

        animator.Advance(1f);

        Assert.Equal([("swing", AnimationEventKind.Begin), ("swing", AnimationEventKind.End)], Read(animator));
        Assert.False(animator.IsActive("swing"));
    }

    [Fact]
    public void Stopping_an_action_ends_its_open_window()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("wave"), "swing", 0.3f, 0.5f)).Start(Stance.Idle);
        animator.Moves.Play(Deed.Wave);
        animator.Advance(0.4f);

        animator.Moves.Stop(Deed.Wave);
        animator.Advance(0.05f);

        Assert.Equal([("swing", AnimationEventKind.End)], Read(animator));
        Assert.False(animator.IsActive("swing"));
    }

    [Fact]
    public void An_action_event_at_the_start_fires_on_its_first_tick_only()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("wave"), "start", 0f)).Start(Stance.Idle);
        animator.Moves.Play(Deed.Wave);

        animator.Advance(0.1f);
        Assert.Equal([("start", AnimationEventKind.Moment)], Read(animator));

        animator.Advance(0.1f);
        Assert.Empty(Read(animator));
    }

    [Fact]
    public void A_blend_fires_only_the_heavier_clip_events()
    {
        var animator = Controller(rig =>
        {
            rig.Event(rig.Clip("idle"), "idle-beat", 0.5f);
            rig.Event(rig.Clip("move"), "move-beat", 1f);
        }).Start(Stance.Blended);

        // Three of four is three quarters of the way to the move.
        animator.Parameter = 3f;
        animator.Advance(0.1f);
        var moving = new List<string>();
        for (int tick = 0; tick < 30; tick++)
        {
            animator.Advance(0.1f);
            moving.AddRange(Read(animator).Select(e => e.Name));
        }

        // One of four is a quarter of the way, so the idle is heavier.
        animator.Parameter = 1f;
        var idling = new List<string>();
        for (int tick = 0; tick < 30; tick++)
        {
            animator.Advance(0.1f);
            idling.AddRange(Read(animator).Select(e => e.Name));
        }

        Assert.NotEmpty(moving);
        Assert.All(moving, name => Assert.Equal("move-beat", name));
        Assert.NotEmpty(idling);
        Assert.All(idling, name => Assert.Equal("idle-beat", name));
    }

    [Fact]
    public void An_event_carries_where_its_joint_is_after_the_tick()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("wave"), "hit", 0.25f, TestRig.Child)).Start(Stance.Idle);
        animator.Moves.Play(Deed.Wave);

        animator.Advance(0.5f);

        // The wave slides the child from x 0 to 2 over its first second, one unit up, so half a second in it is at x 1.
        var hit = Assert.Single(animator.Events.ToArray());
        Assert.NotNull(hit.Joint);
        var at = hit.Joint.Value.Translation;
        Assert.Equal(1f, at.X, Tolerance);
        Assert.Equal(1f, at.Y, Tolerance);
        Assert.Equal(0f, at.Z, Tolerance);
        Assert.Equal(1f, hit.Weight);
    }

    [Fact]
    public void An_event_without_a_joint_carries_none()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("wave"), "hit", 0.25f)).Start(Stance.Idle);
        animator.Moves.Play(Deed.Wave);

        animator.Advance(0.5f);

        Assert.Null(Assert.Single(animator.Events.ToArray()).Joint);
    }

    [Fact]
    public void Events_from_a_state_and_an_action_come_in_the_order_they_happened()
    {
        var animator = Controller(rig =>
        {
            rig.Event(rig.Clip("move"), "step", 0.5f);
            rig.Event(rig.Clip("wave"), "hit", 0.2f);
        }).Start(Stance.Move);
        animator.Moves.Play(Deed.Wave);

        animator.Advance(1f);

        // The action comes after the state, but its event is 0.2 seconds in and the state's 0.5.
        Assert.Equal(["hit", "step"], Read(animator).Select(e => e.Name));
    }

    [Fact]
    public void An_event_names_its_clip()
    {
        var animator = Controller(rig => rig.Event(rig.Clip("move"), "step", 0.5f)).Start(Stance.Move);

        animator.Advance(1f);

        Assert.Equal(animator.Rig.Clip("move"), Assert.Single(animator.Events.ToArray()).Clip);
    }

    [Fact]
    public void An_event_outside_its_clip_is_refused()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => rig.Event(rig.Clip("wave"), "late", 1f, 3f));

        Assert.Contains("Event late runs from 1 to 3 seconds, but clip wave lasts 2", problem.Message);
    }

    [Fact]
    public void An_event_needs_a_name()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() => rig.Event(rig.Clip("wave"), "", 1f));

        Assert.Contains("An event needs a name.", problem.Message);
    }

    [Fact]
    public void An_event_on_an_unknown_joint_lists_the_joints()
    {
        var rig = TestRig.Rig();

        var problem = Assert.Throws<ArgumentException>(() => rig.Event(rig.Clip("wave"), "hit", 1f, "hand"));

        Assert.Contains("Event hit names the joint hand, but the rig has no joint called that. It has: root, child", problem.Message);
    }

    [Fact]
    public void A_rig_lists_its_clip_events()
    {
        var rig = TestRig.Rig();
        rig.Event(rig.Clip("wave"), "swing", 0.3f, 0.5f);

        var swing = Assert.Single(rig.Events(rig.Clip("wave")));

        Assert.Equal(new ClipEvent("swing", 0.3f, 0.5f), swing);
        Assert.Empty(rig.Events(rig.Clip("move")));
    }
}
