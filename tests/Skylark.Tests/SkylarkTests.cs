using System.Numerics;
using SkylarkExample;
using Super;
using Super.Physics;
using Super.Testing;

namespace Skylark.Tests;

public class SkylarkTests
{
    /// <summary>A game past the title screen, with the bird standing on the first island.</summary>
    private static HeadlessRun<SkylarkGame> Start(int level = 0)
    {
        var run = HeadlessGame.Create<SkylarkGame>(1, services => services.AddSkylark());
        run.States<Screen>().Set(Screen.Playing);
        run.Ticks(1);
        if (level > 0)
        {
            Journey(run).Level = level;
            run.Scenes.Rebuild();
        }

        run.Ticks(10);
        return run;
    }

    private static Entity Bird(HeadlessRun<SkylarkGame> run) => run.World.Single<Hero>();

    // The game's own services, so the game needs no property only its tests read.
    private static Journey Journey(HeadlessRun<SkylarkGame> run) => run.Service<Journey>();

    private static int Found(HeadlessRun<SkylarkGame> run) => run.Service<Gems>().Found;

    private static Records Records(HeadlessRun<SkylarkGame> run) => run.Service<UserData>().Get<Records>();

    private static Vector3 Position(HeadlessRun<SkylarkGame> run, Entity entity) => run.World.Get<Transform>(entity).Position;

    /// <summary>Puts the bird on each gem in turn, then waits for the areas to notice.</summary>
    private static void CollectEveryGem(HeadlessRun<SkylarkGame> run)
    {
        foreach (var spot in Levels.All[Journey(run).Level].Gems)
        {
            run.World.Teleport(Bird(run), Transform.At(spot + new Vector3(0f, 0.5f, 0f)));
            run.Ticks(3);
        }
    }

    /// <summary>Finds every gem on the last level and stands the bird by its beacon.</summary>
    private static void FinishLastLevel(HeadlessRun<SkylarkGame> run)
    {
        CollectEveryGem(run);
        run.World.Teleport(Bird(run), Transform.At(Levels.All[^1].Goal + new Vector3(1f, 0f, 0f)));
        run.Ticks(5);
        Assert.True(run.States<Screen>().Is(Screen.Won));
    }

    [Fact]
    public void The_bird_stands_on_the_first_island()
    {
        using var run = Start();
        run.Ticks(30);

        var bird = Bird(run);
        Assert.InRange(Position(run, bird).Y, -0.05f, 0.05f);
        Assert.Equal(0f, run.World.Get<Jumper>(bird).AirTime);
        Assert.Equal(0, Journey(run).Falls);
    }

    [Fact]
    public void Holding_forward_walks_away_from_the_camera()
    {
        using var run = Start();
        var before = Position(run, Bird(run));

        run.Input.Hold(Control.Move, new Vector2(0f, 1f));
        run.Ticks(15);

        // Half a second at 6 metres a second, away from a camera that looks down -z.
        var after = Position(run, Bird(run));
        Assert.InRange(before.Z - after.Z, 2.8f, 3.2f);
        Assert.InRange(after.X - before.X, -0.05f, 0.05f);
    }

    [Fact]
    public void A_jump_rises_nearly_two_metres_and_lands_again()
    {
        using var run = Start();
        var bird = Bird(run);

        run.Input.Hold(Control.Jump);
        float highest = 0f;
        for (int tick = 0; tick < 40; tick++)
        {
            run.Ticks(1);
            highest = MathF.Max(highest, Position(run, bird).Y);
        }

        // 9.5 m/s up against 24 m/s² comes to 1.88 m, and stepping in ticks adds about 0.16 m.
        Assert.InRange(highest, 1.9f, 2.1f);
        Assert.InRange(Position(run, bird).Y, -0.05f, 0.05f);
    }

    [Fact]
    public void Letting_go_of_jump_early_makes_a_lower_hop()
    {
        using var run = Start();
        var bird = Bird(run);

        run.Input.Press(Control.Jump);
        float highest = 0f;
        for (int tick = 0; tick < 40; tick++)
        {
            run.Ticks(1);
            highest = MathF.Max(highest, Position(run, bird).Y);
        }

        Assert.InRange(highest, 0.2f, 1f);
    }

    [Fact]
    public void Walking_off_the_edge_puts_the_bird_home_and_counts_a_fall()
    {
        using var run = Start();
        var bird = Bird(run);

        run.Input.Hold(Control.Move, new Vector2(0f, -1f));
        for (int tick = 0; tick < 120 && Journey(run).Falls == 0; tick++)
        {
            run.Ticks(1);
        }

        run.Input.Release(Control.Move);
        run.Ticks(20);

        Assert.Equal(1, Journey(run).Falls);
        Assert.True(Vector3.Distance(Position(run, bird), Levels.All[0].Start) < 0.1f, $"the bird is at {Position(run, bird)}");
    }

    [Fact]
    public void Walking_into_a_gem_collects_it()
    {
        using var run = Start();
        Assert.Equal(5, run.World.Count<Gem>());

        // The first gem is left of the path and ahead: 2.5 m left and 3.5 m forward of the start.
        run.Input.Hold(Control.Move, Vector2.Normalize(new Vector2(-2.5f, 3.5f)));
        run.Ticks(26);

        Assert.Equal(1, Found(run));
        Assert.Equal(4, run.World.Count<Gem>());
        Assert.Contains(run.ShownText, shown => shown.Text == "Gems 1/5");
    }

    [Fact]
    public void The_beacon_does_nothing_until_every_gem_is_found()
    {
        using var run = Start();
        var flame = run.World.Only<Goal>().Flame;
        var atBeacon = Transform.At(Levels.All[0].Goal + new Vector3(1f, 0f, 0f));

        run.World.Teleport(Bird(run), atBeacon);
        run.Ticks(40);
        Assert.True(run.World.Has<Hidden>(flame));
        Assert.Equal(0, Journey(run).Level);

        CollectEveryGem(run);
        Assert.False(run.World.Has<Hidden>(flame));
        Assert.Equal(0, Journey(run).Level);

        run.World.Teleport(Bird(run), atBeacon);
        run.Ticks(40);
        Assert.Equal(1, Journey(run).Level);
        Assert.Equal(0, Found(run));
        Assert.Contains(run.ShownText, shown => shown.Text == "The climb");
        Assert.True(Vector3.Distance(Position(run, Bird(run)), Levels.All[1].Start) < 0.1f);
    }

    [Fact]
    public void A_lift_carries_the_bird_across()
    {
        using var run = Start();
        var bird = Bird(run);
        var lift = run.World.Single<Lift>();
        var top = new Vector3(0f, 0.2f, 0f);

        run.World.Teleport(bird, Transform.At(Position(run, lift) + top));
        run.Ticks(5);
        float startX = Position(run, bird).X;
        run.Ticks(60);

        // Nothing is held, so only the lift moved the bird. In two seconds it's well on its way.
        var riding = Position(run, bird);
        Assert.True(riding.X - startX > 3f, $"the bird moved {riding.X - startX} m");
        Assert.InRange(riding.X - Position(run, lift).X, -0.3f, 0.3f);
        Assert.Equal(0, Journey(run).Falls);
    }

    [Fact]
    public void Walking_into_a_crate_pushes_it()
    {
        using var run = Start();
        var crate = run.World.Single<Crate>();
        var before = Position(run, crate);

        // Stand a metre and a half behind the crate and walk into it.
        run.World.Teleport(Bird(run), Transform.At(before + new Vector3(0f, -0.5f, 1.5f)));
        run.Input.Hold(Control.Move, new Vector2(0f, 1f));
        run.Ticks(30);

        var after = Position(run, crate);
        Assert.True(before.Z - after.Z > 0.5f, $"the crate moved {before.Z - after.Z} m");
    }

    [Fact]
    public void The_gem_on_the_rock_needs_the_crate()
    {
        using var run = Start();
        var bird = Bird(run);
        var besideRock = new Vector3(2.5f, 0f, -0.9f);

        run.World.Teleport(bird, Transform.Facing(besideRock, -Vector3.UnitZ));
        run.Input.Hold(Control.Move, new Vector2(0f, 1f));
        run.Input.Hold(Control.Jump);
        run.Ticks(40);
        Assert.InRange(Position(run, bird).Y, -0.05f, 0.05f);
        Assert.Equal(0, Found(run));

        // Now with the crate against the rock, and the bird standing on it. The bird steps aside first,
        // so the crate isn't dropped on top of it.
        run.Input.Release(Control.Jump);
        run.Input.Release(Control.Move);
        run.World.Teleport(bird, Transform.At(Levels.All[0].Start));
        run.Ticks(2);
        run.World.Teleport(run.World.Single<Crate>(), Transform.At(besideRock + new Vector3(0f, 0.5f, 0f)));
        run.World.Teleport(bird, Transform.Facing(besideRock + new Vector3(0f, 1.05f, 0f), -Vector3.UnitZ));
        run.Ticks(10);
        run.Input.Hold(Control.Move, new Vector2(0f, 1f));
        run.Input.Hold(Control.Jump);
        run.Ticks(16);
        run.Input.Release(Control.Move);
        run.Ticks(20);

        Assert.InRange(Position(run, bird).Y, 2.85f, 2.95f);
        Assert.Equal(1, Found(run));
    }

    [Fact]
    public void A_crate_that_falls_off_goes_home()
    {
        using var run = Start();
        var crate = run.World.Single<Crate>();
        var home = run.World.Get<Crate>(crate).Home;

        run.World.Teleport(crate, Transform.At(new Vector3(40f, 0f, 40f)));
        run.Ticks(90);

        Assert.True(Vector3.Distance(Position(run, crate), home) < 0.2f, $"the crate is at {Position(run, crate)}");
    }

    [Fact]
    public void Finishing_the_last_level_wins_and_saves_the_time()
    {
        using var run = Start(level: Levels.All.Count - 1);
        run.Ticks(50);
        FinishLastLevel(run);

        float seconds = Journey(run).Seconds;
        Assert.InRange(seconds, 2f, 4f);
        Assert.Equal(seconds, Records(run).BestSeconds);
        Assert.Contains(run.ShownText, shown => shown.Text == "You made it in 0:02");
    }

    [Fact]
    public void A_slower_run_keeps_the_old_best()
    {
        using var run = Start(level: Levels.All.Count - 1);
        Records(run).BestSeconds = 0.1f;
        FinishLastLevel(run);

        Assert.Equal(0.1f, Records(run).BestSeconds);
    }

    [Fact]
    public void Restart_goes_back_to_the_first_level_with_nothing_found()
    {
        using var run = Start(level: 1);
        CollectEveryGem(run);
        Assert.Equal(4, Found(run));

        run.Input.Press(Control.Restart);
        run.Ticks(3);

        Assert.Equal(0, Journey(run).Level);
        Assert.Equal(0, Found(run));
        Assert.Equal(5, run.World.Count<Gem>());
        Assert.InRange(Journey(run).Seconds, 0f, 0.2f);
    }
}
