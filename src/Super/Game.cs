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

        // The seed is the one thing here allowed to come from outside the game.
        int seed;
        if ((arguments.Seed ?? options.Seed) is { } chosen)
        {
            seed = chosen;
            Console.WriteLine($"seed {seed}");
        }
        else
        {
            var pick = RestartSeed.Pick(
                Environment.GetEnvironmentVariable(RestartSeed.WatchIterationVariable), AppContext.BaseDirectory, Random.Shared.Next);
            seed = pick.Seed;
            Console.WriteLine(pick.Kept ? $"seed {seed}, kept from before the restart" : $"seed {seed}");
        }

        bool baking = arguments.Bake || arguments.BakeCheck;
        bool hidden = arguments.ScreenshotPath is not null || arguments.RecordPath is not null || arguments.ProfilePath is not null || baking;

        // Checked even when unused, so a bad id fails in a screenshot run as well as in play.
        string gameId = options.Id ?? UserData.IdFromTitle(options.Title);
        UserData.CheckId(gameId);

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

        if (arguments.NoVsync)
        {
            options.Graphics.Vsync = false;
        }

        if (arguments.NoMaterials)
        {
            options.Graphics.Materials = false;
        }

        if (arguments.RenderScale is { } renderScale)
        {
            options.Graphics.RenderScale = renderScale;
        }

        if (arguments.Upscaler is { } upscaler)
        {
            options.Graphics.Upscaler = upscaler;
        }

        using var host = new GameHost<TGame>(seed, options.TicksPerSecond, services, userDataFolder, audioSeed, options.Graphics)
        {
            KeepsRunningAfterErrors = keepRunning,
        };
        host.Script = FindScript<TGame>(host, arguments.ScriptName);
        host.Ui.Scale = arguments.UiScale ?? options.UiScale;

        // Validation would swamp the timings, so a profile runs without it.
        bool validate = hidden && arguments.ProfilePath is null;
        using var window = new GameWindow(
            options.Title, arguments.Width ?? options.Width, arguments.Height ?? options.Height, hidden, validate, !host.Graphics.Vsync,
            host.Graphics.ShadowResolution, host.Graphics.RenderScale, host.Graphics.Upscaler, options.Maximized, options.Borderless,
            arguments.NoTrace);

        // Said before anything plays, so a run that can't show the view fails at once instead of on its first frame.
        // Why tracing is off is only said here, when something asked for it, so players without it see nothing.
        if (GameArguments.DebugViewError(arguments.DebugView, window.CanTrace, window.NoTraceReason) is { } debugViewError)
        {
            Console.Error.WriteLine(debugViewError);
            Environment.ExitCode = 1;
            return;
        }

        window.DebugView = arguments.DebugView;

        if (arguments.ProfilePath is { } report)
        {
            window.Profile(host, options.Title, seed, arguments.Ticks ?? 0, arguments.Frames, options.TicksPerSecond, report);
            Console.WriteLine($"{report}: {arguments.Frames} frames");
            return;
        }

        if (arguments.ScreenshotPath is { } path)
        {
            int problems = window.Screenshot(host, arguments.Ticks ?? 0, path);
            Console.WriteLine($"{path}: {problems} validation messages");
            Environment.ExitCode = problems == 0 ? 0 : 1;
            return;
        }

        if (baking)
        {
            // Two seconds by default, all of them drawn, so a light that sways or flickers is seen moving and left out
            // of the bake, as it is in play. After one tick it would have been drawn only where it stood then.
            long bakeTicks = BakeTicks(arguments.Ticks, options.TicksPerSecond);
            if (!arguments.Bake)
            {
                // The check reads the cooked bake, the one the game draws with, so it works in a published game too.
                Environment.ExitCode = window.BakeCheck(host, bakeTicks) == 0 ? 0 : 1;
                return;
            }

            // Bakes are sources, like the scene's models, so they're saved beside them in the first content root.
            if (host.Content.Files is not CookedFolder { Dev: { Roots.Length: > 0 } dev })
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
            return;
        }

        if (arguments.RecordPath is { } video)
        {
            int frames = (int)Math.Round(arguments.Seconds!.Value * arguments.FramesPerSecond);
            int problems = window.Record(host, arguments.Ticks ?? 0, video, frames, arguments.FramesPerSecond, options.TicksPerSecond);
            Console.WriteLine($"{video}: {frames} frames, {problems} validation messages");
            Environment.ExitCode = problems == 0 ? 0 : 1;
            return;
        }

        // Only live play gets here, so only it plays through the speakers. Without a device it runs silently, with the
        // game loop still moving the audio on a tick at a time.
        host.Audio.UseDevice(Console.Error);

        // Only live play bakes missing lighting by itself and reads the chunk bakes it made before, so a screenshot,
        // recording, profile or bake draws only what was cooked, the same on every machine.
        // Under --user-data when it is given; none, and a line saying so, when the environment names no folder.
        if (BakeCache.PlayFolder(gameId, arguments.UserDataFolder, BakeCache.ThisPlatform, Environment.GetEnvironmentVariable, Console.Out) is { } bakes)
        {
            window.BakeCache = new BakeCache(bakes, TimeProvider.System);
        }

        window.Run(host, options.TicksPerSecond);
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
}
