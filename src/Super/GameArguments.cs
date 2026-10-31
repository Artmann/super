using System.Globalization;

namespace Super;

/// <summary>
/// The options every Super game understands. Anything else on the command line is
/// left for the game to read.
/// </summary>
internal sealed record GameArguments(int? Seed, long? Ticks, string? ScreenshotPath)
{
    public static GameArguments Parse(ReadOnlySpan<string> args)
    {
        int? seed = null;
        long? ticks = null;
        string? screenshotPath = null;

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
            }
        }

        if (ticks < 0)
        {
            throw new ArgumentException("--ticks can't be negative.");
        }

        if (ticks is not null && screenshotPath is null)
        {
            throw new ArgumentException("--ticks needs --screenshot, because that's the only run without a window so far.");
        }

        return new GameArguments(seed, ticks, screenshotPath);
    }

    private static long Number(ReadOnlySpan<string> args, int index, string name)
    {
        string text = Value(args, index, name);
        if (name == "--seed")
        {
            return int.TryParse(text, CultureInfo.InvariantCulture, out int seed)
                ? seed
                : throw new ArgumentException($"{name} needs a whole number, not '{text}'.");
        }

        return long.TryParse(text, CultureInfo.InvariantCulture, out long number)
            ? number
            : throw new ArgumentException($"{name} needs a whole number, not '{text}'.");
    }

    private static string Value(ReadOnlySpan<string> args, int index, string name) =>
        index < args.Length ? args[index] : throw new ArgumentException($"{name} needs a value.");
}
