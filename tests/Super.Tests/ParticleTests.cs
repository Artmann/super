using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class ParticleTests
{
    /// <summary>One particle that lives long and flies straight, so its sums can be worked by hand.</summary>
    private static readonly ParticleEffect Single = new()
    {
        Count = 1,
        Lifetime = 10f,
        LifetimeSpread = 0f,
        SizeSpread = 0f,
        FadeIn = 0f,
        FadeOut = 0f,
        Opacity = 1f,
    };

    private static Particle[] Draw(ParticleEffect effect, float age, int key = 7, Matrix4x4? transform = null)
    {
        var view = new View();
        view.Begin(0f);
        view.Particles(key, effect, transform ?? Matrix4x4.Identity, age);
        return view.DrawnParticles.ToArray();
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"Expected {expected} but got {actual}.");

    [Fact]
    public void Drag_slows_the_start_speed_while_gravity_pulls()
    {
        // With drag ln 2, speed halves every second. After one second the start speed has
        // carried it 0.5 / ln 2 = 0.72135 m, and gravity of 2 m/s² has moved it
        // 2 * (1 - 0.72135) / ln 2 = 0.80402 m.
        var effect = Single with { Velocity = new Vector3(1f, 0f, 0f), Drag = MathF.Log(2f), Gravity = new Vector3(0f, 2f, 0f) };

        var particle = Assert.Single(Draw(effect, 1f, transform: Matrix4x4.CreateTranslation(10f, 0f, 0f)));

        Near(new Vector3(10.72135f, 0.80402f, 0f), particle.Position);
    }

    [Fact]
    public void Without_drag_particles_fly_as_thrown()
    {
        // 2 m/s for 1.5 s is 3 m across; falling at 4 m/s² for 1.5 s drops it 4.5 m.
        var effect = Single with { Velocity = new Vector3(2f, 0f, 0f), Gravity = new Vector3(0f, -4f, 0f) };

        var particle = Assert.Single(Draw(effect, 1.5f));

        Near(new Vector3(3f, -4.5f, 0f), particle.Position);
    }

    [Fact]
    public void A_transform_turns_the_speed_but_not_gravity()
    {
        // A quarter turn about z points the start speed straight up. Gravity of 1 m/s² along
        // world x still moves it 0.5 m along x in one second.
        var effect = Single with { Velocity = new Vector3(1f, 0f, 0f), Gravity = new Vector3(1f, 0f, 0f) };

        var particle = Assert.Single(Draw(effect, 1f, transform: Matrix4x4.CreateRotationZ(MathF.PI / 2f)));

        Near(new Vector3(0.5f, 1f, 0f), particle.Position);
    }

    [Fact]
    public void Size_grows_fast_at_first_then_slows()
    {
        // Halfway through its life a particle has done three quarters of its growth: 1 + 0.75 * 2.
        var effect = Single with { StartSize = 1f, EndSize = 3f, Lifetime = 2f };

        var particle = Assert.Single(Draw(effect, 1f));

        Assert.Equal(2.5f, particle.Size, 4);
    }

    [Fact]
    public void Color_blends_from_start_to_end_over_the_life()
    {
        var effect = Single with { StartColor = new Vector3(0.8f, 0.4f, 0f), EndColor = new Vector3(0.2f, 0.4f, 1f), Lifetime = 2f };

        var particle = Assert.Single(Draw(effect, 0.5f));

        Near(new Vector3(0.65f, 0.4f, 0.25f), new Vector3(particle.Color.X, particle.Color.Y, particle.Color.Z));
    }

    [Theory]
    [InlineData(0.2f, 0.4f)]  // a tenth of its life: halfway through fading in
    [InlineData(1.0f, 0.8f)]  // halfway: fully there, just before it fades
    [InlineData(1.5f, 0.4f)]  // three quarters: halfway through fading out
    public void Opacity_fades_in_and_out(float age, float opacity)
    {
        var effect = Single with { Lifetime = 2f, Opacity = 0.8f, FadeIn = 0.2f, FadeOut = 0.5f };

        var particle = Assert.Single(Draw(effect, age));

        Assert.Equal(opacity, particle.Color.W, 4);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.3f)]
    [InlineData(5f)]
    public void Nothing_is_drawn_before_the_start_or_after_the_last_particle_dies(float age)
    {
        var effect = new ParticleEffect { Lifetime = 1f, LifetimeSpread = 0.3f };

        Assert.Equal(1.3f, effect.Duration, 5);
        Assert.Empty(Draw(effect, age));
    }

    [Fact]
    public void Every_particle_is_alive_until_the_shortest_life_ends()
    {
        var effect = new ParticleEffect { Count = 30, Lifetime = 1f, LifetimeSpread = 0.3f };

        Assert.Equal(30, Draw(effect, 0f).Length);
        Assert.Equal(30, Draw(effect, 0.69f).Length);
    }

    [Fact]
    public void Lifetimes_differ_so_a_burst_thins_out_instead_of_vanishing_at_once()
    {
        var effect = new ParticleEffect { Count = 30, Lifetime = 1f, LifetimeSpread = 0.3f };

        int left = Draw(effect, 1f).Length;

        Assert.InRange(left, 1, 29);
    }

    [Fact]
    public void Births_spread_evenly_over_the_emit_time()
    {
        // Ten births over one second: the nth is born in its own tenth of the second, so
        // exactly five have been born by half a second.
        var effect = new ParticleEffect { Count = 10, Lifetime = 10f, EmitSeconds = 1f };

        Assert.Equal(5, Draw(effect, 0.5f).Length);
    }

    [Fact]
    public void The_same_key_and_age_always_give_the_same_particles()
    {
        var effect = new ParticleEffect { Scatter = 1f, Outward = 1f, Spawn = ParticleSpawn.Disc(1f) };

        var first = Draw(effect, 0.4f);
        var second = Draw(effect, 0.4f);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_keys_and_seeds_give_different_patterns()
    {
        var effect = new ParticleEffect { Scatter = 1f, Outward = 1f, Spawn = ParticleSpawn.Disc(1f) };

        var one = Draw(effect, 0.4f, key: 1);
        var two = Draw(effect, 0.4f, key: 2);
        var reseeded = Draw(effect with { Seed = 5 }, 0.4f, key: 1);

        Assert.NotEqual(one[0].Position, two[0].Position);
        Assert.NotEqual(one[0].Position, reseeded[0].Position);
    }

    [Fact]
    public void A_line_from_and_to_covers_the_span_and_keeps_speeds_their_size()
    {
        // From the origin to (3, 0, 4) is 5 m. A start speed of 1 m/s along the line moves
        // each particle 1 m along it in a second, not 5 m.
        var effect = new ParticleEffect
        {
            Count = 40,
            Lifetime = 10f,
            Spawn = ParticleSpawn.Line(),
            Velocity = new Vector3(1f, 0f, 0f),
        };
        var to = new Vector3(3f, 0f, 4f);
        var direction = to / 5f;

        var view = new View();
        view.Begin(0f);
        view.Particles(3, effect, Vector3.Zero, to, 0f);
        var born = view.DrawnParticles.ToArray();
        view.Begin(0f);
        view.Particles(3, effect, Vector3.Zero, to, 1f);
        var later = view.DrawnParticles.ToArray();

        var along = born.Select(p => Vector3.Dot(p.Position, direction)).ToArray();
        foreach (var particle in born)
        {
            float distanceOff = (particle.Position - direction * Vector3.Dot(particle.Position, direction)).Length();
            Assert.True(distanceOff < 1e-4f, $"{particle.Position} is off the line.");
        }

        Assert.InRange(along.Min(), 0f, 1f);
        Assert.InRange(along.Max(), 4f, 5f);
        Assert.Equal(born.Length, later.Length);
        for (int i = 0; i < born.Length; i++)
        {
            Near(born[i].Position + direction, later[i].Position);
        }
    }

    [Fact]
    public void Begin_forgets_the_last_frames_particles()
    {
        var view = new View();
        view.Begin(0f);
        view.Particles(1, new ParticleEffect(), Vector3.Zero, 0.1f);
        Assert.NotEqual(0, view.DrawnParticles.Length);

        view.Begin(0f);

        Assert.Equal(0, view.DrawnParticles.Length);
    }

    [Fact]
    public void Too_few_particles_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleEffect { Count = 0 });

        Assert.Equal("Count", error.ParamName);
        Assert.Contains("An effect needs 1 to 32768 particles.", error.Message);
    }

    [Fact]
    public void A_lifetime_of_zero_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleEffect { Lifetime = 0f });

        Assert.Contains("A particle's lifetime must be more than zero seconds.", error.Message);
    }

    [Fact]
    public void Opacity_above_one_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleEffect { Opacity = 1.5f });

        Assert.Contains("Opacity must be from 0 to 1.", error.Message);
    }

    [Fact]
    public void A_negative_size_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleEffect { EndSize = -1f });

        Assert.Contains("EndSize can't be negative.", error.Message);
    }

    [Fact]
    public void A_negative_spawn_box_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ParticleSpawn.Box(new Vector3(1f, -1f, 1f)));

        Assert.Contains("A spawn box can't be smaller than nothing.", error.Message);
    }

    [Fact]
    public void Particles_are_sorted_furthest_from_the_camera_first()
    {
        // Looking down -z from the origin, z = -5 is furthest and z = -1 nearest.
        Particle At(float z) => new() { Position = new Vector3(0f, 0f, z) };
        Particle[] particles = [At(-1f), At(-5f), At(-3f)];
        var sorted = new Particle[3];

        ParticlePass.SortBackToFront(particles, Vector3.Zero, -Vector3.UnitZ, new float[3], new int[3], sorted);

        Assert.Equal([-5f, -3f, -1f], sorted.Select(p => p.Position.Z));
    }

    [Fact]
    public void Particle_matches_the_vertex_layout_the_shader_reads()
    {
        Assert.Equal(40, Marshal.SizeOf<Particle>());
        Assert.Equal(0, (int)Marshal.OffsetOf<Particle>(nameof(Particle.Position)));
        Assert.Equal(12, (int)Marshal.OffsetOf<Particle>(nameof(Particle.Size)));
        Assert.Equal(16, (int)Marshal.OffsetOf<Particle>(nameof(Particle.Color)));
        Assert.Equal(32, (int)Marshal.OffsetOf<Particle>(nameof(Particle.Rotation)));
        Assert.Equal(36, (int)Marshal.OffsetOf<Particle>(nameof(Particle.Variant)));
    }
}
