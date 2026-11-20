using System.Numerics;
using Super;
using Super.Testing;

namespace Fox.Tests;

// The fox is from Khronos's samples and isn't in the repository, so CI, which has no assets folder, leaves these out.
[Trait("Needs", "Assets")]
public sealed class FoxTests
{
    [Fact]
    public void Every_paw_kicks_up_dust_once_a_stride()
    {
        using var run = HeadlessGame.Create<FoxGame>(1);

        run.Ticks(90);

        // At the start pace of 1.2 the blend is a third of the way from walk to run, so a stride takes
        // 2/3 * 0.7 + 1/3 * 7/6 = 0.856 seconds, played from the walk's four steps. The 89 ticks the game
        // has read events from are 3.47 strides: twelve steps, then the two that land before 0.47 of the next.
        Assert.Equal(14, run.Game.Steps);
    }

    [Fact]
    public void Every_paw_that_kicks_up_dust_sounds_a_step()
    {
        using var run = HeadlessGame.Create<FoxGame>(1);

        int sounded = 0;
        for (int tick = 0; tick < 90; tick++)
        {
            run.Ticks(1);
            sounded += run.Sounds.Count(sound => sound is { Clip: "sounds/step.wav", Channel: Channel.Effects, Seconds: 0 });
        }

        Assert.Equal(run.Game.Steps, sounded);
    }

    [Fact]
    public void The_dust_is_on_the_ground_under_the_fox()
    {
        using var run = HeadlessGame.Create<FoxGame>(1);

        run.Ticks(30);

        Assert.NotEqual(0, run.World.Count<Burst>());
        foreach (var puff in run.World.Query<Burst, Transform>())
        {
            var at = puff.Second.Position;
            Assert.Equal(0f, at.Y);

            // The path is a circle four metres out from the middle, and the fox is about a metre long.
            float fromPath = MathF.Abs(new Vector2(at.X, at.Z).Length() - 4f);
            Assert.True(fromPath < 0.5f, $"A puff landed {fromPath} metres off the fox's path.");
        }
    }
}
