using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Rendition;
using Super.Testing;
using AudioService = Super.Audio;

namespace Super.Tests.Animation;

/// <summary>
/// Clip events that play a sound, through the game loop. The test rig's idle clip lasts a
/// second and holds the child joint at (0, 1, 0) in the model. The game draws the model ten
/// metres left of the camera and one down, so a step at that joint sounds at (−10, 0, 0). Steps
/// fall at 0.51 seconds, just past a tick, so float error can't move them a tick: on tick 16,
/// and every 30 ticks after.
/// </summary>
public class AnimationSoundTests
{
    // Falloff at 10 m with near 2 and far 50: (2/10 − 2/50) / (1 − 2/50).
    private const float TenMetres = 0.16f / 0.96f;

    private static readonly Matrix4x4 Placement = Matrix4x4.CreateTranslation(-10f, -1f, 0f);

    private enum Stance
    {
        Idle,
        Rest,
    }

    private enum Deed
    {
        Wave,
    }

    /// <summary>How the test game sets its animator up, and what it does in Update.</summary>
    private sealed class Setup
    {
        /// <summary>The idle clip's events, as a cooked model would have them.</summary>
        public ClipEvent[] Cooked { get; init; } = [];

        /// <summary>Marks events on the rig in code, once content has it.</summary>
        public Action<Rig, Content>? Mark { get; init; }

        public Stance Start { get; init; } = Stance.Idle;

        public float CrossFade { get; init; }

        public float Threshold { get; init; } = 0.5f;

        public bool Draws { get; init; } = true;

        public Action<Animator, AnimationControllerBase, Content>? Update { get; set; }
    }

    private sealed class Stepper(Content content, GameRandom random, Setup setup) : IGame
    {
        public Animator Animator { get; private set; } = null!;

        public AnimationControllerBase Controller { get; private set; } = null!;

        public GameRandom Random => random;

        public void Load()
        {
            var idle = TestRig.Idle();
            var rest = new AnimationClip("rest", idle.FrameRate, idle.JointCount, idle.Frames);
            var rig = new Rig(TestRig.Skeleton(), [new AnimationClip(idle.Name, idle.FrameRate, idle.JointCount, idle.Frames, setup.Cooked), rest, TestRig.Wave()]);
            rig.Runner = content.Models.Animators;
            setup.Mark?.Invoke(rig, content);
            Controller = new AnimationController<Stance, Deed>(rig) { CrossFadeSeconds = setup.CrossFade, EventThreshold = setup.Threshold }
                .State(Stance.Idle, rig.Clip("idle"))
                .State(Stance.Rest, rig.Clip("rest"))
                .Action(Deed.Wave, rig.Clip("wave"), fadeSeconds: 0f);
            Animator = ((AnimationController<Stance, Deed>)Controller).Start(setup.Start);
        }

        public void Update(GameTime time) => setup.Update?.Invoke(Animator, Controller, content);

        public void Draw(View view)
        {
            view.Camera(Vector3.Zero, -Vector3.UnitZ);
            if (setup.Draws)
            {
                view.Model(1, ModelRef.Sphere, Placement, Animator.Pose);
            }
        }
    }

    private static byte[] Click()
    {
        var samples = new float[Mixer.SampleRate / 100];
        Array.Fill(samples, 0.5f);
        return TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: false);
    }

    private static HeadlessRun<Stepper> Start(Setup setup) =>
        HeadlessGame.Create<Stepper>(1, services => services
            .AddContentFiles(new MemoryContent().Add("step.wav", Click()).Add("stone.wav", Click()))
            .AddSingleton(setup));

    private static readonly ClipEvent Step = new("step", 0.51f, TestRig.Child) { Sound = new ClipSound("step.wav", volume: 0.5f) };

    /// <summary>Runs a tick at a time and notes each sound that started on it, with the tick it started on.</summary>
    private static List<(int Tick, PlayingSound Sound)> Listen(HeadlessRun<Stepper> run, int ticks)
    {
        var started = new List<(int, PlayingSound)>();
        for (int tick = 1; tick <= ticks; tick++)
        {
            run.Ticks(1);
            foreach (var sound in run.Sounds)
            {
                if (sound.Seconds == 0)
                {
                    started.Add((tick, sound));
                }
            }
        }

        return started;
    }

    [Fact]
    public void A_looping_walk_plays_its_step_once_a_loop_where_the_joint_is()
    {
        using var run = Start(new Setup { Cooked = [Step] });

        var started = Listen(run, 90);

        Assert.Equal([16, 46, 76], started.Select(s => s.Tick).ToArray());
        foreach (var (_, sound) in started)
        {
            Assert.Equal("step.wav", sound.Clip);
            Assert.Equal(Channel.Effects, sound.Channel);
            Assert.Equal(-1f, sound.Pan, 0.0001f);
            Assert.Equal(0.5f * TenMetres, sound.Gain, 0.0001f);
        }
    }

    /// <summary>A setup whose first Update fades from rest, which holds the same pose as idle but has no steps, to idle over two seconds.</summary>
    private static Setup FadingIn(float threshold)
    {
        var setup = new Setup { Cooked = [Step], Start = Stance.Rest, CrossFade = 2f, Threshold = threshold };
        setup.Update = (animator, _, _) =>
        {
            animator.Set(Stance.Idle);
            setup.Update = null;
        };
        return setup;
    }

    [Fact]
    public void A_clip_faded_in_below_the_threshold_plays_nothing_and_then_plays_as_loud_as_its_share()
    {
        using var run = Start(FadingIn(threshold: 0.5f));

        var started = Listen(run, 60);

        // Idle's first step, on tick 16, has 16 of the fade's 60 ticks, under half. Its second, on tick 46, has 46.
        var (tick, sound) = Assert.Single(started);
        Assert.Equal(46, tick);
        Assert.Equal(46f / 60f * 0.5f * TenMetres, sound.Gain, 0.0001f);
    }

    [Fact]
    public void With_no_threshold_a_clip_just_fading_in_plays_quietly()
    {
        using var run = Start(FadingIn(threshold: 0f));

        var started = Listen(run, 30);

        var (tick, sound) = Assert.Single(started);
        Assert.Equal(16, tick);
        Assert.Equal(16f / 60f * 0.5f * TenMetres, sound.Gain, 0.0001f);
    }

    [Fact]
    public void A_window_plays_its_sound_as_it_begins_and_not_as_it_ends()
    {
        using var run = Start(new Setup
        {
            Mark = (rig, _) => rig.Event(rig.Clip("idle"), "swing", 0.51f, 0.9f, TestRig.Child, new ClipSound("step.wav")),
        });

        // The window opens on tick 16 and closes on tick 27. The next lap opens it again on tick 46.
        var started = Listen(run, 45);

        var (tick, sound) = Assert.Single(started);
        Assert.Equal(16, tick);
        Assert.Equal(TenMetres, sound.Gain, 0.0001f);
    }

    [Fact]
    public void The_controller_can_swap_a_sound_or_leave_it_out()
    {
        int picks = 0;
        var setup = new Setup { Cooked = [Step] };
        setup.Update = (_, controller, content) =>
        {
            controller.PickSound = (at, animationEvent, sound) =>
            {
                Assert.Equal(new Vector3(-10f, 0f, 0f), at);
                Assert.Equal("step", animationEvent.Name);
                Assert.Equal(content.Sounds.Sound("step.wav"), sound);
                return picks++ == 0 ? content.Sounds.Sound("stone.wav") : null;
            };
            setup.Update = null;
        };
        using var run = Start(setup);

        var started = Listen(run, 90);

        Assert.Equal(3, picks);
        var (tick, sound) = Assert.Single(started);
        Assert.Equal(16, tick);
        Assert.Equal("stone.wav", sound.Clip);
    }

    [Fact]
    public void An_animator_that_was_never_drawn_plays_nothing()
    {
        using var run = Start(new Setup { Cooked = [Step], Draws = false });

        Assert.Empty(Listen(run, 90));
    }

    [Fact]
    public void A_sound_content_has_not_got_stops_the_rig_being_adopted()
    {
        var setup = new Setup { Cooked = [Step with { Sound = new ClipSound("sounds/missing.wav") }] };

        var problem = Assert.Throws<ContentException>(() => Start(setup));
        Assert.Contains("sounds/missing.wav", problem.Message);
    }
}
