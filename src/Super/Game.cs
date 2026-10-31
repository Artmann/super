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
        int seed = arguments.Seed ?? options.Seed ?? Random.Shared.Next();
        Console.WriteLine($"seed {seed}");

        using var host = new GameHost<TGame>(seed, options.TicksPerSecond, services);
        using var window = new GameWindow(options, hidden: arguments.ScreenshotPath is not null);

        if (arguments.ScreenshotPath is { } path)
        {
            int problems = window.Screenshot(host, arguments.Ticks ?? 0, path);
            Console.WriteLine($"{path}: {problems} validation messages");
            Environment.ExitCode = problems == 0 ? 0 : 1;
            return;
        }

        window.Run(host, options.TicksPerSecond);
    }
}
