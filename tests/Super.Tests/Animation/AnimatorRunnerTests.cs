using System.Runtime.CompilerServices;
using Super.Testing;

namespace Super.Tests.Animation;

public class AnimatorRunnerTests
{
    private enum Stance
    {
        Move,
    }

    private static Animator Start(Rig rig) =>
        new AnimationController<Stance, Stance>(rig).State(Stance.Move, rig.Clip("move")).Start(Stance.Move);

    /// <summary>A game that never moves its animator itself, and notes the phase each Update sees.</summary>
    private sealed class Walker(Content content) : IGame
    {
        public Animator Animator { get; private set; } = null!;

        public List<float> PhasesSeen { get; } = [];

        public void Load()
        {
            var rig = TestRig.Rig();
            content.Adopt(rig);
            Animator = Start(rig);
        }

        public void Update(GameTime time) => PhasesSeen.Add(Animator.Phase);

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void The_game_loop_moves_animators_on_once_a_tick_after_update()
    {
        using var run = HeadlessGame.Create<Walker>(1);

        run.Ticks(3);

        // The move clip lasts two seconds, so each thirtieth of a second is a sixtieth of
        // its cycle. Each Update sees the phase from before its own tick moved it on.
        Assert.Equal([0f, 1f / 60f, 2f / 60f], run.Game.PhasesSeen.ToArray(), new ToleranceComparer(0.0001f));
        Assert.Equal(3f / 60f, run.Game.Animator.Phase, 0.0001f);
    }

    /// <summary>A walker in a world running at half speed.</summary>
    private sealed class SlowWalker(Content content, GameTime time) : IGame
    {
        public Animator Animator { get; private set; } = null!;

        public void Load()
        {
            time.Scale = 0.5f;
            var rig = TestRig.Rig();
            content.Adopt(rig);
            Animator = Start(rig);
        }

        public void Update(GameTime gameTime)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void The_game_loop_moves_animators_by_the_scaled_time()
    {
        using var run = HeadlessGame.Create<SlowWalker>(1);

        run.Ticks(4);

        // Four ticks at half speed is two ticks' worth: a sixtieth of the cycle each.
        Assert.Equal(2f / 60f, run.Game.Animator.Phase, 0.0001f);
    }

    /// <summary>A game with a rig built in code and never handed to content.</summary>
    private sealed class HandMade : IGame
    {
        public Animator Animator { get; } = Start(TestRig.Rig());

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void An_animator_from_a_rig_built_in_code_is_left_to_move_by_hand()
    {
        using var run = HeadlessGame.Create<HandMade>(1);

        run.Ticks(3);

        Assert.Equal(0f, run.Game.Animator.Phase);
    }

    [Fact]
    public void An_animator_nothing_holds_is_forgotten_once_collected()
    {
        var runner = new AnimatorRunner();
        var rig = TestRig.Rig();
        rig.Runner = runner;
        var kept = Start(rig);
        StartAndDrop(rig);
        Assert.Equal(2, runner.Count);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        runner.Advance(0.5f);

        Assert.Equal(1, runner.Count);
        Assert.Equal(0.25f, kept.Phase, 0.0001f);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartAndDrop(Rig rig) => Start(rig);

    private sealed class ToleranceComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) <= tolerance;

        public int GetHashCode(float value) => 0;
    }
}
