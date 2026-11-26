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
    bool Vsync = false,
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
    string? GraphicsProfile = null,
    bool Stats = false,
    bool NeutralLut = false,
    ArtLook? Look = null,
    IReadOnlyList<string>? DebugChannels = null)
{
    public const int DefaultFramesPerSecond = 60;

    public const int DefaultProfileFrames = 60;

    /// <summary>
    /// What each option does to the arguments read so far. An option that takes a value reads it from the
    /// <see cref="Reader"/>, which names the option in its errors.
    /// </summary>
    private static readonly Dictionary<string, Func<GameArguments, Reader, GameArguments>> Options = new()
    {
        ["--seed"] = (parsed, reader) => parsed with { Seed = reader.Int() },
        ["--ticks"] = (parsed, reader) => parsed with { Ticks = reader.Long() },
        ["--screenshot"] = (parsed, reader) => parsed with { ScreenshotPath = reader.Value() },
        ["--record"] = (parsed, reader) => parsed with { RecordPath = reader.Value() },
        ["--seconds"] = (parsed, reader) => parsed with { Seconds = PositiveSeconds(reader.Value()) },
        ["--fps"] = (parsed, reader) => parsed with { FramesPerSecond = reader.Int() },
        ["--size"] = WithSize,
        ["--profile"] = (parsed, reader) => parsed with { ProfilePath = reader.Value() },
        ["--frames"] = (parsed, reader) => parsed with { Frames = reader.Int() },
        ["--script"] = (parsed, reader) => parsed with { ScriptName = reader.Value() },
        ["--user-data"] = (parsed, reader) => parsed with { UserDataFolder = reader.Value() },
        ["--ui-scale"] = (parsed, reader) => parsed with { UiScale = PositiveScale(reader.Value()) },
        ["--render-scale"] = (parsed, reader) => parsed with { RenderScale = SceneScale(reader.Value()) },
        ["--look"] = (parsed, reader) => parsed with { Look = LookName(reader.Value()) },
        ["--graphics"] = (parsed, reader) => parsed with { GraphicsProfile = GraphicsProfileName(reader.Value()) },
        ["--upscaler"] = (parsed, reader) => parsed with { Upscaler = UpscalerName(reader.Value()) },
        ["--debug-view"] = (parsed, reader) => parsed with { DebugView = DebugViewName(reader.Value()) },
        ["--vsync"] = (parsed, _) => parsed with { Vsync = true },
        ["--stats"] = (parsed, _) => parsed with { Stats = true },
        ["--debug"] = (parsed, reader) => parsed with { DebugChannels = DebugDraw.ChannelNames(reader.Value()) },
        ["--no-materials"] = (parsed, _) => parsed with { NoMaterials = true },
        ["--neutral-lut"] = (parsed, _) => parsed with { NeutralLut = true },
        ["--no-trace"] = (parsed, _) => parsed with { NoTrace = true },
        ["--bake"] = (parsed, _) => parsed with { Bake = true },
        ["--bake-check"] = (parsed, _) => parsed with { BakeCheck = true },
        ["--bake-quality"] = (parsed, reader) => parsed with { BakeQuality = BakeQualityName(reader.Value()) },
    };

    public static GameArguments Parse(ReadOnlySpan<string> args)
    {
        var parsed = new GameArguments(null, null, null);
        var given = new HashSet<string>();
        var reader = new Reader(args.ToArray());
        while (reader.NextOption() is { } option)
        {
            if (Options.TryGetValue(option, out var apply))
            {
                parsed = apply(parsed, reader);
                given.Add(option);
            }
        }

        if (parsed.Ticks < 0)
        {
            throw new ArgumentException("--ticks can't be negative.");
        }

        parsed.CheckBake(given);
        parsed.CheckCapture();
        parsed.CheckProfile(given);
        parsed.CheckRecord();
        return parsed;
    }

    /// <summary>Whether the run saves a picture, a video or a profile instead of playing in a window.</summary>
    private bool Captures => ScreenshotPath is not null || RecordPath is not null || ProfilePath is not null;

    /// <summary>A bake is a run of its own: it neither saves a picture nor plays in a window.</summary>
    private void CheckBake(HashSet<string> given)
    {
        foreach (var (asked, name) in new[] { (Bake, "--bake"), (BakeCheck, "--bake-check") })
        {
            if (asked && BakeConflict(name) is { } other)
            {
                throw new ArgumentException($"{name} can't run with {other}.");
            }
        }

        if (given.Contains("--bake-quality") && !Bake)
        {
            throw new ArgumentException("--bake-quality needs --bake.");
        }
    }

    /// <summary>The first option given that can't run with <paramref name="name"/>, or null.</summary>
    private string? BakeConflict(string name)
    {
        (bool Given, string Option)[] conflicts =
        [
            (ScreenshotPath is not null, "--screenshot"),
            (RecordPath is not null, "--record"),
            (ProfilePath is not null, "--profile"),
            (name == "--bake" && BakeCheck, "--bake-check"),
            (Vsync, "--vsync"),
        ];
        return conflicts.FirstOrDefault(conflict => conflict.Given).Option;
    }

    private void CheckCapture()
    {
        if (Ticks is not null && !Captures && !Bake && !BakeCheck)
        {
            throw new ArgumentException("--ticks needs --screenshot, --record, --profile, --bake or --bake-check, because those are the only runs without a window so far.");
        }

        if (new[] { ScreenshotPath, RecordPath, ProfilePath }.Count(path => path is not null) > 1)
        {
            throw new ArgumentException("--screenshot, --record and --profile can't run together.");
        }

        CheckNeutralLut();

        // A run without a window decides for itself whether to wait for the display.
        if (Vsync && Captures)
        {
            throw new ArgumentException("--vsync only works when playing in a window, so it can't run with --screenshot, --record or --profile.");
        }

        // The panel's drawing and measuring would land in the profile's own figures.
        if (Stats && ProfilePath is not null)
        {
            throw new ArgumentException("--stats can't run with --profile, which measures the frames itself and writes what it found to its report.");
        }
    }

    private void CheckNeutralLut()
    {
        if (NeutralLut && ScreenshotPath is null)
        {
            throw new ArgumentException("--neutral-lut needs --screenshot, since it stamps the neutral colour table on the picture saved.");
        }

        if (NeutralLut && Width < NeutralLutStrip.Width)
        {
            throw new ArgumentException($"--neutral-lut needs a picture at least {NeutralLutStrip.Width} pixels wide to hold the colour table, but --size asks for {Width}.");
        }
    }

    private void CheckProfile(HashSet<string> given)
    {
        if (given.Contains("--frames") && ProfilePath is null)
        {
            throw new ArgumentException("--frames needs --profile.");
        }

        if (Frames <= 0)
        {
            throw new ArgumentException("--frames must be above zero.");
        }
    }

    private void CheckRecord()
    {
        if (RecordPath is not null && Seconds is null)
        {
            throw new ArgumentException("--record needs --seconds.");
        }

        if (RecordPath is not null && !RecordPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--record needs a path ending in .mp4.");
        }

        if (Seconds is not null && RecordPath is null)
        {
            throw new ArgumentException("--seconds needs --record.");
        }

        if (FramesPerSecond <= 0)
        {
            throw new ArgumentException("--fps must be above zero.");
        }
    }

    private static Rendition.BakeQuality BakeQualityName(string text) => text switch
    {
        "preview" => Rendition.BakeQuality.Preview,
        "final" => Rendition.BakeQuality.Final,
        _ => throw new ArgumentException($"--bake-quality needs preview or final, not '{text}'."),
    };

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

    private static Rendition.DebugView DebugViewName(string text) => text switch
    {
        "trace" => Rendition.DebugView.Trace,
        "probes" => Rendition.DebugView.Probes,
        _ => throw new ArgumentException($"--debug-view doesn't know '{text}'. It knows: trace, probes."),
    };

    private static float PositiveScale(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) && scale > 0f && float.IsFinite(scale)
            ? scale
            : throw new ArgumentException($"--ui-scale needs a number above zero, such as 2 or 1.5, not '{text}'.");

    private static float SceneScale(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale)
        && scale is >= Rendition.RenderSettings.MinRenderScale and <= Rendition.RenderSettings.MaxRenderScale
            ? scale
            : throw new ArgumentException($"--render-scale needs a number from 0.5 to 2, such as 1.5, not '{text}'.");

    /// <summary>The names <c>--graphics</c> takes besides a profile's path.</summary>
    internal const string AutoProfile = "auto";

    private static ArtLook LookName(string text) =>
        Looks.All.FirstOrDefault(look => Looks.ArgumentName(look.Name!) == text) is { Name: not null } found
            ? found
            : throw new ArgumentException(
                $"--look needs one of {string.Join(", ", Looks.All.Select(look => Looks.ArgumentName(look.Name!)))}, not '{text}'.");

    private static string GraphicsProfileName(string text) =>
        text is "low" or "medium" or "high" or "ultra" or "fastest" or AutoProfile
        || text.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? text
            : throw new ArgumentException($"--graphics needs low, medium, high, ultra, fastest, auto or a profile ending in .json, not '{text}'.");

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

    private static Rendition.Upscaler UpscalerName(string text) => text switch
    {
        "taa" => Rendition.Upscaler.Taa,
        "fsr" => Rendition.Upscaler.Fsr,
        _ => throw new ArgumentException($"--upscaler needs taa or fsr, not '{text}'."),
    };

    private static double PositiveSeconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 && double.IsFinite(seconds)
            ? seconds
            : throw new ArgumentException($"--seconds needs a number above zero, not '{text}'.");

    private static GameArguments WithSize(GameArguments parsed, Reader reader)
    {
        string text = reader.Value();
        string[] parts = text.Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int width) && width > 0
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int height) && height > 0
                ? parsed with { Width = width, Height = height }
                : throw new ArgumentException($"--size needs a size like 1920x1080, not '{text}'.");
    }

    /// <summary>Walks the command line one option at a time and reads the values that follow an option.</summary>
    private sealed class Reader(string[] args)
    {
        private int index = -1;

        private string option = "";

        /// <summary>The next word on the command line, or null at the end.</summary>
        public string? NextOption()
        {
            index++;
            option = index < args.Length ? args[index] : "";
            return index < args.Length ? option : null;
        }

        public string Value() =>
            ++index < args.Length ? args[index] : throw new ArgumentException($"{option} needs a value.");

        public int Int()
        {
            string text = Value();
            return int.TryParse(text, CultureInfo.InvariantCulture, out int number)
                ? number
                : throw new ArgumentException($"{option} needs a whole number, not '{text}'.");
        }

        public long Long()
        {
            string text = Value();
            return long.TryParse(text, CultureInfo.InvariantCulture, out long number)
                ? number
                : throw new ArgumentException($"{option} needs a whole number, not '{text}'.");
        }
    }
}
