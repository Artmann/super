namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class FrameCaptureGpuTests(GpuFixture gpu)
{
    [Fact]
    public void A_requested_capture_is_ready_once_a_later_frame_has_waited_for_it()
    {
        int errors = gpu.ValidationErrors;
        var view = gpu.View(DebugView.None);
        var capture = gpu.Renderer.RequestCapture();

        // Its own frame copies it, and it is only read once a frame has waited for that frame slot again.
        gpu.Renderer.Render(view, []);
        Assert.False(capture.IsReady);
        Assert.Throws<InvalidOperationException>(() => capture.Pixels);
        for (int i = 1; i < FrameRing.Count; i++)
        {
            gpu.Renderer.Render(view, []);
            Assert.False(capture.IsReady);
        }

        gpu.Renderer.Render(view, []);

        Assert.True(capture.IsReady);
        Assert.Equal((view.Width, view.Height), (capture.Width, capture.Height));
        Assert.Equal(view.Width * view.Height * 4, capture.Pixels.Length);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_requested_capture_holds_what_a_waited_capture_does()
    {
        // A blue sky with nothing else moving it, so the two pictures only differ in how they were read.
        var view = TestSky.Bare(gpu.View(DebugView.None).Width);
        for (int i = 0; i < 8; i++)
        {
            gpu.Renderer.Render(view, []);
        }

        var capture = gpu.Renderer.RequestCapture();
        RenderUntilReady(capture, view);

        byte[] waited = gpu.Renderer.Capture(view, [], out _, out _);

        var requested = Means(capture.Pixels);
        var expected = Means(waited);
        // The sky is blue, so a picture read back with its red and blue swapped shows here.
        Assert.True(requested.B > requested.R + 10f, $"The sky should be bluer than it is red: {requested}");
        Assert.InRange(requested.R, expected.R - 2f, expected.R + 2f);
        Assert.InRange(requested.G, expected.G - 2f, expected.G + 2f);
        Assert.InRange(requested.B, expected.B - 2f, expected.B + 2f);
        Assert.Equal(255f, requested.A);
    }

    [Fact]
    public void Requesting_twice_before_a_frame_gives_one_capture()
    {
        var first = gpu.Renderer.RequestCapture();
        var second = gpu.Renderer.RequestCapture();

        Assert.Same(first, second);

        var view = gpu.View(DebugView.None);
        RenderUntilReady(first, view);

        Assert.NotSame(first, gpu.Renderer.RequestCapture());
        // Takes that new request, so the next test on this renderer doesn't start with one.
        gpu.Renderer.Capture(view, [], out _, out _);
    }

    [Fact]
    public void A_waited_capture_that_throws_keeps_a_capture_the_game_asked_for()
    {
        var view = gpu.View(DebugView.None);
        var asked = gpu.Renderer.RequestCapture();
        // A static draw can't swim, so the frame throws before it is drawn.
        var swimming = new DrawItem { BakeId = BakeIds.Of(1, 0), Swim = new SwimMotion(0.1f, 1f, 0f) };

        Assert.Throws<ArgumentException>(() => gpu.Renderer.Capture(gpu.View(DebugView.None), [swimming], out _, out _));

        RenderUntilReady(asked, view);
        Assert.Equal((view.Width, view.Height), (asked.Width, asked.Height));
    }

    // A capture is ready a frame slot's turn after the frame that copied it; a few more frames than that and it never will be.
    private void RenderUntilReady(FrameCapture capture, in FrameView view)
    {
        for (int i = 0; i < FrameRing.Count + 2 && !capture.IsReady; i++)
        {
            gpu.Renderer.Render(view, []);
        }

        Assert.True(capture.IsReady, "The capture wasn't ready after its frame slot came round again.");
    }

    private static (float R, float G, float B, float A) Means(byte[] rgba)
    {
        double r = 0, g = 0, b = 0, a = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            r += rgba[i];
            g += rgba[i + 1];
            b += rgba[i + 2];
            a += rgba[i + 3];
        }

        double count = rgba.Length / 4.0;
        return ((float)(r / count), (float)(g / count), (float)(b / count), (float)(a / count));
    }
}
