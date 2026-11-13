using Rendition;

namespace Super.Tests;

public class BakeIdTests
{
    [Fact]
    public void Bake_id_is_never_zero()
    {
        Assert.NotEqual(0ul, BakeIds.Of(0, 0));
    }

    [Fact]
    public void Part_and_instance_come_back_out()
    {
        ulong id = BakeIds.Of(0xABCDEF1234567000, 17);

        Assert.Equal(17, BakeIds.Part(id));
        Assert.Equal(BakeIds.Of(0xABCDEF1234567000, 0), BakeIds.Instance(id));
    }

    [Fact]
    public void Part_past_the_limit_is_explained()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => BakeIds.Of(0x1234, 4096));

        Assert.Contains("4095", error.Message);
    }
}
