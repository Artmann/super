using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class BurstTests
{
    private const float TickSeconds = 0.5f;

    // Eight particles that sit still for a second, all born at once.
    private static readonly ParticleEmitter Puff = new(count: 8, lifetime: 1f, lifetimeSpread: 0f);

    /// <summary>The particles on screen once this many ticks have been played.</summary>
    private static Particle[] Shown(World world, long ticksPlayed)
    {
        var view = new View();
        view.Begin(0f, tick: ticksPlayed, tickSeconds: TickSeconds);
        WorldFrame.Describe(view, world);
        return view.Effects.DrawnParticles.ToArray();
    }

    private sealed class Popper(World world) : IGame
    {
        // Thirty ticks a second, so this plays for three ticks.
        private static readonly ParticleEmitter Quick = new(count: 4, lifetime: 0.1f, lifetimeSpread: 0f);

        public void Update(GameTime time)
        {
            if (time.Tick == 2)
            {
                world.Spawn(Transform.Identity, new Burst(Quick));
            }
        }
    }

    [Fact]
    public void A_burst_shows_its_particles_at_its_entity()
    {
        var world = new World();
        var at = new Vector3(5f, 2f, -3f);
        world.Spawn(Transform.At(at), new Burst(Puff));
        WorldBursts.Advance(world, tick: 4, TickSeconds);

        // Started on tick 4 and shown after tick 5, it's half a second old.
        var particles = Shown(world, ticksPlayed: 6);

        Assert.Equal(8, particles.Length);
        Assert.All(particles, particle => Assert.True(Vector3.Distance(particle.Position, at) < 0.01f, $"{particle.Position}"));
    }

    [Fact]
    public void A_burst_follows_its_parent()
    {
        var world = new World();
        var parent = world.Spawn(Transform.At(new Vector3(10f, 0f, 0f)));
        world.Spawn(Transform.At(new Vector3(0f, 2f, 0f)), new Parent(parent), new Burst(Puff));
        WorldBursts.Advance(world, tick: 4, TickSeconds);

        var particles = Shown(world, ticksPlayed: 6);

        Assert.Equal(8, particles.Length);
        Assert.All(particles, particle => Assert.True(Vector3.Distance(particle.Position, new Vector3(10f, 2f, 0f)) < 0.01f, $"{particle.Position}"));
    }

    [Fact]
    public void A_burst_that_hasnt_started_shows_nothing()
    {
        var world = new World();
        world.Spawn(Transform.Identity, new Burst(Puff));

        Assert.Empty(Shown(world, ticksPlayed: 6));
    }

    [Fact]
    public void A_hidden_burst_shows_nothing()
    {
        var world = new World();
        world.Spawn(Transform.Identity, new Burst(Puff), new Hidden());
        WorldBursts.Advance(world, tick: 4, TickSeconds);

        Assert.Empty(Shown(world, ticksPlayed: 6));
    }

    [Fact]
    public void Two_bursts_of_one_effect_scatter_differently()
    {
        var scattered = new ParticleEmitter(count: 8, lifetime: 1f, lifetimeSpread: 0f).Outward(2f);
        var world = new World();
        world.Spawn(Transform.Identity, new Burst(scattered));
        world.Spawn(Transform.Identity, new Burst(scattered));
        WorldBursts.Advance(world, tick: 4, TickSeconds);

        var particles = Shown(world, ticksPlayed: 6);

        Assert.Equal(16, particles.Length);
        Assert.Equal(16, particles.Select(particle => particle.Position).Distinct().Count());
    }

    [Fact]
    public void A_burst_destroys_its_entity_once_it_has_played()
    {
        var world = new World();
        var burst = world.Spawn(Transform.Identity, new Burst(Puff));
        var other = world.Spawn(Transform.Identity);

        // It starts on tick 4 and lasts a second, which is two ticks.
        WorldBursts.Advance(world, tick: 4, TickSeconds);
        WorldBursts.Advance(world, tick: 5, TickSeconds);
        Assert.True(world.IsAlive(burst));

        WorldBursts.Advance(world, tick: 6, TickSeconds);

        Assert.False(world.IsAlive(burst));
        Assert.True(world.IsAlive(other));
    }

    [Fact]
    public void The_game_loop_starts_a_burst_and_clears_it_away()
    {
        using var run = HeadlessGame.Create<Popper>(seed: 1);

        int ticksThere = 0;
        for (int tick = 0; tick < 20; tick++)
        {
            run.Ticks(1);
            ticksThere += run.World.Count<Burst>();
        }

        // There from the tick it's spawned in, and gone at the end of the third tick after.
        Assert.Equal(3, ticksThere);
    }
}
