using System.Numerics;
using Rendition.Passes;

namespace Super.Tests;

public class AtmosphereTests
{
    private static readonly Vector3 SixtyUp = new(MathF.Cos(MathF.PI / 3f), MathF.Sin(MathF.PI / 3f), 0f);

    [Fact]
    public void Aerosol_at_turbidity_3_follows_angstrom()
    {
        // beta = 0.04608 * 3 - 0.04586 = 0.09238; depth at 550 nm = 0.09238 * 0.55^-1.3 = 0.200959;
        // spread over a 1.2 km scale height that is 0.167465 per km at the ground, nine tenths of it scattering.
        var (scattering, extinction) = Atmosphere.Aerosol(3f);

        Assert.Equal(0.167465f, extinction, 1e-5f);
        Assert.Equal(0.150719f, scattering, 1e-5f);
    }

    [Fact]
    public void Light_going_straight_up_from_the_ground_loses_the_whole_columns_depth()
    {
        // Straight up the column's depth is each part's ground value times its height:
        // Rayleigh 8 km * (1 - e^-12.5), the aerosol 0.200959, ozone's triangle 15 km wide on average.
        //   red:   0.046416 + 0.200959 + 0.009750 = 0.257124, e^- = 0.773272
        //   green: 0.108464 + 0.200959 + 0.028215 = 0.337637, e^- = 0.713454
        //   blue:  0.264800 + 0.200959 + 0.001275 = 0.467033, e^- = 0.626860
        var through = Atmosphere.Transmittance(0f, 1f, 3f);

        Assert.Equal(0.773272f, through.X, 2e-3f);
        Assert.Equal(0.713454f, through.Y, 2e-3f);
        Assert.Equal(0.626860f, through.Z, 2e-3f);
    }

    [Fact]
    public void Light_aimed_into_the_ground_gets_nowhere()
    {
        Assert.Equal(Vector3.Zero, Atmosphere.Transmittance(1f, -0.5f, 3f));
    }

    [Fact]
    public void The_sun_sixty_degrees_up_keeps_its_colour()
    {
        var tint = Atmosphere.SunTint(SixtyUp, 3f, 0.005f);

        Assert.Equal(1f, tint.X, 1e-4f);
        Assert.Equal(1f, tint.Y, 1e-4f);
        Assert.Equal(1f, tint.Z, 1e-4f);
    }

    [Fact]
    public void A_low_sun_is_dimmer_and_warmer()
    {
        float elevation = 3f * MathF.PI / 180f;
        var low = new Vector3(MathF.Cos(elevation), MathF.Sin(elevation), 0f);

        var tint = Atmosphere.SunTint(low, 3f, 0.005f);

        Assert.True(tint.Y < 0.5f, $"The tint is {tint}.");
        Assert.True(tint.X > 2f * tint.Z, $"The tint is {tint}.");
    }

    [Fact]
    public void A_sun_below_the_horizon_gives_no_light()
    {
        float elevation = -2f * MathF.PI / 180f;
        var set = new Vector3(MathF.Cos(elevation), MathF.Sin(elevation), 0f);

        Assert.Equal(Vector3.Zero, Atmosphere.SunTint(set, 3f, 0.005f));
    }

    [Fact]
    public void A_sun_half_behind_the_horizon_gives_half_its_last_light()
    {
        // The horizon dips acos(6360 / 6360.05) = 0.003965 radians from just above the ground.
        float dip = 0.003965f;
        var half = new Vector3(MathF.Cos(-dip), MathF.Sin(-dip), 0f);

        var tint = Atmosphere.SunTint(half, 3f, 0.005f);

        Assert.True(tint.X > 0f, $"The tint is {tint}.");
        float justAbove = Atmosphere.SunTint(new Vector3(MathF.Cos(-dip + 0.02f), MathF.Sin(-dip + 0.02f), 0f), 3f, 0.005f).X;
        Assert.True(tint.X < justAbove, $"Half set {tint.X}, risen {justAbove}.");
    }

    // A point sun has no radius to fade over, so it goes out at the horizon at once. Exactly on the horizon it is
    // still a number, never NaN, which would blacken every pixel it lights.
    [Fact]
    public void A_point_sun_exactly_on_the_horizon_gives_a_number()
    {
        float dip = MathF.Acos(Atmosphere.PlanetRadius / (Atmosphere.PlanetRadius + Atmosphere.ViewHeight));
        var onHorizon = Vector3.Zero;
        for (float y = MathF.Sin(-dip) - 1e-6f; y < MathF.Sin(-dip) + 1e-6f; y = MathF.BitIncrement(y))
        {
            var direction = Vector3.Normalize(new Vector3(MathF.Sqrt(1f - y * y), y, 0f));
            if (MathF.Asin(direction.Y) == -dip)
            {
                onHorizon = direction;
                break;
            }
        }

        Assert.NotEqual(Vector3.Zero, onHorizon);
        var tint = Atmosphere.SunTint(onHorizon, 3f, 0f);
        Assert.True(float.IsFinite(tint.X) && float.IsFinite(tint.Y) && float.IsFinite(tint.Z), $"The tint is {tint}.");
    }

    [Fact]
    public void The_ground_is_lit_fully_at_sixty_degrees_and_barely_at_night()
    {
        Assert.Equal(1f, Atmosphere.GroundLight(SixtyUp, 3f), 1e-4f);
        Assert.True(Atmosphere.GroundLight(Vector3.UnitY, 3f) > 1f);
        Assert.Equal(0.002f, Atmosphere.GroundLight(-Vector3.UnitY, 3f), 1e-6f);
    }

    [Fact]
    public void Air_is_off_without_a_physical_sky_or_with_the_switch_off()
    {
        Assert.Equal((Vector4.Zero, Vector4.Zero), Atmosphere.Air(0f, SixtyUp, 1f, on: true));
        Assert.Equal((Vector4.Zero, Vector4.Zero), Atmosphere.Air(3f, SixtyUp, 1f, on: false));
    }

    [Fact]
    public void Air_dims_by_the_ground_air_per_metre()
    {
        // Green: Rayleigh 13.558e-3 plus the aerosol 0.167465 per km = 1.81023e-4 per metre.
        var (extinction, sun) = Atmosphere.Air(3f, SixtyUp, 2f, on: true);

        Assert.Equal(1.81023e-4f, extinction.Y, 1e-8f);
        Assert.Equal(1.50719e-4f, extinction.W, 1e-8f);
        Assert.Equal(1f, sun.W);
        // The sun at 60 degrees, before the air between it and the ground, is 20 times the intensity of 2.
        Assert.True(sun.Y > 0f && sun.Y < 40f, $"The sun is {sun}.");
    }
}
