namespace Super.Materials.Tests;

public class BlendTests
{
    static float Pixel(Func<Gray> build)
    {
        return GraphRunner.Evaluate(build, 16)[0];
    }

    [Theory]
    [InlineData(BlendMode.Copy, 0.75f)]
    [InlineData(BlendMode.Add, 1.0f)]
    [InlineData(BlendMode.Subtract, -0.5f)]
    [InlineData(BlendMode.Multiply, 0.1875f)]
    [InlineData(BlendMode.Min, 0.25f)]
    [InlineData(BlendMode.Max, 0.75f)]
    [InlineData(BlendMode.Overlay, 0.375f)]
    [InlineData(BlendMode.Screen, 0.8125f)]
    public void Each_mode_gives_the_hand_worked_result(BlendMode mode, float expected)
    {
        Assert.Equal(expected, Pixel(() => Gray.Of(0.25f).Blend(Gray.Of(0.75f), mode)), 5);
    }

    [Fact]
    public void Overlay_of_a_bright_base_uses_the_screen_side()
    {
        // base 0.75 is above a half: 1 - 2 * (1 - 0.75) * (1 - 0.5) = 0.75
        Assert.Equal(0.75f, Pixel(() => Gray.Of(0.75f).Blend(Gray.Of(0.5f), BlendMode.Overlay)), 5);
    }

    [Fact]
    public void A_mask_of_zero_keeps_the_base()
    {
        Assert.Equal(0.25f, Pixel(() => Gray.Of(0.25f).Blend(Gray.Of(0.75f), BlendMode.Copy, Gray.Of(0f))), 5);
    }

    [Fact]
    public void A_mask_of_one_gives_the_full_result()
    {
        Assert.Equal(0.75f, Pixel(() => Gray.Of(0.25f).Blend(Gray.Of(0.75f), BlendMode.Copy, Gray.Of(1f))), 5);
    }

    [Fact]
    public void Half_opacity_lands_halfway()
    {
        Assert.Equal(0.5f, Pixel(() => Gray.Of(0.25f).Blend(Gray.Of(0.75f), BlendMode.Copy, opacity: 0.5f)), 5);
    }

    [Fact]
    public void Mask_and_opacity_multiply()
    {
        Assert.Equal(0.375f, Pixel(() => Gray.Of(0.25f).Blend(Gray.Of(0.75f), BlendMode.Copy, Gray.Of(0.5f), 0.5f)), 5);
    }

    [Fact]
    public void Add_does_not_clamp()
    {
        Assert.Equal(1.5f, Pixel(() => Gray.Of(0.75f).Add(Gray.Of(0.75f))), 5);
    }

    [Fact]
    public void Shorthands_with_a_number_match_blending_a_constant()
    {
        Assert.Equal(1.0f, Pixel(() => Gray.Of(0.25f).Add(0.75f)), 5);
        Assert.Equal(-0.5f, Pixel(() => Gray.Of(0.25f).Subtract(0.75f)), 5);
        Assert.Equal(0.1875f, Pixel(() => Gray.Of(0.25f).Multiply(0.75f)), 5);
        Assert.Equal(0.25f, Pixel(() => Gray.Of(0.25f).Min(0.75f)), 5);
        Assert.Equal(0.75f, Pixel(() => Gray.Of(0.25f).Max(0.75f)), 5);
    }

    [Fact]
    public void Shorthands_with_an_image_use_the_matching_mode()
    {
        Assert.Equal(-0.5f, Pixel(() => Gray.Of(0.25f).Subtract(Gray.Of(0.75f))), 5);
        Assert.Equal(0.1875f, Pixel(() => Gray.Of(0.25f).Multiply(Gray.Of(0.75f))), 5);
        Assert.Equal(0.25f, Pixel(() => Gray.Of(0.25f).Min(Gray.Of(0.75f))), 5);
        Assert.Equal(0.75f, Pixel(() => Gray.Of(0.25f).Max(Gray.Of(0.75f))), 5);
    }

    [Fact]
    public void Rgb_blend_works_per_channel_and_the_mask_covers_all_three()
    {
        var pixels = GraphRunner.Evaluate(
            () => Rgb.Of(0.25f, 0.5f, 1f).Blend(Rgb.Of(0.75f, 0.5f, 0f), BlendMode.Add, Gray.Of(0.5f)), 2);

        Assert.Equal(0.625f, pixels[0], 5);
        Assert.Equal(0.75f, pixels[1], 5);
        Assert.Equal(1f, pixels[2], 5);
    }

    [Fact]
    public void Rgb_multiply_by_a_gray_scales_every_channel()
    {
        var pixels = GraphRunner.Evaluate(() => Rgb.Of(1f, 0.5f, 0.25f).Multiply(Gray.Of(0.5f)), 2);

        Assert.Equal([0.5f, 0.25f, 0.125f], pixels[..3]);
    }

    [Fact]
    public void Rgb_multiply_by_an_rgb_goes_channel_by_channel()
    {
        var pixels = GraphRunner.Evaluate(() => Rgb.Of(1f, 0.5f, 0.25f).Multiply(Rgb.Of(0.5f, 0.5f, 0f)), 2);

        Assert.Equal([0.5f, 0.25f, 0f], pixels[..3]);
    }

    [Fact]
    public void Height_blend_with_a_higher_top_takes_the_top()
    {
        var mask = Pixel(() => Gray.HeightBlend(Gray.Of(0.2f), Gray.Of(0.8f)).Mask);
        var height = Pixel(() => Gray.HeightBlend(Gray.Of(0.2f), Gray.Of(0.8f)).Height);

        Assert.Equal(1f, mask, 5);
        Assert.Equal(0.8f, height, 5);
    }

    [Fact]
    public void Height_blend_with_a_lower_top_keeps_the_bottom()
    {
        var mask = Pixel(() => Gray.HeightBlend(Gray.Of(0.8f), Gray.Of(0.2f)).Mask);
        var height = Pixel(() => Gray.HeightBlend(Gray.Of(0.8f), Gray.Of(0.2f)).Height);

        Assert.Equal(0f, mask, 5);
        Assert.Equal(0.8f, height, 5);
    }

    [Fact]
    public void Height_blend_of_equal_heights_is_half_masked()
    {
        Assert.Equal(0.5f, Pixel(() => Gray.HeightBlend(Gray.Of(0.5f), Gray.Of(0.5f)).Mask), 5);
    }

    [Fact]
    public void Height_blend_offset_raises_the_top()
    {
        // top 0.5 + (0.75 - 0.5) - bottom 0.5 = 0.25, which saturates the mask. Height is 0.5 + 0.75 - 0.5.
        var mask = Pixel(() => Gray.HeightBlend(Gray.Of(0.5f), Gray.Of(0.5f), offset: 0.75f).Mask);
        var height = Pixel(() => Gray.HeightBlend(Gray.Of(0.5f), Gray.Of(0.5f), offset: 0.75f).Height);

        Assert.Equal(1f, mask, 5);
        Assert.Equal(0.75f, height, 5);
    }
}
