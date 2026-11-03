namespace Super;

/// <summary>
/// Saves what the game draws, from anywhere in a game's code. The pictures come straight
/// from the renderer, so other windows and overlays can never end up in them.
/// With no window there are no pictures to capture, so screenshots and videos aren't saved.
/// A headless recording can still write the game's sound to a .wav, and that file is
/// deleted again when the recording stops.
/// </summary>
public sealed class GameCapture : IDisposable
{
    private const string VideoExtension = ".mp4";

    private readonly List<string> _screenshots = [];
    private string? _videoPath;
    private readonly Audio? _audio;
    private VideoFile? _video;
    private string? _soundPath;

    public GameCapture()
    {
    }

    /// <summary>With the game's audio, a recording carries its sound.</summary>
    internal GameCapture(Audio? audio)
    {
        _audio = audio;
    }

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

    /// <summary>
    /// Finishes the video, and waits for the file to be complete. It includes the game's sound; if ffmpeg can't add
    /// it, the video is kept without sound and the sound is left beside it as a .wav.
    /// </summary>
    public void StopRecording()
    {
        var video = _video;
        string? soundPath = _soundPath;
        _video = null;
        _videoPath = null;
        _soundPath = null;
        try
        {
            _audio?.StopRecording();
        }
        finally
        {
            video?.Finish(soundPath);
        }
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

            if (_audio is not null)
            {
                // The sound starts with the first picture, so the two line up. It waits beside the video until
                // the video is done, then goes into it.
                string soundPath = Path.ChangeExtension(_videoPath, ".wav");
                _audio.StartRecording(soundPath);
                _soundPath = soundPath;
            }
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
