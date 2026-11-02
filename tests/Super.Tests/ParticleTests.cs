using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class ParticleTests
{
    /// <summary>One particle that lives ten seconds exactly, so its sums can be worked by hand.</summary>
    private static readonly ParticleEmitter Single = new(count: 1, lifetime: 10f, lifetimeSpread: 0f);

    private static Particle[] Draw(ParticleEffect effect, float age, int key = 7, Matrix4x4? transform = null)
    {
        var view = new View();
        view.Begin(0f);
        view.Particles(key, effect, transform ?? Matrix4x4.Identity, age);
        return view.DrawnParticles.ToArray();
    }

    private static Particle[] DrawLine(ParticleEffect effect, Vector3 from, Vector3 to, float age, int key = 3)
    {
        var view = new View();
        view.Begin(0f);
        view.Particles(key, effect, from, to, age);
        return view.DrawnParticles.ToArray();
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"Expected {expected} but got {actual}.");

    private static Vector3 Rgb(Particle particle) => new(particle.Color.X, particle.Color.Y, particle.Color.Z);

    [Fact]
    public void Drag_slows_the_start_speed_while_gravity_pulls()
    {
        // With drag ln 2, speed halves every second. After one second the start speed has
        // carried it 0.5 / ln 2 = 0.72135 m, and gravity of 2 m/s² has moved it
        // 2 * (1 - 0.72135) / ln 2 = 0.80402 m.
        var emitter = Single.Velocity(new Vector3(1f, 0f, 0f)).Drag(MathF.Log(2f)).Gravity(new Vector3(0f, 2f, 0f));

        var particle = Assert.Single(Draw(emitter, 1f, transform: Matrix4x4.CreateTranslation(10f, 0f, 0f)));

        Near(new Vector3(10.72135f, 0.80402f, 0f), particle.Position);
    }

    [Fact]
    public void Without_drag_particles_fly_as_thrown()
    {
        // 2 m/s for 1.5 s is 3 m across; falling at 4 m/s² for 1.5 s drops it 4.5 m.
        var emitter = Single.Velocity(new Vector3(2f, 0f, 0f)).Gravity(new Vector3(0f, -4f, 0f));

        var particle = Assert.Single(Draw(emitter, 1.5f));

        Near(new Vector3(3f, -4.5f, 0f), particle.Position);
    }

    [Fact]
    public void Speeds_and_forces_add_up()
    {
        // Two speeds of 1 m/s along x make 2 m/s; two pulls of 1 m/s² down make 2, so a
        // second later it is 2 m across and 1 m down.
        var emitter = Single
            .Velocity(new Vector3(1f, 0f, 0f)).Gravity(new Vector3(0f, -1f, 0f))
            .Velocity(new Vector3(1f, 0f, 0f)).Gravity(new Vector3(0f, -1f, 0f));

        var particle = Assert.Single(Draw(emitter, 1f));

        Near(new Vector3(2f, -1f, 0f), particle.Position);
    }

    [Fact]
    public void Wind_carries_particles_along_as_drag_catches_them()
    {
        // Still at first, in wind of 2 m/s with drag ln 2. Its speed closes half the gap to
        // the wind's each second, so it has gone 2 * (1 - 0.5 / ln 2) = 0.55730 m after one.
        var emitter = Single.Drag(MathF.Log(2f)).Wind(new Vector3(2f, 0f, 0f));

        var particle = Assert.Single(Draw(emitter, 1f));

        Near(new Vector3(0.55730f, 0f, 0f), particle.Position);
    }

    [Fact]
    public void A_transform_turns_the_speed_but_not_gravity()
    {
        // A quarter turn about z points the start speed straight up. Gravity of 1 m/s² along
        // world x still moves it 0.5 m along x in one second.
        var emitter = Single.Velocity(new Vector3(1f, 0f, 0f)).Gravity(new Vector3(1f, 0f, 0f));

        var particle = Assert.Single(Draw(emitter, 1f, transform: Matrix4x4.CreateRotationZ(MathF.PI / 2f)));

        Near(new Vector3(0.5f, 1f, 0f), particle.Position);
    }

    [Fact]
    public void A_cone_with_no_spread_shoots_straight_along_its_direction()
    {
        // 2 m/s at 75% to 125%, so 1.5 to 2.5 m along x after one second, and nowhere else.
        var emitter = Single.Cone(new Vector3(3f, 0f, 0f), angle: 0f, speed: 2f);

        var particle = Assert.Single(Draw(emitter, 1f));

        Assert.InRange(particle.Position.X, 1.5f, 2.5f);
        Assert.Equal(0f, particle.Position.Y, 4);
        Assert.Equal(0f, particle.Position.Z, 4);
    }

    [Fact]
    public void Swirl_turns_particles_round_the_upright_axis()
    {
        // Thrown 1 m along x, then a quarter turn about +y, which takes x to -z.
        var emitter = Single.Velocity(new Vector3(1f, 0f, 0f)).Swirl(0.25f);

        var particle = Assert.Single(Draw(emitter, 1f));

        Near(new Vector3(0f, 0f, -1f), particle.Position);
    }

    [Fact]
    public void Wobble_starts_where_the_shape_puts_a_particle_then_sways()
    {
        var emitter = Single.Wobble(1f, 1f);

        var born = Assert.Single(Draw(emitter, 0f));
        var later = Assert.Single(Draw(emitter, 0.3f));

        Near(Vector3.Zero, born.Position);
        Assert.True(later.Position.Length() > 0.01f, $"{later.Position} hasn't moved.");
    }

    [Fact]
    public void Size_grows_fast_at_first_then_slows()
    {
        // Halfway through its life a particle has done seven eighths of its growth under a
        // cubic ease out: 1 + 0.875 * 2.
        var emitter = Single.WithLifetime(2f, spread: 0f).Size(1f, 3f, spread: 0f);

        var particle = Assert.Single(Draw(emitter, 1f));

        Assert.Equal(2.75f, particle.Size, 4);
    }

    [Fact]
    public void Linear_size_grows_evenly()
    {
        var emitter = Single.WithLifetime(2f, spread: 0f).Size(1f, 3f, Easing.Linear, spread: 0f);

        var particle = Assert.Single(Draw(emitter, 1f));

        Assert.Equal(2f, particle.Size, 4);
    }

    [Fact]
    public void Color_blends_from_start_to_end_over_the_life()
    {
        var emitter = Single.WithLifetime(2f, spread: 0f).Color(new Vector3(0.8f, 0.4f, 0f), new Vector3(0.2f, 0.4f, 1f));

        var particle = Assert.Single(Draw(emitter, 0.5f));

        Near(new Vector3(0.65f, 0.4f, 0.25f), Rgb(particle));
    }

    [Fact]
    public void A_gradient_blends_between_the_stops_either_side()
    {
        // Three quarters through the life is halfway from the stop at 0.5 to the one at 1.
        var emitter = Single.WithLifetime(4f, spread: 0f).Gradient(
            (0f, new Vector3(1f, 0f, 0f)),
            (0.5f, new Vector3(0f, 1f, 0f)),
            (1f, new Vector3(0f, 0f, 1f)));

        var particle = Assert.Single(Draw(emitter, 3f));

        Near(new Vector3(0f, 0.5f, 0.5f), Rgb(particle));
    }

    [Fact]
    public void Looks_multiply()
    {
        var emitter = Single.Opacity(0.5f).Opacity(0.5f).Color(new Vector3(0.5f)).Color(new Vector3(0.5f, 1f, 1f));

        var particle = Assert.Single(Draw(emitter, 1f));

        Assert.Equal(0.25f, particle.Color.W, 5);
        Near(new Vector3(0.25f, 0.5f, 0.5f), Rgb(particle));
    }

    [Fact]
    public void A_bare_emitter_draws_white_solid_particles_a_metre_wide()
    {
        var particle = Assert.Single(Draw(Single, 1f));

        Assert.Equal(new Vector4(1f), particle.Color);
        Assert.Equal(1f, particle.Size);
    }

    [Theory]
    [InlineData(0.2f, 0.4f)]  // a tenth of its life: halfway through fading in
    [InlineData(1.0f, 0.8f)]  // halfway: fully there, before it fades
    [InlineData(1.6f, 0.4f)]  // four fifths: halfway through the last 40% fading out
    public void Opacity_fades_in_and_out(float age, float opacity)
    {
        var emitter = Single.WithLifetime(2f, spread: 0f).Opacity(0.8f).Fade(0.2f, 0.4f);

        var particle = Assert.Single(Draw(emitter, age));

        Assert.Equal(opacity, particle.Color.W, 4);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.3f)]
    [InlineData(5f)]
    public void Nothing_is_drawn_before_the_start_or_after_the_last_particle_dies(float age)
    {
        var emitter = new ParticleEmitter(count: 24, lifetime: 1f, lifetimeSpread: 0.3f);

        Assert.Equal(1.3f, emitter.Duration, 5);
        Assert.Empty(Draw(emitter, age));
    }

    [Fact]
    public void Every_particle_is_alive_until_the_shortest_life_ends()
    {
        var emitter = new ParticleEmitter(count: 30, lifetime: 1f, lifetimeSpread: 0.3f);

        Assert.Equal(30, Draw(emitter, 0f).Length);
        Assert.Equal(30, Draw(emitter, 0.69f).Length);
    }

    [Fact]
    public void Lifetimes_differ_so_a_burst_thins_out_instead_of_vanishing_at_once()
    {
        var emitter = new ParticleEmitter(count: 30, lifetime: 1f, lifetimeSpread: 0.3f);

        int left = Draw(emitter, 1f).Length;

        Assert.InRange(left, 1, 29);
    }

    [Fact]
    public void Births_spread_evenly_over_the_time_given()
    {
        // Ten births over one second: the nth is born in its own tenth of the second, so
        // exactly five have been born by half a second.
        var emitter = new ParticleEmitter(count: 10, lifetime: 10f).Over(1f);

        Assert.Equal(5, Draw(emitter, 0.5f).Length);
        // The last birth at 1 s, then up to 130% of a ten second life.
        Assert.Equal(14f, emitter.Duration, 5);
    }

    [Theory]
    [InlineData(0.5f, 2)]  // the first pulse
    [InlineData(1.5f, 4)]  // and the second, a second later
    [InlineData(2.5f, 6)]  // and the third
    public void Pulses_share_the_particles_between_bursts(float age, int alive)
    {
        var emitter = new ParticleEmitter(count: 6, lifetime: 10f, lifetimeSpread: 0f).Pulses(3, every: 1f);

        Assert.Equal(alive, Draw(emitter, age).Length);
        Assert.Equal(12f, emitter.Duration, 5);
    }

    [Fact]
    public void Setting_the_timing_or_shape_again_replaces_it()
    {
        // Over, then Burst: all ten are born at once. A line, then a point: all at the middle.
        var emitter = new ParticleEmitter(count: 10, lifetime: 10f).Over(5f).Burst().FromLine(4f).FromPoint();

        var particles = Draw(emitter, 0f);

        Assert.Equal(10, particles.Length);
        Assert.All(particles, p => Near(Vector3.Zero, p.Position));
    }

    [Fact]
    public void Adding_a_node_leaves_the_emitter_it_came_from_alone()
    {
        var faded = Single.Opacity(0.5f);

        Assert.Equal(1f, Assert.Single(Draw(Single, 1f)).Color.W);
        Assert.Equal(0.5f, Assert.Single(Draw(faded, 1f)).Color.W);
    }

    [Fact]
    public void The_same_key_and_age_always_give_the_same_particles()
    {
        var emitter = new ParticleEmitter(count: 24, lifetime: 1.5f).FromDisc(1f).Scatter(1f).Outward(1f);

        var first = Draw(emitter, 0.4f);
        var second = Draw(emitter, 0.4f);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_keys_and_seeds_give_different_patterns()
    {
        var emitter = new ParticleEmitter(count: 24, lifetime: 1.5f).FromDisc(1f).Scatter(1f).Outward(1f);

        var one = Draw(emitter, 0.4f, key: 1);
        var two = Draw(emitter, 0.4f, key: 2);
        var reseeded = Draw(emitter.Seed(5), 0.4f, key: 1);

        Assert.NotEqual(one[0].Position, two[0].Position);
        Assert.NotEqual(one[0].Position, reseeded[0].Position);
    }

    [Fact]
    public void The_same_emitter_twice_in_an_effect_makes_two_patterns()
    {
        var emitter = new ParticleEmitter(count: 1, lifetime: 1.5f).Scatter(1f);
        var effect = new ParticleEffect().Add(emitter).Add(emitter);

        var particles = Draw(effect, 0.4f);

        Assert.Equal(2, particles.Length);
        Assert.NotEqual(particles[0].Position, particles[1].Position);
    }

    [Fact]
    public void An_effect_starts_each_emitter_after_its_delay()
    {
        // Thrown at 1 m/s a second late, so at 1.5 s it has flown half a metre.
        var effect = new ParticleEffect().Add(Single.Velocity(Vector3.UnitX), delay: 1f);

        Assert.Empty(Draw(effect, 0.5f));
        Near(new Vector3(0.5f, 0f, 0f), Assert.Single(Draw(effect, 1.5f)).Position);
        Assert.Equal(11f, effect.Duration, 5);
    }

    [Fact]
    public void An_offset_on_a_line_stretches_along_it_so_minus_a_half_is_the_start()
    {
        // From (1, 0, 0) to (5, 0, 0): -0.5 along is the start, and up stays a metre up.
        var effect = new ParticleEffect().Add(Single, offset: new Vector3(-0.5f, 1f, 0f));

        var particle = Assert.Single(DrawLine(effect, new Vector3(1f, 0f, 0f), new Vector3(5f, 0f, 0f), 0f));

        Near(new Vector3(1f, 1f, 0f), particle.Position);
    }

    [Fact]
    public void A_line_from_and_to_covers_the_span_and_keeps_speeds_their_size()
    {
        // From the origin to (3, 0, 4) is 5 m. A start speed of 1 m/s along the line moves
        // each particle 1 m along it in a second, not 5 m.
        var emitter = new ParticleEmitter(count: 40, lifetime: 10f).FromLine().Velocity(Vector3.UnitX);
        var to = new Vector3(3f, 0f, 4f);
        var direction = to / 5f;

        var born = DrawLine(emitter, Vector3.Zero, to, 0f);
        var later = DrawLine(emitter, Vector3.Zero, to, 1f);

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
    public void An_emitter_drawn_on_its_own_becomes_one_effect_every_time()
    {
        ParticleEffect first = Single;
        ParticleEffect second = Single;

        Assert.Same(first, second);
    }

    [Fact]
    public void ToString_lists_the_nodes_in_order()
    {
        var emitter = new ParticleEmitter(count: 3, lifetime: 2f).Over(1f).FromLine().Drag(2f);

        Assert.Equal(
            "ParticleEmitter 3 x 2 s: Over { Duration = 1 } -> Line { Length = 1 } -> Drag { PerSecond = 2 }",
            emitter.ToString());
    }

    [Fact]
    public void Begin_forgets_the_last_frames_particles()
    {
        var view = new View();
        view.Begin(0f);
        view.Particles(1, Single, Vector3.Zero, 0.1f);
        Assert.NotEqual(0, view.DrawnParticles.Length);

        view.Begin(0f);

        Assert.Equal(0, view.DrawnParticles.Length);
    }

    [Fact]
    public void Too_few_particles_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleEmitter(count: 0, lifetime: 1f));

        Assert.Equal("count", error.ParamName);
        Assert.Contains("An emitter needs 1 to 32768 particles.", error.Message);
    }

    [Fact]
    public void A_lifetime_of_zero_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.WithLifetime(0f));

        Assert.Contains("A particle's lifetime must be more than zero seconds.", error.Message);
    }

    [Fact]
    public void Opacity_above_one_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.Opacity(1.5f));

        Assert.Contains("opacity must be from 0 to 1.", error.Message);
    }

    [Fact]
    public void A_negative_size_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.Size(1f, -1f));

        Assert.Contains("end can't be negative.", error.Message);
    }

    [Fact]
    public void A_negative_color_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.Color(new Vector3(0.5f, -0.1f, 0.5f)));

        Assert.Contains("A color can't have a negative channel.", error.Message);
    }

    [Fact]
    public void A_negative_spawn_box_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.FromBox(new Vector3(1f, -1f, 1f)));

        Assert.Contains("A spawn box can't be smaller than nothing.", error.Message);
    }

    [Fact]
    public void A_gradient_going_backwards_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.Gradient((0.5f, Vector3.One), (0.2f, Vector3.One)));

        Assert.Contains("A gradient's points must go up from 0 to 1.", error.Message);
    }

    [Fact]
    public void A_cone_with_no_direction_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Single.Cone(Vector3.Zero, 0.3f, 1f));

        Assert.Contains("A cone needs a direction to point in.", error.Message);
    }

    [Fact]
    public void A_negative_delay_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleEffect().Add(Single, delay: -1f));

        Assert.Contains("An emitter's delay can't be negative.", error.Message);
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
