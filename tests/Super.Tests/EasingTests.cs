namespace Super.Tests;

public class EasingTests
{
    private const float Tolerance = 0.00001f;

    public static TheoryData<Easing> Every => [.. Enum.GetValues<Easing>()];

    public static TheoryData<Easing> InOuts => [.. Enum.GetValues<Easing>().Where(easing => easing.ToString().EndsWith("InOut", StringComparison.Ordinal))];

    // Each expected value is worked by hand from the curve's formula.
    [Theory]
    [InlineData(Easing.SineIn, 0.5f, 0.2928932f)] // 1 - cos(π/4)
    [InlineData(Easing.SineOut, 0.5f, 0.7071068f)] // sin(π/4)
    [InlineData(Easing.SineInOut, 0.25f, 0.1464466f)] // (1 - cos(π/4)) / 2
    [InlineData(Easing.QuadIn, 0.5f, 0.25f)]
    [InlineData(Easing.QuadOut, 0.5f, 0.75f)]
    [InlineData(Easing.QuadInOut, 0.25f, 0.125f)] // 2 × 0.25²
    [InlineData(Easing.QuadInOut, 0.75f, 0.875f)]
    [InlineData(Easing.QuartIn, 0.5f, 0.0625f)]
    [InlineData(Easing.QuartOut, 0.5f, 0.9375f)]
    [InlineData(Easing.QuintIn, 0.5f, 0.03125f)]
    [InlineData(Easing.QuintOut, 0.5f, 0.96875f)]
    [InlineData(Easing.QuintInOut, 0.25f, 0.015625f)] // 16 × 0.25⁵
    [InlineData(Easing.ExpoIn, 0.5f, 0.03125f)] // 2⁻⁵
    [InlineData(Easing.ExpoOut, 0.5f, 0.96875f)]
    [InlineData(Easing.ExpoInOut, 0.25f, 0.015625f)] // 2⁻⁵ / 2
    [InlineData(Easing.CircIn, 0.6f, 0.2f)] // 1 - √0.64
    [InlineData(Easing.CircOut, 0.4f, 0.8f)] // √(1 - 0.36)
    [InlineData(Easing.CircInOut, 0.25f, 0.0669873f)] // (1 - √0.75) / 2
    [InlineData(Easing.BackIn, 0.5f, -0.0876975f)] // 2.70158 × 0.125 - 1.70158 × 0.25
    [InlineData(Easing.BackOut, 0.5f, 1.0876975f)]
    [InlineData(Easing.BackInOut, 0.25f, -0.0996818f)] // 0.5² × (3.5949095 × 0.5 - 2.5949095) / 2
    [InlineData(Easing.ElasticIn, 0.5f, -0.015625f)]
    [InlineData(Easing.ElasticOut, 0.5f, 1.015625f)] // 2⁻⁵ × sin(4.25 × 2π/3) + 1, and that sine is 0.5
    [InlineData(Easing.ElasticInOut, 0.25f, 0.0119694f)] // -(2⁻⁵ × sin(-6.125 × 2π/4.5)) / 2, and that sine is -sin 50°
    [InlineData(Easing.BounceOut, 0.2f, 0.3025f)] // 7.5625 × 0.2²
    [InlineData(Easing.BounceOut, 0.5f, 0.765625f)] // 7.5625 × (0.5 - 1.5/2.75)² + 0.75
    [InlineData(Easing.BounceOut, 0.8f, 0.94f)] // 7.5625 × (0.8 - 2.25/2.75)² + 0.9375
    [InlineData(Easing.BounceOut, 0.95f, 0.98453125f)] // 7.5625 × (0.95 - 2.625/2.75)² + 0.984375
    [InlineData(Easing.BounceIn, 0.5f, 0.234375f)]
    [InlineData(Easing.BounceInOut, 0.25f, 0.1171875f)] // (1 - 0.765625) / 2
    public void Easing_matches_a_hand_worked_value(Easing easing, float amount, float expected)
    {
        Assert.Equal(expected, Ease.Apply(easing, amount), Tolerance);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void Every_easing_starts_at_zero_and_ends_at_one(Easing easing)
    {
        Assert.Equal(0f, Ease.Apply(easing, 0f), Tolerance);
        Assert.Equal(1f, Ease.Apply(easing, 1f), Tolerance);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void Every_easing_holds_its_ends_outside_zero_to_one(Easing easing)
    {
        Assert.Equal(0f, Ease.Apply(easing, -2f), Tolerance);
        Assert.Equal(1f, Ease.Apply(easing, 3f), Tolerance);
    }

    [Theory]
    [MemberData(nameof(InOuts))]
    public void Every_in_out_easing_is_halfway_at_half_time(Easing easing)
    {
        Assert.Equal(0.5f, Ease.Apply(easing, 0.5f), Tolerance);
    }

    [Theory]
    [InlineData(Easing.BackOut)]
    [InlineData(Easing.ElasticOut)]
    public void Back_and_elastic_go_past_the_end_on_the_way(Easing easing)
    {
        float highest = 0f;
        for (int i = 0; i <= 100; i++)
        {
            highest = MathF.Max(highest, Ease.Apply(easing, i / 100f));
        }

        Assert.True(highest > 1.05f, $"{easing} peaked at {highest}");
    }
}
