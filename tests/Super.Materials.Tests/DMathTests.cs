namespace Super.Materials.Tests;

public class DMathTests
{
    [Fact]
    public void Known_values()
    {
        Assert.Equal(1f, DMath.Sin(MathF.PI / 2), 1e-6f);
        Assert.Equal(1f, DMath.Cos(0f), 1e-6f);
        Assert.Equal(1024f, DMath.Pow(2f, 10f));
        Assert.Equal(1f, DMath.Exp(0f));
        Assert.Equal(0f, DMath.Log(1f));
        Assert.Equal(MathF.PI / 4, DMath.Atan2(1f, 1f), 1e-6f);
    }

    static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance * Math.Max(1.0, Math.Abs(expected)), $"{what}: expected {expected}, got {actual}");
    }

    [Fact]
    public void Sin_and_cos_match_math_over_a_wide_range()
    {
        for (var i = 0; i < 10000; i++)
        {
            var x = -100f + 200f * i / 9999f;
            Close(Math.Sin(x), DMath.Sin(x), 2e-6, $"sin({x})");
            Close(Math.Cos(x), DMath.Cos(x), 2e-6, $"cos({x})");
        }
    }

    [Fact]
    public void Exp_matches_math()
    {
        for (var i = 0; i < 10000; i++)
        {
            var x = -10f + 20f * i / 9999f;
            Close(Math.Exp(x), DMath.Exp(x), 2e-6, $"exp({x})");
        }
    }

    [Fact]
    public void Log_matches_math()
    {
        for (var i = 1; i <= 10000; i++)
        {
            var x = i * 0.1f;
            Close(Math.Log(x), DMath.Log(x), 2e-6, $"log({x})");
        }
    }

    [Fact]
    public void Atan2_matches_math_in_every_quadrant()
    {
        for (var i = 0; i < 10000; i++)
        {
            var angle = -Math.PI + 2 * Math.PI * i / 10000;
            var y = (float)Math.Sin(angle) * 3f;
            var x = (float)Math.Cos(angle) * 3f;
            Close(Math.Atan2(y, x), DMath.Atan2(y, x), 2e-6, $"atan2({y}, {x})");
        }
    }
}
