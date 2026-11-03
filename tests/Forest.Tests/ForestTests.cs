using Forest;
using Super;
using Super.Ecs;
using Super.Testing;

namespace Forest.Tests;

public sealed class ForestTests
{
    private const int TicksPerSecond = 30;

    private static HeadlessRun<ForestGame> Start(int seed = 1) =>
        HeadlessGame.Create<ForestGame>(seed, services => services.AddSuperEcs());

    private static bool Hammering(PlayingSound sound) => sound.Clip == "sounds/hammering.wav";

    private static void RunUntil(HeadlessRun<ForestGame> run, bool building)
    {
        for (int i = 0; i < 1200 && run.Game.Builder.IsBuilding != building; i++)
        {
            run.Ticks(1);
        }

        Assert.Equal(building, run.Game.Builder.IsBuilding);
    }

    [Fact]
    public void ForestAmbienceAndMusicPlayFromTheStart()
    {
        using var run = Start();
        run.Ticks(1);

        var ambience = run.Sounds.Where(sound => sound.Channel == Channel.Ambience).Select(sound => sound.Clip).Order().ToList();
        Assert.Equal(["sounds/ambient/rain.wav", "sounds/ambient/wind.ogg"], ambience);
        var music = Assert.Single(run.Sounds, sound => sound.Channel == Channel.Music);
        Assert.Equal("music/clearing.ogg", music.Clip);
        Assert.True(music.Looping);
    }

    [Fact]
    public void HammeringPlaysOnlyWhileBuilding()
    {
        using var run = Start();
        run.Ticks(2);
        Assert.True(run.Game.Builder.IsBuilding);
        var hammering = Assert.Single(run.Sounds, Hammering);
        Assert.Equal(Channel.Effects, hammering.Channel);

        RunUntil(run, building: false);
        run.Ticks(TicksPerSecond);
        Assert.DoesNotContain(run.Sounds, Hammering);

        RunUntil(run, building: true);
        run.Ticks(2);
        Assert.Contains(run.Sounds, Hammering);
    }

    [Fact]
    public void CameraOrbitPansTheHammering()
    {
        using var run = Start();
        run.Game.OrbitSpeed = 0;

        // The builder works 4 m to the side of the cabin, at +x, and the camera is 16 m out. A camera looking at the
        // cabin from +z has the builder on its right, about 4 m across in 16 m, so a pan of about 0.24.
        run.Game.Camera.Yaw = 0;
        run.Ticks(3);
        Assert.True(Assert.Single(run.Sounds, Hammering).Pan > 0.2f);

        // From -z the camera faces the other way, so the builder is on its left.
        run.Game.Camera.Yaw = MathF.PI;
        run.Ticks(3);
        Assert.True(Assert.Single(run.Sounds, Hammering).Pan < -0.2f);
    }

    [Theory, Seeds(15)]
    public void ForestKeepsItsAmbienceAndMusicAndHammersOnlyWhileBuildingForAMinute(int seed)
    {
        // A hammering sound fades out over 0.3 s after the builder stops, so it may linger this many ticks.
        const int FadeOutTicks = 12;
        using var run = Start(seed);
        int sinceBuilding = 0;
        for (int tick = 0; tick < 1800; tick++)
        {
            run.Ticks(1);
            sinceBuilding = run.Game.Builder.IsBuilding ? 0 : sinceBuilding + 1;

            Assert.Contains(run.Sounds, sound => sound.Clip == "sounds/ambient/rain.wav" && sound.Channel == Channel.Ambience);
            Assert.Contains(run.Sounds, sound => sound.Clip == "sounds/ambient/wind.ogg" && sound.Channel == Channel.Ambience);
            Assert.Contains(run.Sounds, sound => sound.Clip == "music/clearing.ogg" && sound.Channel == Channel.Music);
            Assert.True(run.Sounds.Count(Hammering) <= 1, $"more than one hammering at tick {tick}");
            if (sinceBuilding > FadeOutTicks)
            {
                Assert.DoesNotContain(run.Sounds, Hammering);
            }
        }
    }
}
