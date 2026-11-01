using Super.Testing;

namespace Super.Tests;

public class StatesTests
{
    public enum Screen { Menu, House, Outside }

    public enum Pause { Running, Paused }

    private class Walker(States<Screen> states, States<Pause> pause) : IGame
    {
        public List<string> Log { get; } = [];
        public Screen? SetOnUpdate { get; set; }
        public Screen DrawnIn { get; private set; }

        public void Load()
        {
            foreach (var screen in Enum.GetValues<Screen>())
            {
                states.OnEnter(screen, () => Log.Add($"enter {screen} from {states.Previous}"));
                states.OnExit(screen, () => Log.Add($"exit {screen} current {states.Current}"));
            }

            pause.OnEnter(Pause.Paused, () => Log.Add("paused"));
        }

        public void Update(GameTime time)
        {
            Log.Add($"update {states.Current}");
            if (SetOnUpdate is { } next)
            {
                SetOnUpdate = null;
                states.Set(next);
                Log.Add($"still {states.Current}");
            }
        }

        public void Draw(View view) => DrawnIn = states.Current;
    }

    private class StartsOutside(States<Screen> states) : IGame
    {
        public List<string> Log { get; } = [];

        public void Load()
        {
            states.OnEnter(Screen.Menu, () => Log.Add("menu"));
            states.OnEnter(Screen.Outside, () => Log.Add("outside"));
            states.Set(Screen.Outside);
            Log.Add("loaded");
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    private class Chain(States<Screen> states) : IGame
    {
        public void Load()
        {
            states.OnEnter(Screen.House, () => states.Set(Screen.Outside));
            states.Set(Screen.House);
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    private class Loop(States<Screen> states) : IGame
    {
        public void Load()
        {
            states.OnEnter(Screen.House, () => states.Set(Screen.Outside));
            states.OnEnter(Screen.Outside, () => states.Set(Screen.House));
            states.Set(Screen.House);
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    private class SetsInDraw(States<Screen> states) : IGame
    {
        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => states.Set(Screen.House);
    }

    private class ReadsInputOnEnter(States<Screen> states, Input input) : IGame
    {
        public void Load() => states.OnEnter(Screen.House, () => input.Down(Key.W));

        public void Update(GameTime time) => states.Set(Screen.House);

        public void Draw(View view)
        {
        }
    }

    [Fact]
    public void The_first_value_is_entered_before_the_first_tick()
    {
        using var run = HeadlessGame.Create<Walker>(1);
        Assert.Equal(["enter Menu from Menu"], run.Game.Log);
        Assert.True(run.States<Screen>().Is(Screen.Menu));
    }

    [Fact]
    public void Load_can_pick_the_starting_state()
    {
        using var run = HeadlessGame.Create<StartsOutside>(1);
        Assert.Equal(["loaded", "outside"], run.Game.Log);
    }

    [Fact]
    public void A_change_lands_after_Update_in_the_order_exit_then_enter()
    {
        using var run = HeadlessGame.Create<Walker>(1);
        run.Game.Log.Clear();
        run.Game.SetOnUpdate = Screen.House;
        run.Ticks(1);
        Assert.Equal(["update Menu", "still Menu", "exit Menu current Menu", "enter House from Menu"], run.Game.Log);
    }

    [Fact]
    public void The_draw_after_a_change_shows_the_new_state()
    {
        using var run = HeadlessGame.Create<Walker>(1);
        run.Game.SetOnUpdate = Screen.House;
        run.Ticks(1);
        Assert.Equal(Screen.House, run.Game.DrawnIn);
    }

    [Fact]
    public void Setting_the_current_state_does_nothing()
    {
        using var run = HeadlessGame.Create<Walker>(1);
        run.Game.Log.Clear();
        run.Game.SetOnUpdate = Screen.Menu;
        run.Ticks(1);
        Assert.Equal(["update Menu", "still Menu"], run.Game.Log);
    }

    [Fact]
    public void The_last_set_in_a_tick_wins()
    {
        using var run = HeadlessGame.Create<Walker>(1);
        run.States<Screen>().Set(Screen.House);
        run.States<Screen>().Set(Screen.Outside);
        run.Ticks(1);
        Assert.Equal(Screen.Outside, run.States<Screen>().Current);
        Assert.DoesNotContain(run.Game.Log, line => line.StartsWith("enter House"));
    }

    [Fact]
    public void A_callback_can_chain_to_another_state()
    {
        using var run = HeadlessGame.Create<Chain>(1);
        Assert.Equal(Screen.Outside, run.States<Screen>().Current);
        Assert.Equal(Screen.House, run.States<Screen>().Previous);
    }

    [Fact]
    public void A_loop_of_changes_throws()
    {
        Assert.Throws<InvalidOperationException>(() => HeadlessGame.Create<Loop>(1));
    }

    [Fact]
    public void Changing_state_in_draw_throws()
    {
        using var run = HeadlessGame.Create<SetsInDraw>(1);
        Assert.Throws<InvalidOperationException>(() => run.Ticks(1));
    }

    [Fact]
    public void Enter_callbacks_cannot_read_input()
    {
        using var run = HeadlessGame.Create<ReadsInputOnEnter>(1);
        Assert.Throws<InvalidOperationException>(() => run.Ticks(1));
    }

    [Fact]
    public void Two_enums_run_independently()
    {
        using var run = HeadlessGame.Create<Walker>(1);
        run.States<Pause>().Set(Pause.Paused);
        run.Ticks(1);
        Assert.Contains("paused", run.Game.Log);
        Assert.Equal(Screen.Menu, run.States<Screen>().Current);
    }

    [Fact]
    public void Ticking_without_a_change_allocates_nothing()
    {
        using var run = HeadlessGame.Create<StartsOutside>(1);
        run.Ticks(100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        run.Ticks(1000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
