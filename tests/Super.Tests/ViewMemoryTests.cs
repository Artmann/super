using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class ViewMemoryTests
{
    private const float TickSeconds = 0.1f;

    private readonly View _view = new();
    private long _tick = 1;

    /// <summary>Moves on one tick and starts a frame. Tick 1 shows game time zero.</summary>
    private void Frame(float blend = 0f)
    {
        _view.Begin(blend, _tick, TickSeconds);
        _tick++;
    }

    private float Lid(float target) => _view.Animate(1, target, seconds: 1f);

    private bool Open(bool value) => _view.Changed(1, value);

    [Fact]
    public void A_new_key_goes_straight_to_its_target()
    {
        Frame();
        Assert.Equal(1f, Lid(1f));
    }

    [Fact]
    public void A_change_of_target_takes_the_time_asked_for()
    {
        Frame();
        Lid(0f);
        Frame();
        Assert.Equal(0f, Lid(1f));

        for (int i = 0; i < 4; i++)
        {
            Frame();
            Lid(1f);
        }

        Frame();
        Assert.Equal(0.5f, Lid(1f), 3);

        for (int i = 0; i < 5; i++)
        {
            Frame();
            Lid(1f);
        }

        Assert.Equal(1f, Lid(1f));
    }

    [Fact]
    public void Animating_eases_in_and_out()
    {
        Frame();
        Lid(0f);
        Frame();
        Lid(1f);
        Frame();
        Assert.InRange(Lid(1f), 0f, 0.05f);
    }

    [Fact]
    public void Animating_moves_between_ticks()
    {
        Frame();
        Lid(0f);
        Frame();
        Lid(1f);
        _tick += 4;
        Frame();
        float before = Lid(1f);
        _tick--;
        Frame(0.5f);
        Assert.True(Lid(1f) > before);
    }

    [Fact]
    public void A_new_target_starts_from_where_the_value_is()
    {
        Frame();
        Lid(0f);
        Frame();
        Lid(1f);
        _tick += 4;
        Frame();
        float halfway = Lid(1f);
        Frame();
        Assert.Equal(halfway, Lid(0f), 3);
    }

    [Fact]
    public void The_frame_rate_does_not_change_the_curve()
    {
        var everyTick = new ViewMemoryTests();
        var skipping = new ViewMemoryTests();
        foreach (var run in new[] { everyTick, skipping })
        {
            run.Frame();
            run.Lid(0f);
            run.Frame();
            run.Lid(1f);
        }

        for (int i = 0; i < 3; i++)
        {
            everyTick.Frame();
            everyTick.Lid(1f);
        }

        skipping._tick += 2;
        skipping.Frame();
        Assert.Equal(everyTick.Lid(1f), skipping.Lid(1f));
    }

    [Fact]
    public void Two_calls_with_one_key_keep_apart()
    {
        Frame();
        float lid = _view.Animate(1, 0f, seconds: 1f);
        float latch = _view.Animate(1, 1f, seconds: 1f);
        Assert.Equal(0f, lid);
        Assert.Equal(1f, latch);
    }

    [Fact]
    public void A_key_that_was_not_drawn_is_forgotten_and_comes_back_finished()
    {
        Frame();
        Lid(0f);
        Frame();
        Frame();
        Assert.Equal(1f, Lid(1f));
    }

    [Fact]
    public void A_value_seen_for_the_first_time_is_not_a_change()
    {
        Frame();
        Assert.False(Open(true));
    }

    [Fact]
    public void A_change_shows_for_one_frame()
    {
        Frame();
        Open(false);
        Frame();
        Assert.True(Open(true));
        Frame();
        Assert.False(Open(true));
    }

    [Fact]
    public void Changes_of_different_types_keep_apart()
    {
        Frame();
        _view.Changed(1, 5);
        _view.Changed(1, "text");
        Frame();
        Assert.True(_view.Changed(1, 6));
    }

    [Fact]
    public void A_value_that_was_not_drawn_is_not_a_change_when_it_comes_back()
    {
        Frame();
        Open(false);
        Frame();
        Frame();
        Assert.False(Open(true));
    }

    [Fact]
    public void Seconds_since_counts_from_the_tick_that_was_saved()
    {
        _view.Begin(0.5f, 11, TickSeconds);
        Assert.Equal(0.05f, _view.SecondsSince(10), 4);
        _view.Begin(0.5f, 21, TickSeconds);
        Assert.Equal(1.05f, _view.SecondsSince(10), 4);
    }

    [Fact]
    public void Seconds_since_is_never_negative()
    {
        _view.Begin(0f, 1, TickSeconds);
        Assert.Equal(0f, _view.SecondsSince(50));
    }

    private int _described;

    private void Walls(long version, int key = 1) => _view.Cached(key, version, this, static (self, view) =>
    {
        self._described++;
        view.Model(self._described, ModelRef.Cube, Matrix4x4.Identity);
    });

    [Fact]
    public void Cached_only_describes_again_when_the_version_changes()
    {
        Frame();
        Walls(1);
        Frame();
        Walls(1);
        Assert.Equal(1, _described);
        Assert.Equal(1, Assert.Single(_view.Drawn.ToArray()).Key);

        Frame();
        Walls(2);
        Assert.Equal(2, _described);
        Assert.Equal(2, Assert.Single(_view.Drawn.ToArray()).Key);
    }

    [Fact]
    public void Cached_keeps_each_key_apart_at_one_version()
    {
        Frame();
        Walls(1, key: 1);
        Walls(1, key: 2);
        Assert.Equal(2, _described);
        Assert.Equal(2, _view.Drawn.Length);
    }

    [Fact]
    public void Cached_sits_in_order_among_other_models()
    {
        for (int i = 0; i < 2; i++)
        {
            Frame();
            _view.Model(100, ModelRef.Cube, Matrix4x4.Identity);
            Walls(1);
            _view.Model(200, ModelRef.Cube, Matrix4x4.Identity);
            Assert.Equal([100, 1, 200], _view.Drawn.ToArray().Select(model => model.Key));
        }
    }

    [Fact]
    public void Cached_inside_cached_is_kept_by_the_outer_one()
    {
        for (int i = 0; i < 2; i++)
        {
            Frame();
            _view.Cached(9, 1, this, static (self, view) => self.Walls(1));
            Assert.Equal([1], _view.Drawn.ToArray().Select(model => model.Key));
        }

        Assert.Equal(1, _described);
    }

    [Fact]
    public void Remembering_allocates_nothing_once_it_has_settled()
    {
        for (int i = 0; i < 3; i++)
        {
            Frame();
            Lid(1f);
            Open(true);
            Walls(1);
            _view.SecondsSince(0);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Frame();
            Lid(i % 2);
            Open(i % 2 == 0);
            Walls(1);
            _view.SecondsSince(0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private class Box(Input input) : IGame
    {
        public bool IsOpen { get; private set; }

        public float Lid { get; private set; }

        public int Creaks { get; private set; }

        public void Update(GameTime time)
        {
            IsOpen = input.Down(Key.E);
        }

        public void Draw(View view)
        {
            Lid = view.Animate(7, IsOpen ? 1f : 0f, seconds: 0.5f);
            if (view.Changed(7, IsOpen))
            {
                Creaks++;
            }
        }
    }

    [Fact]
    public void A_lid_swings_in_a_running_game()
    {
        using var run = HeadlessGame.Create<Box>(1);
        run.Ticks(2);
        Assert.Equal(0f, run.Game.Lid);

        run.Input.Hold(Key.E);
        run.Ticks(5);
        Assert.InRange(run.Game.Lid, 0.01f, 0.99f);
        Assert.Equal(1, run.Game.Creaks);

        run.Ticks(30);
        Assert.Equal(1f, run.Game.Lid);
        Assert.Equal(1, run.Game.Creaks);
    }
}
