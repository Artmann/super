using System.Numerics;
using Rendition;

namespace Super.Tests;

public class EmitterTests
{
    private const float TickSeconds = 0.5f;

    // Particles that sit still, half a second each, born over one second.
    private static readonly ParticleEffect Puffs = new ParticleEmitter(count: 8, lifetime: 0.5f, lifetimeSpread: 0f).Over(1f);

    private static View Describe(World world, double seconds)
    {
        var view = new View();

        // The time on screen is (tick - 1 + blend) * tick seconds.
        view.Begin(0f, tick: (long)(seconds / TickSeconds) + 1, tickSeconds: TickSeconds);
        WorldFrame.Describe(view, world);
        return view;
    }

    [Fact]
    public void An_emitter_shows_particles_at_its_entity()
    {
        var world = new World();
        var at = new Vector3(5f, 2f, -3f);
        world.Spawn(Transform.At(at), new Emitter(Puffs));

        var particles = Describe(world, 0.5).Effects.DrawnParticles.ToArray();

        Assert.NotEmpty(particles);
        Assert.All(particles, particle => Assert.True(Vector3.Distance(particle.Position, at) < 0.01f, $"{particle.Position}"));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(10.5)]
    [InlineData(100.5)]
    public void An_emitter_never_runs_out(double seconds)
    {
        var world = new World();
        world.Spawn(Transform.Identity, new Emitter(Puffs));

        Assert.NotEmpty(Describe(world, seconds).Effects.DrawnParticles.ToArray());
    }

    [Fact]
    public void An_emitter_looks_the_same_at_the_same_time()
    {
        var moving = new ParticleEmitter(count: 16, lifetime: 2f).Over(1f).Velocity(Vector3.UnitY).Scatter(1f);

        Vector3[] Positions()
        {
            var world = new World();
            world.Spawn(Transform.Identity, new Emitter(moving, Every: 0.5f));
            var particles = Describe(world, 7.5).Effects.DrawnParticles.ToArray();
            return [.. particles.Select(particle => particle.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)];
        }

        var first = Positions();

        Assert.NotEmpty(first);
        Assert.Equal(first, Positions());
    }

    [Fact]
    public void A_hidden_emitter_shows_nothing()
    {
        var world = new World();
        var parent = world.Spawn(Transform.Identity, new Hidden());
        world.Spawn(Transform.Identity, new Parent(parent), new Emitter(Puffs));

        Assert.Empty(Describe(world, 0.5).Effects.DrawnParticles.ToArray());
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Every_must_be_more_than_zero(float every)
    {
        var world = new World();
        world.Spawn(Transform.Identity, new Emitter(Puffs, every));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Describe(world, 0.5));

        Assert.StartsWith("An emitter's Every must be more than zero seconds.", error.Message);
    }
}
