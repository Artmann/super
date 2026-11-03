using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Super;

/// <summary>Streams finished frames to ffmpeg, which turns them into an mp4.</summary>
internal sealed class VideoFile
{
    private readonly string _path;
    private readonly Process _ffmpeg;
    private readonly Stream _frames;
    private readonly List<string> _errors = [];

    public VideoFile(string path, int width, int height, int framesPerSecond)
    {
        Width = width;
        Height = height;
        _path = path;

        try
        {
            _ffmpeg = Process.Start(CreateStartInfo(path, width, height, framesPerSecond))
                ?? throw new InvalidOperationException("Could not start ffmpeg.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Recording needs ffmpeg on the PATH. Install it with: winget install Gyan.FFmpeg", exception);
        }

        _ffmpeg.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text)
            {
                lock (_errors)
                {
                    _errors.Add(text);
                }
            }
        };
        _ffmpeg.BeginErrorReadLine();
        _frames = _ffmpeg.StandardInput.BaseStream;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Adds a frame of RGBA pixels.</summary>
    public void Write(ReadOnlySpan<byte> pixels)
    {
        try
        {
            _frames.Write(pixels);
        }
        catch (IOException)
        {
            // ffmpeg stopped reading, and Finish reports why.
        }
    }

    /// <summary>
    /// Waits for ffmpeg to finish the file. With a WAV of the game's sound, adds it to the video afterwards: see
    /// <see cref="AddSound"/>.
    /// </summary>
    public void Finish(string? soundPath = null)
    {
        FinishVideo();
        if (soundPath is not null)
        {
            AddSound(_path, soundPath);
        }
    }

    /// <summary>
    /// Muxes the sound into the video without re-encoding the picture, then deletes the WAV. If that fails the video
    /// stays as it was, the WAV is left where it is, and one line says so.
    /// </summary>
    /// <param name="run">For tests: runs ffmpeg with these arguments and returns its error, or null on success.</param>
    internal static void AddSound(string videoPath, string soundPath, Func<IReadOnlyList<string>, string?>? run = null)
    {
        string temp = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(videoPath))!,
            Path.GetFileNameWithoutExtension(videoPath) + ".with-sound" + Path.GetExtension(videoPath));
        string? error;
        try
        {
            error = (run ?? RunFfmpeg)(
                ["-y", "-hide_banner", "-loglevel", "error", "-i", videoPath, "-i", soundPath, "-c:v", "copy", "-c:a", "aac", "-shortest", temp]);
            if (error is null)
            {
                File.Move(temp, videoPath, overwrite: true);
                File.Delete(soundPath);
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = exception.Message;
        }

        TryDelete(temp);
        Console.Error.WriteLine($"Couldn't add sound to {videoPath}: {error}. The sound is in {soundPath}.");
    }

    private static string? RunFfmpeg(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"ffmpeg failed with exit code {process.ExitCode}: {errors.Trim()}";
        }
        catch (Win32Exception)
        {
            return "ffmpeg isn't on the PATH";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file isn't worth failing over.
        }
    }

    private void FinishVideo()
    {
        try
        {
            _frames.Dispose();
        }
        catch (IOException)
        {
            // ffmpeg has already gone, and its exit code below reports why.
        }

        _ffmpeg.WaitForExit();
        int exitCode = _ffmpeg.ExitCode;
        _ffmpeg.Dispose();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed with exit code {exitCode}: {string.Join(' ', _errors)}");
        }
    }

    private static ProcessStartInfo CreateStartInfo(string path, int width, int height, int framesPerSecond)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };

        string[] arguments =
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}",
            "-framerate", framesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-i", "-",

            // Most players need even dimensions.
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            path,
        ];
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}
