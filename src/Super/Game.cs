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

        bool hidden = arguments.ScreenshotPath is not null || arguments.RecordPath is not null || arguments.ProfilePath is not null;

        // Worked out even when unused, so a bad id fails in a screenshot run as well as in play.
        string playerFolder = UserData.FolderFor(options.Id ?? UserData.IdFromTitle(options.Title));

        // Runs without a window get a throwaway folder, so they never change the player's settings.
        string? userDataFolder = arguments.UserDataFolder ?? (hidden ? null : playerFolder);

        // Only live play sounds different each time. Screenshots, recordings and profiles keep the fixed audio seed.
        int? audioSeed = hidden ? null : Random.Shared.Next();
        // Only live play keeps going after an error: a screenshot, recording or profile should fail loudly.
        bool keepRunning = !hidden && HotReload.IsOn;
        if (keepRunning)
        {
            Console.WriteLine("Hot reload is on, so an error stops the game until the next change instead of closing it.");
        }

        using var host = new GameHost<TGame>(seed, options.TicksPerSecond, services, userDataFolder, audioSeed)
        {
            KeepsRunningAfterErrors = keepRunning,
        };
        host.Script = FindScript<TGame>(host, arguments.ScriptName);
        host.Ui.Scale = arguments.UiScale ?? options.UiScale;

        // Validation would swamp the timings, so a profile runs without it.
        bool validate = hidden && arguments.ProfilePath is null;
        using var window = new GameWindow(
            options.Title, arguments.Width ?? options.Width, arguments.Height ?? options.Height, hidden, validate, arguments.NoVsync);

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
        window.Run(host, options.TicksPerSecond);
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
