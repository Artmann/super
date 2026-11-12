namespace Super.Tests;

public class ScreenshotSettleTests
{
    private const int Cap = 2048;

    [Fact]
    public void Screenshot_waits_for_settled_probes_up_to_the_cap()
    {
        // Settles once 37 frames have been rendered.
        int rendered = 0;
        int waited = GameWindow.ExtraSettleFrames(() => rendered >= 37, () => rendered++, Cap);
        Assert.Equal(37, waited);
        Assert.Equal(37, rendered);

        int neverRendered = 0;
        int capped = GameWindow.ExtraSettleFrames(() => false, () => neverRendered++, Cap);
        Assert.Equal(2048, capped);
        Assert.Equal(2048, neverRendered);

        int alreadyRendered = 0;
        int none = GameWindow.ExtraSettleFrames(() => true, () => alreadyRendered++, Cap);
        Assert.Equal(0, none);
        Assert.Equal(0, alreadyRendered);
    }
}
