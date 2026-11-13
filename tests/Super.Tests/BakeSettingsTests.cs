using Rendition;

namespace Super.Tests;

public class BakeSettingsTests
{
    [Fact]
    public void Default_settings_bake_twenty_texels_a_metre()
    {
        Assert.Equal(20, default(BakeSettings).TexelsPerMetre);
        Assert.Equal(new BakeSettings(20), default(BakeSettings));
        Assert.Equal(20, new BakeSettings().TexelsPerMetre);
        Assert.Equal(BakeQuality.Final, new BakeSettings().Quality);
    }

    [Fact]
    public void Default_settings_filter_the_lightmaps()
    {
        Assert.True(default(BakeSettings).Filter);
        Assert.True(new BakeSettings().Filter);
        Assert.False(new BakeSettings(filter: false).Filter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Texels_a_metre_outside_one_to_two_hundred_throws(int texels)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new BakeSettings(texels));

        Assert.Contains("texelsPerMetre", exception.Message);
        Assert.Contains("1", exception.Message);
        Assert.Contains("200", exception.Message);
        Assert.Contains(texels.ToString(), exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Texels_a_metre_at_the_limits_are_kept(int texels)
    {
        Assert.Equal(texels, new BakeSettings(texels).TexelsPerMetre);
    }
}
