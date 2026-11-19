using Microsoft.Extensions.DependencyInjection;
using Super.Testing;

namespace Super.Tests;

public class SystemsTests
{
    private record struct Wave(int Number);

    /// <summary>What each system wrote, in order, kept in a service the test registers.</summary>
    private sealed class Log
    {
        public List<string> Lines { get; } = [];
    }

    private sealed class Waves(Log log) : ISystem
    {
        public void Update(GameTime time) => log.Lines.Add($"Waves {time.Tick}");
    }

    private sealed class Towers(Log log) : ISystem
    {
        public void Update(GameTime time) => log.Lines.Add($"Towers {time.Tick}");
    }

    /// <summary>Spawns a wave through the world it was handed.</summary>
    private sealed class Spawner(World world) : ISystem
    {
        public void Update(GameTime time) => world.Spawn(new Wave((int)time.Tick));
    }

    /// <summary>Adds Towers before Waves, and runs them only when <see cref="Running"/> is set.</summary>
    private sealed class Outpost(Systems systems, Log log) : IGame
    {
        private readonly Systems _systems = systems.Add<Towers>().Add<Waves>();

        public Log Log => log;

        public Systems Systems => systems;

        public bool Running { get; set; } = true;

        public void Update(GameTime time)
        {
            if (Running)
            {
                _systems.Update(time);
            }
        }
    }

    private static HeadlessRun<Outpost> Start() =>
        HeadlessGame.Create<Outpost>(1, services => services.AddSingleton<Log>());

    [Fact]
    public void Update_runs_systems_in_the_order_they_were_added()
    {
        using var run = Start();

        run.Ticks(2);

        Assert.Equal(["Towers 0", "Waves 0", "Towers 1", "Waves 1"], run.Game.Log.Lines);
    }

    [Fact]
    public void A_system_gets_its_services_without_being_registered()
    {
        using var run = HeadlessGame.Create<Empty>(1);
        var systems = run.Game.Systems.Add<Spawner>();

        systems.Update(run.Time);

        int waves = 0;
        foreach (var _ in run.World.Query<Wave>())
        {
            waves++;
        }

        Assert.Equal(1, waves);
    }

    [Fact]
    public void Adding_a_system_twice_throws()
    {
        using var run = Start();

        var error = Assert.Throws<InvalidOperationException>(() => run.Game.Systems.Add<Waves>());

        Assert.Equal("Waves is added already.", error.Message);
    }

    [Fact]
    public void Nothing_runs_unless_the_game_calls_Update()
    {
        using var run = Start();
        run.Game.Running = false;

        run.Ticks(3);

        Assert.Empty(run.Game.Log.Lines);
    }

    private sealed class Empty(Systems systems) : IGame
    {
        public Systems Systems => systems;

        public void Update(GameTime time)
        {
        }
    }
}
