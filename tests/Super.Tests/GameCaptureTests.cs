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
        try
        {
            using var capture = new GameCapture();

            capture.Screenshot(path);
            Assert.True(capture.WantsFrame);

            capture.Accept(new byte[4], 1, 1);

            Assert.True(File.Exists(path));
            Assert.False(capture.WantsFrame);

            // A second frame must not save the shot again.
            File.Delete(path);
            capture.Accept(new byte[4], 1, 1);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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

        var thrown = Assert.Throws<InvalidOperationException>(() => capture.StartRecording("other.mp4"));
        Assert.Equal("Already recording clip.mp4.", thrown.Message);
    }

    [Fact]
    public void Refuses_a_video_that_is_not_an_mp4()
    {
        using var capture = new GameCapture();

        var thrown = Assert.Throws<ArgumentException>(() => capture.StartRecording("clip.avi", 60));
        Assert.Equal("Videos are saved as .mp4, so 'clip.avi' won't work. (Parameter 'path')", thrown.Message);
    }

    [Fact]
    public void Refuses_zero_frames_a_second()
    {
        using var capture = new GameCapture();

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => capture.StartRecording("clip.mp4", 0));
        Assert.Equal("framesPerSecond", thrown.ParamName);
    }

    [Fact]
    public void Refuses_an_empty_path()
    {
        using var capture = new GameCapture();

        var thrown = Assert.Throws<ArgumentException>(() => capture.StartRecording("", 60));
        Assert.Equal("path", thrown.ParamName);
    }
}
