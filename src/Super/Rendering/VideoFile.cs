using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Super;

/// <summary>Streams finished frames to ffmpeg, which turns them into an mp4.</summary>
internal sealed class VideoFile
{
    private readonly Process _ffmpeg;
    private readonly Stream _frames;
    private readonly List<string> _errors = [];

    public VideoFile(string path, int width, int height, int framesPerSecond)
    {
        Width = width;
        Height = height;

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

    /// <summary>Waits for ffmpeg to finish the file.</summary>
    public void Finish()
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
