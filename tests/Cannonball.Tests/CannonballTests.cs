using System.Numerics;
using Super;
using Super.Physics;
using Super.Testing;

namespace Cannonball.Tests;

public sealed class CannonballTests
{
    private const int TicksPerSecond = 30;

    private static HeadlessRun<CannonballGame> Start(int seed = 1) =>
        HeadlessGame.Create<CannonballGame>(seed, services => services.AddSuperPhysics());

    /// <summary>Plays until the stuntman reaches a stage, and returns where his hips were the tick before.</summary>
    private static Vector3 RunUntil(HeadlessRun<CannonballGame> run, Stage stage, float seconds = 20f)
    {
        var hips = run.Game.Hips;
        for (int i = 0; i < seconds * TicksPerSecond && run.Game.Stage != stage; i++)
        {
            hips = run.Game.Hips;
            run.Ticks(1);
        }

        Assert.Equal(stage, run.Game.Stage);
        return hips;
    }

    private static float Across(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_shot_lands_in_the_field_hard_enough_to_go_limp()
    {
        using var run = Start();
        var loaded = run.Game.Hips;

        RunUntil(run, Stage.Flying);
        RunUntil(run, Stage.Tumbling);

        Assert.True(run.Game.HitSpeed >= StuntFlight.KnockoutSpeed, $"the hit was only {run.Game.HitSpeed} m/s");
        Assert.True(run.Game.Hips.Z - loaded.Z > 15f, $"he only flew to {run.Game.Hips}");
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void He_gets_up_from_where_he_lay_and_stands_before_the_next_shot()
    {
        using var run = Start();
        RunUntil(run, Stage.Tumbling);

        var lying = RunUntil(run, Stage.GettingUp);
        Assert.True(Across(lying, run.Game.Hips) < 0.15f, $"he lay at {lying} but gets up from {run.Game.Hips}");

        // Once the ragdoll has blended out he is still on the ground, partway through the get-up clip.
        run.Ticks(TicksPerSecond * 3 / 4);
        Assert.True(run.Game.Hips.Y < 0.7f, $"his hips are already {run.Game.Hips.Y} m up, so he never played the get-up");

        var standing = run.Game.Up;
        var hips = run.Game.Hips;
        for (int i = 0; i < 20 * TicksPerSecond && run.Game.Stage == Stage.GettingUp; i++)
        {
            (standing, hips) = (run.Game.Up, run.Game.Hips);
            run.Ticks(1);
        }

        Assert.Equal(Stage.Loaded, run.Game.Stage);
        Assert.True(standing.Y > 0.9f, $"he ended the get-up leaning {standing}");
        // The get-up from the front crawls the hips about 1.2 m on its own, so allow for that and no more.
        Assert.True(Across(lying, hips) < 1.5f,$"he got up at {hips}, {Across(lying, hips)} m from where he lay at {lying}");
    }

    [Fact]
    public void R_loads_him_back_into_the_cannon()
    {
        using var run = Start();
        var loaded = run.Game.Hips;
        RunUntil(run, Stage.Flying);
        run.Ticks(TicksPerSecond / 2);

        run.Input.Press(Key.R);
        run.Ticks(1);

        Assert.Equal(Stage.Loaded, run.Game.Stage);
        Assert.True(Vector3.Distance(loaded, run.Game.Hips) < 0.1f, $"he's at {run.Game.Hips}, not in the cannon at {loaded}");
    }
}
