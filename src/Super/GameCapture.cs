namespace Super;

/// <summary>
/// Saves what the game draws, from anywhere in a game's code. The pictures come straight
/// from the renderer, so other windows and overlays can never end up in them.
/// With no window there's nothing to capture, so requests do nothing.
/// </summary>
public sealed class GameCapture : IDisposable
{
    private const string VideoExtension = ".mp4";

    private readonly List<string> _screenshots = [];
    private string? _videoPath;
    private VideoFile? _video;

    /// <summary>
    /// While this is true each frame moves the game on by exactly one video frame. A
    /// slow capture slows the game down rather than making the video stutter.
    /// </summary>
    public bool IsRecording => _videoPath is not null;

    internal int FramesPerSecond { get; private set; }

    internal bool WantsFrame => IsRecording || _screenshots.Count > 0;

    /// <summary>Saves the next frame as a PNG.</summary>
    public void Screenshot(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _screenshots.Add(path);
    }

    /// <summary>Saves every frame to an mp4 until <see cref="StopRecording"/>. This needs ffmpeg.</summary>
    public void StartRecording(string path, int framesPerSecond = 60)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        if (!path.EndsWith(VideoExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Videos are saved as {VideoExtension}, so '{path}' won't work.", nameof(path));
        }

        if (IsRecording)
        {
            throw new InvalidOperationException($"Already recording {_videoPath}.");
        }

        _videoPath = path;
        FramesPerSecond = framesPerSecond;
    }

    /// <summary>Finishes the video, and waits for the file to be complete.</summary>
    public void StopRecording()
    {
        var video = _video;
        _video = null;
        _videoPath = null;
        video?.Finish();
    }

    public void Dispose() => StopRecording();

    /// <summary>Takes a finished frame of RGBA pixels.</summary>
    internal void Accept(byte[] pixels, int width, int height)
    {
        foreach (string path in _screenshots)
        {
            CreateFolderFor(path);
            PngFile.Write(path, pixels, width, height);
        }

        _screenshots.Clear();

        if (_videoPath is null)
        {
            return;
        }

        if (_video is null)
        {
            CreateFolderFor(_videoPath);
            _video = new VideoFile(_videoPath, width, height, FramesPerSecond);
        }
        else if (_video.Width != width || _video.Height != height)
        {
            Console.Error.WriteLine($"The window changed size, so {_videoPath} stops here.");
            StopRecording();
            return;
        }

        _video.Write(pixels);
    }

    private static void CreateFolderFor(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }
    }
}
