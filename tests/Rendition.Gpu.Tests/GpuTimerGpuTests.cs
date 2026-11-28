using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class GpuTimerGpuTests(GpuFixture gpu)
{
    [Fact]
    public void A_frame_that_skipped_a_mark_reads_nothing_instead_of_an_older_frames_time()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var timer = new GpuTimer(device, 1, ["Pass"]);
        gpu.Context.SubmitAndWait(cmd =>
        {
            timer.Begin(cmd, 0);
            timer.Mark(cmd);
            timer.End(cmd);
        });
        Assert.True(timer.Collect(0));

        // The pass's closing mark is left out, so its second query still holds the first frame's time.
        gpu.Context.SubmitAndWait(cmd =>
        {
            timer.Begin(cmd, 0);
            timer.End(cmd);
        });

        Assert.False(timer.Collect(0));
        Assert.Equal(errors, gpu.ValidationErrors);
    }
}
