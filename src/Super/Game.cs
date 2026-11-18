using Microsoft.Extensions.DependencyInjection;

namespace Super;

public static class Game
{
    /// <summary>
    /// Opens a window and runs the game until it closes. Register your own classes in
    /// <paramref name="services"/>. GameTime and GameRandom are already there.
    /// </summary>
    public static void StartWindowsGameLoop<TGame>(GameOptions? options = null, Action<IServiceCollection>? services = null)
        where TGame : class, IGame
    {
        options ??= new GameOptions();
        var arguments = GameArguments.Parse(Environment.GetCommandLineArgs().AsSpan(1));
        int seed = ChooseSeed(arguments, options);

        // Checked even when unused, so a bad id fails in a screenshot run as well as in play.
        string gameId = options.Id ?? UserData.IdFromTitle(options.Title);
        UserData.CheckId(gameId);

        using var host = MakeHost<TGame>(options, arguments, services, seed, gameId);
        host.Script = FindScript<TGame>(host, arguments.ScriptName);
        host.Ui.Scale = arguments.UiScale ?? options.UiScale;

        using var window = MakeWindow(options, arguments, host.Graphics);
        if (!PrepareWindow(host, window, arguments))
        {
            return;
        }

        if (!RunHidden(host, window, options, arguments))
        {
            PlayLive(host, window, gameId, arguments);
        }
    }

    /// <summary>The seed the game plays with, said on the console. It is the one thing allowed to come from outside the game.</summary>
    private static int ChooseSeed(GameArguments arguments, GameOptions options)
    {
        if ((arguments.Seed ?? options.Seed) is { } chosen)
        {
            Console.WriteLine($"seed {chosen}");
            return chosen;
        }

        var pick = RestartSeed.Pick(
            Environment.GetEnvironmentVariable(RestartSeed.WatchIterationVariable), AppContext.BaseDirectory, Random.Shared.Next);
        Console.WriteLine(pick.Kept ? $"seed {pick.Seed}, kept from before the restart" : $"seed {pick.Seed}");
        return pick.Seed;
    }

    /// <summary>Whether the run has no window anyone sees: a screenshot, a recording, a profile or a bake.</summary>
    private static bool RunsHidden(GameArguments arguments) =>
        arguments.ScreenshotPath is not null || arguments.RecordPath is not null || arguments.ProfilePath is not null ||
        arguments.Bake || arguments.BakeCheck;

    /// <summary>Builds the game on its services, with the graphics settings the command line asks for.</summary>
    private static GameHost<TGame> MakeHost<TGame>(
        GameOptions options, GameArguments arguments, Action<IServiceCollection>? services, int seed, string gameId)
        where TGame : class, IGame
    {
        bool hidden = RunsHidden(arguments);

        // Runs without a window get a throwaway folder, so they never change the player's settings. The player's folder
        // is only looked up when it is used, so --user-data runs where the environment names no folder.
        string? userDataFolder = arguments.UserDataFolder ?? (hidden ? null : UserData.FolderFor(gameId));

        // Only live play sounds different each time. Screenshots, recordings and profiles keep the fixed audio seed.
        int? audioSeed = hidden ? null : Random.Shared.Next();
        // Only live play keeps going after an error: a screenshot, recording or profile should fail loudly.
        bool keepRunning = !hidden && HotReload.IsOn;
        if (keepRunning)
        {
            Console.WriteLine("Hot reload is on, so an error stops the game until the next change instead of closing it.");
        }

        ApplyGraphicsArguments(options.Graphics, arguments, gpu: null);
        return new GameHost<TGame>(seed, options.TicksPerSecond, services, userDataFolder, audioSeed, options.Graphics)
        {
            KeepsRunningAfterErrors = keepRunning,
        };
    }

    /// <summary>Opens the window, hidden for a run nobody watches.</summary>
    private static GameWindow MakeWindow(GameOptions options, GameArguments arguments, Graphics graphics)
    {
        bool hidden = RunsHidden(arguments);
        var setup = new WindowSetup(options.Title, arguments.Width ?? options.Width, arguments.Height ?? options.Height)
        {
            Hidden = hidden,
            // Validation would swamp the timings, so a profile runs without it.
            Validate = hidden && arguments.ProfilePath is null,
            Maximized = options.Maximized,
            Borderless = options.Borderless,
            NoRayTracing = arguments.NoTrace,
        };
        return new GameWindow(setup, graphics);
    }

    /// <summary>
    /// Sets the game up for the window: the debug view, the interface's scale and the GPU. Returns false, with the
    /// exit code set, when the run can't show the view it asked for.
    /// </summary>
    private static bool PrepareWindow<TGame>(GameHost<TGame> host, GameWindow window, GameArguments arguments)
        where TGame : class, IGame
    {
        // Said before anything plays, so a run that can't show the view fails at once instead of on its first frame.
        // Why tracing is off is only said here, when something asked for it, so players without it see nothing.
        if (GameArguments.DebugViewError(arguments.DebugView, window.CanTrace, window.NoTraceReason) is { } debugViewError)
        {
            Console.Error.WriteLine(debugViewError);
            Environment.ExitCode = 1;
            return false;
        }

        // On a Retina screen the window has twice the pixels, so the interface is drawn twice as big to look the same.
        // A hidden window, for a picture or a video, has the pixels asked for, so it keeps the scale.
        host.Ui.Scale *= window.PixelsPerPoint;

        window.DebugView = arguments.DebugView;
        host.Gpu.Fill(window.Gpu);
        if (arguments.GraphicsProfile == GameArguments.AutoProfile)
        {
            // Only now is the GPU known. The window already has the settings it started with, so the first frame
            // rebuilds whatever the recommended ones change, once.
            Console.WriteLine($"{window.Gpu.Name}: {Graphics.Recommend(window.Gpu)} graphics");
            ApplyGraphicsArguments(host.Graphics, arguments, window.Gpu);
        }

        return true;
    }

    /// <summary>
    /// Runs what the command line asked for instead of play, if it did: a profile, a screenshot, a bake or a recording,
    /// and sets the exit code. Returns false when it asked for none, so the game is played.
    /// </summary>
    private static bool RunHidden<TGame>(GameHost<TGame> host, GameWindow window, GameOptions options, GameArguments arguments)
        where TGame : class, IGame
    {
        if (arguments.ProfilePath is { } report)
        {
            window.Profile(host, new ProfileRequest(options.Title, host.Seed, arguments.Ticks ?? 0, arguments.Frames, report));
            Console.WriteLine($"{report}: {arguments.Frames} frames");
            return true;
        }

        if (arguments.ScreenshotPath is { } path)
        {
            int problems = window.Screenshot(host, arguments.Ticks ?? 0, path);
            Console.WriteLine($"{path}: {problems} validation messages");
            Environment.ExitCode = problems == 0 ? 0 : 1;
            return true;
        }

        if (arguments.Bake || arguments.BakeCheck)
        {
            RunBake(host, window, arguments);
            return true;
        }

        if (arguments.RecordPath is { } video)
        {
            int frames = (int)Math.Round(arguments.Seconds!.Value * arguments.FramesPerSecond);
            int problems = window.Record(host, arguments.Ticks ?? 0, video, frames, arguments.FramesPerSecond);
            Console.WriteLine($"{video}: {frames} frames, {problems} validation messages");
            Environment.ExitCode = problems == 0 ? 0 : 1;
            return true;
        }

        return false;
    }

    /// <summary>Runs a <c>--bake</c> or a <c>--bake-check</c>, and sets the exit code.</summary>
    private static void RunBake(GameLoop host, GameWindow window, GameArguments arguments)
    {
        // Two seconds by default, all of them drawn, so a light that sways or flickers is seen moving and left out
        // of the bake, as it is in play. After one tick it would have been drawn only where it stood then.
        long bakeTicks = BakeTicks(arguments.Ticks, host.Time.TicksPerSecond);
        if (!arguments.Bake)
        {
            // The check reads the cooked bake, the one the game draws with, so it works in a published game too.
            Environment.ExitCode = window.BakeCheck(host, bakeTicks) == 0 ? 0 : 1;
            return;
        }

        // Bakes are sources, like the scene's models, so they're saved beside them in the first content root.
        if (host.Content.Store.Files is not CookedFolder { Dev: { Roots.Length: > 0 } dev })
        {
            Console.Error.WriteLine(
                $"--bake needs the game run from source, where the build writes {DevSettings.FileName} to say where the content is.");
            Environment.ExitCode = 1;
            return;
        }

        int problems = 0;
        int failed = 0;
        if (!TryBake(() => problems = window.Bake(host, bakeTicks, arguments.BakeQuality, dev.Roots[0], out failed), Console.Error))
        {
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"{problems} validation messages");
        Environment.ExitCode = problems == 0 && failed == 0 ? 0 : 1;
    }

    /// <summary>Plays the game in the window until it closes.</summary>
    private static void PlayLive(GameLoop host, GameWindow window, string gameId, GameArguments arguments)
    {
        // Only live play gets here, so only it plays through the speakers. Without a device it runs silently, with the
        // game loop still moving the audio on a tick at a time.
        host.Audio.Device.Open(Console.Error);

        // Only live play bakes missing lighting by itself and reads the chunk bakes it made before, so a screenshot,
        // recording, profile or bake draws only what was cooked, the same on every machine.
        // Under --user-data when it is given; none, and a line saying so, when the environment names no folder.
        if (BakeCache.PlayFolder(gameId, arguments.UserDataFolder, BakeCache.ThisPlatform, Environment.GetEnvironmentVariable, Console.Out) is { } bakes)
        {
            window.BakeCache = new BakeCache(bakes, TimeProvider.System);
        }

        window.Run(host);
    }

    /// <summary>
    /// The ticks <c>--bake</c> and <c>--bake-check</c> play: <paramref name="ticks"/> when <c>--ticks</c> gives it,
    /// otherwise two seconds, more than the second a moving light needs to hold still before it returns to the bake,
    /// so the frame sees every light that moves at least once a second as moving.
    /// </summary>
    internal static long BakeTicks(long? ticks, int ticksPerSecond) => ticks ?? 2L * ticksPerSecond;

    /// <summary>
    /// Runs a <c>--bake</c>. An <see cref="InvalidOperationException"/> is what a bake that can't be made throws (no
    /// baked space, an area with nothing to bake, draws that don't fit the pages), so its message goes to
    /// <paramref name="error"/> as one line, as the other command-line errors do, and this returns false.
    /// </summary>
    internal static bool TryBake(Action bake, TextWriter error)
    {
        try
        {
            bake();
            return true;
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine(exception.Message);
            return false;
        }
    }

    private static InputScript? FindScript<TGame>(GameHost<TGame> host, string? name)
        where TGame : class, IGame
    {
        if (name is null)
        {
            return null;
        }

        var scripts = ((IGame)host.Game).Scripts;
        return scripts.TryGetValue(name, out var script)
            ? script
            : throw new ArgumentException($"There's no script named '{name}'. This game has: {string.Join(", ", scripts.Keys)}.");
    }

    /// <summary>
    /// Sets <paramref name="graphics"/> from the command line: first the whole profile <c>--graphics</c> names, then the
    /// single settings such as <c>--vsync</c> and <c>--render-scale</c> on top, so they win over the profile. With
    /// <c>--graphics auto</c> and no <paramref name="gpu"/> yet, the profile waits and only the single settings apply.
    /// </summary>
    internal static void ApplyGraphicsArguments(Graphics graphics, GameArguments arguments, Rendition.GpuInfo? gpu)
    {
        if (arguments.GraphicsProfile is { } name && GameArguments.GraphicsFor(name, gpu) is { } profile)
        {
            graphics.Apply(profile);
        }

        if (arguments.Vsync)
        {
            graphics.Screen.Vsync = true;
        }

        if (arguments.NoMaterials)
        {
            graphics.Surfaces.Materials = false;
        }

        if (arguments.RenderScale is { } renderScale)
        {
            graphics.Screen.RenderScale = renderScale;
        }

        if (arguments.Upscaler is { } upscaler)
        {
            graphics.Screen.Upscaler = upscaler;
        }
    }
}
