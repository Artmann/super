using System.Globalization;

namespace Super;

/// <summary>
/// The options every Super game understands. Anything else on the command line is
/// left for the game to read.
/// </summary>
internal sealed record GameArguments(
    int? Seed,
    long? Ticks,
    string? ScreenshotPath,
    string? RecordPath = null,
    double? Seconds = null,
    int FramesPerSecond = GameArguments.DefaultFramesPerSecond,
    int? Width = null,
    int? Height = null,
    string? ProfilePath = null,
    int Frames = GameArguments.DefaultProfileFrames,
    bool NoVsync = false,
    string? ScriptName = null)
{
    public const int DefaultFramesPerSecond = 60;

    public const int DefaultProfileFrames = 60;

    public static GameArguments Parse(ReadOnlySpan<string> args)
    {
        int? seed = null;
        long? ticks = null;
        string? screenshotPath = null;
        string? recordPath = null;
        double? seconds = null;
        int framesPerSecond = DefaultFramesPerSecond;
        int? width = null;
        int? height = null;
        string? profilePath = null;
        int? frames = null;
        bool noVsync = false;
        string? scriptName = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    seed = (int)Number(args, ++i, "--seed");
                    break;
                case "--ticks":
                    ticks = Number(args, ++i, "--ticks");
                    break;
                case "--screenshot":
                    screenshotPath = Value(args, ++i, "--screenshot");
                    break;
                case "--record":
                    recordPath = Value(args, ++i, "--record");
                    break;
                case "--seconds":
                    seconds = PositiveSeconds(args, ++i);
                    break;
                case "--fps":
                    framesPerSecond = (int)Number(args, ++i, "--fps");
                    break;
                case "--size":
                    (width, height) = Size(args, ++i);
                    break;
                case "--profile":
                    profilePath = Value(args, ++i, "--profile");
                    break;
                case "--frames":
                    frames = (int)Number(args, ++i, "--frames");
                    break;
                case "--script":
                    scriptName = Value(args, ++i, "--script");
                    break;
                case "--no-vsync":
                    noVsync = true;
                    break;
            }
        }

        if (ticks < 0)
        {
            throw new ArgumentException("--ticks can't be negative.");
        }

        if (ticks is not null && screenshotPath is null && recordPath is null && profilePath is null)
        {
            throw new ArgumentException("--ticks needs --screenshot, --record or --profile, because those are the only runs without a window so far.");
        }

        if ((screenshotPath is not null ? 1 : 0) + (recordPath is not null ? 1 : 0) + (profilePath is not null ? 1 : 0) > 1)
        {
            throw new ArgumentException("--screenshot, --record and --profile can't run together.");
        }

        // Presenting to a hidden window is slow, so the numbers would be misleading.
        if (noVsync && (screenshotPath is not null || recordPath is not null || profilePath is not null))
        {
            throw new ArgumentException("--no-vsync only works when playing in a window, so it can't run with --screenshot, --record or --profile.");
        }

        if (frames is not null && profilePath is null)
        {
            throw new ArgumentException("--frames needs --profile.");
        }

        if (frames <= 0)
        {
            throw new ArgumentException("--frames must be above zero.");
        }

        if (recordPath is not null && seconds is null)
        {
            throw new ArgumentException("--record needs --seconds.");
        }

        if (recordPath is not null && !recordPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--record needs a path ending in .mp4.");
        }

        if (seconds is not null && recordPath is null)
        {
            throw new ArgumentException("--seconds needs --record.");
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentException("--fps must be above zero.");
        }

        return new GameArguments(
            seed, ticks, screenshotPath, recordPath, seconds, framesPerSecond, width, height, profilePath, frames ?? DefaultProfileFrames, noVsync, scriptName);
    }

    private static double PositiveSeconds(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--seconds");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 && double.IsFinite(seconds)
            ? seconds
            : throw new ArgumentException($"--seconds needs a number above zero, not '{text}'.");
    }

    private static (int Width, int Height) Size(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--size");
        string[] parts = text.Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int width) && width > 0
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int height) && height > 0
                ? (width, height)
                : throw new ArgumentException($"--size needs a size like 1920x1080, not '{text}'.");
    }

    private static long Number(ReadOnlySpan<string> args, int index, string name)
    {
        string text = Value(args, index, name);
        if (name != "--ticks")
        {
            return int.TryParse(text, CultureInfo.InvariantCulture, out int small)
                ? small
                : throw new ArgumentException($"{name} needs a whole number, not '{text}'.");
        }

        return long.TryParse(text, CultureInfo.InvariantCulture, out long number)
            ? number
            : throw new ArgumentException($"{name} needs a whole number, not '{text}'.");
    }

    private static string Value(ReadOnlySpan<string> args, int index, string name) =>
        index < args.Length ? args[index] : throw new ArgumentException($"{name} needs a value.");
}
