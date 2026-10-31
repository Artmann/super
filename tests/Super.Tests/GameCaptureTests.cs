namespace Super.Tests;

public class GameCaptureTests
{
    [Fact]
    public void Wants_nothing_until_asked()
    {
        using var capture = new GameCapture();

        Assert.False(capture.WantsFrame);
        Assert.False(capture.IsRecording);
    }

    [Fact]
    public void A_screenshot_saves_the_next_frame_only()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(folder, "nested", "shot.png");
        using var capture = new GameCapture();

        capture.Screenshot(path);
        Assert.True(capture.WantsFrame);

        capture.Accept(new byte[4], 1, 1);

        Assert.True(File.Exists(path));
        Assert.False(capture.WantsFrame);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Recording_lasts_until_stopped()
    {
        using var capture = new GameCapture();

        capture.StartRecording("clip.mp4", 30);

        Assert.True(capture.IsRecording);
        Assert.True(capture.WantsFrame);
        Assert.Equal(30, capture.FramesPerSecond);

        capture.StopRecording();

        Assert.False(capture.IsRecording);
    }

    [Fact]
    public void Stopping_without_recording_is_harmless()
    {
        using var capture = new GameCapture();

        capture.StopRecording();

        Assert.False(capture.IsRecording);
    }

    [Fact]
    public void Only_one_recording_runs_at_a_time()
    {
        using var capture = new GameCapture();
        capture.StartRecording("clip.mp4");

        Assert.Throws<InvalidOperationException>(() => capture.StartRecording("other.mp4"));
    }

    [Theory]
    [InlineData("clip.avi", 60)]
    [InlineData("clip.mp4", 0)]
    [InlineData("", 60)]
    public void Explains_a_bad_recording(string path, int framesPerSecond)
    {
        using var capture = new GameCapture();

        Assert.ThrowsAny<ArgumentException>(() => capture.StartRecording(path, framesPerSecond));
    }
}
