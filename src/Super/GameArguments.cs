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
    string? ScriptName = null,
    string? UserDataFolder = null,
    float? UiScale = null,
    bool NoMaterials = false,
    float? RenderScale = null,
    Rendition.Upscaler? Upscaler = null,
    bool NoTrace = false,
    Rendition.DebugView DebugView = Rendition.DebugView.None,
    bool Bake = false,
    bool BakeCheck = false,
    Rendition.BakeQuality BakeQuality = Rendition.BakeQuality.Final,
    string? GraphicsProfile = null)
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
        bool noMaterials = false;
        bool noTrace = false;
        string? scriptName = null;
        string? userDataFolder = null;
        float? uiScale = null;
        float? renderScale = null;
        Rendition.Upscaler? upscaler = null;
        var debugView = Rendition.DebugView.None;
        bool bake = false;
        bool bakeCheck = false;
        Rendition.BakeQuality? bakeQuality = null;
        string? graphicsProfile = null;

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
                case "--user-data":
                    userDataFolder = Value(args, ++i, "--user-data");
                    break;
                case "--ui-scale":
                    uiScale = PositiveScale(args, ++i);
                    break;
                case "--render-scale":
                    renderScale = SceneScale(args, ++i);
                    break;
                case "--graphics":
                    graphicsProfile = GraphicsProfileName(args, ++i);
                    break;
                case "--upscaler":
                    upscaler = UpscalerName(args, ++i);
                    break;
                case "--debug-view":
                    debugView = DebugViewName(args, ++i);
                    break;
                case "--no-vsync":
                    noVsync = true;
                    break;
                case "--no-materials":
                    noMaterials = true;
                    break;
                case "--no-trace":
                    noTrace = true;
                    break;
                case "--bake":
                    bake = true;
                    break;
                case "--bake-check":
                    bakeCheck = true;
                    break;
                case "--bake-quality":
                    bakeQuality = BakeQualityName(args, ++i);
                    break;
            }
        }

        if (ticks < 0)
        {
            throw new ArgumentException("--ticks can't be negative.");
        }

        // A bake is a run of its own: it neither saves a picture nor plays in a window.
        foreach (var (asked, name) in new[] { (bake, "--bake"), (bakeCheck, "--bake-check") })
        {
            if (!asked)
            {
                continue;
            }

            string? other = screenshotPath is not null ? "--screenshot"
                : recordPath is not null ? "--record"
                : profilePath is not null ? "--profile"
                : name == "--bake" && bakeCheck ? "--bake-check"
                : noVsync ? "--no-vsync"
                : null;
            if (other is not null)
            {
                throw new ArgumentException($"{name} can't run with {other}.");
            }
        }

        if (bakeQuality is not null && !bake)
        {
            throw new ArgumentException("--bake-quality needs --bake.");
        }

        if (ticks is not null && screenshotPath is null && recordPath is null && profilePath is null && !bake && !bakeCheck)
        {
            throw new ArgumentException("--ticks needs --screenshot, --record, --profile, --bake or --bake-check, because those are the only runs without a window so far.");
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
            seed, ticks, screenshotPath, recordPath, seconds, framesPerSecond, width, height, profilePath, frames ?? DefaultProfileFrames, noVsync, scriptName, userDataFolder, uiScale, noMaterials,
            renderScale, upscaler, noTrace, debugView, bake, bakeCheck, bakeQuality ?? Rendition.BakeQuality.Final,
            graphicsProfile);
    }

    private static Rendition.BakeQuality BakeQualityName(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--bake-quality");
        return text switch
        {
            "preview" => Rendition.BakeQuality.Preview,
            "final" => Rendition.BakeQuality.Final,
            _ => throw new ArgumentException($"--bake-quality needs preview or final, not '{text}'."),
        };
    }

    /// <summary>
    /// The start-up error for a debug view the GPU can't show, or null when it can. The trace view needs a GPU that
    /// can trace rays, and the error carries why this one can't, such as "turned off with --no-trace".
    /// </summary>
    internal static string? DebugViewError(Rendition.DebugView view, bool canTrace, string? noTraceReason)
    {
        if (view == Rendition.DebugView.Trace && !canTrace)
        {
            return $"--debug-view trace needs a GPU that can trace rays, and this one can't ({noTraceReason}).";
        }

        return null;
    }

    private static Rendition.DebugView DebugViewName(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--debug-view");
        return text switch
        {
            "trace" => Rendition.DebugView.Trace,
            "probes" => Rendition.DebugView.Probes,
            _ => throw new ArgumentException($"--debug-view doesn't know '{text}'. It knows: trace, probes."),
        };
    }

    private static float PositiveScale(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--ui-scale");
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) && scale > 0f && float.IsFinite(scale)
            ? scale
            : throw new ArgumentException($"--ui-scale needs a number above zero, such as 2 or 1.5, not '{text}'.");
    }

    private static float SceneScale(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--render-scale");
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale)
            && scale is >= Rendition.Renderer.MinRenderScale and <= Rendition.Renderer.MaxRenderScale
                ? scale
                : throw new ArgumentException($"--render-scale needs a number from 0.5 to 2, such as 1.5, not '{text}'.");
    }

    /// <summary>The names <c>--graphics</c> takes besides a profile's path.</summary>
    internal const string AutoProfile = "auto";

    private static string GraphicsProfileName(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--graphics");
        return text is "low" or "medium" or "high" or "ultra" or "fastest" or AutoProfile
            || text.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? text
            : throw new ArgumentException($"--graphics needs low, medium, high, ultra, fastest, auto or a profile ending in .json, not '{text}'.");
    }

    /// <summary>
    /// The settings <c>--graphics</c> names: a <see cref="GraphicsQuality"/> preset, <see cref="Graphics.Fastest"/>, the
    /// quality <see cref="Graphics.Recommend"/> picks for <paramref name="gpu"/> when the name is <c>auto</c>, or a
    /// profile read from a .json path. Null for <c>auto</c> while the GPU isn't known yet.
    /// </summary>
    internal static Graphics? GraphicsFor(string profile, Rendition.GpuInfo? gpu)
    {
        if (profile == AutoProfile)
        {
            return gpu is { } known ? Graphics.For(Graphics.Recommend(known)) : null;
        }

        if (profile == "fastest")
        {
            return Graphics.Fastest();
        }

        if (profile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(profile)
                ? Graphics.Load(profile)
                : throw new ArgumentException($"--graphics can't find the profile '{profile}'.");
        }

        return Graphics.For(Enum.Parse<GraphicsQuality>(profile, ignoreCase: true));
    }

    private static Rendition.Upscaler UpscalerName(ReadOnlySpan<string> args, int index)
    {
        string text = Value(args, index, "--upscaler");
        return text switch
        {
            "taa" => Rendition.Upscaler.Taa,
            "fsr" => Rendition.Upscaler.Fsr,
            _ => throw new ArgumentException($"--upscaler needs taa or fsr, not '{text}'."),
        };
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
