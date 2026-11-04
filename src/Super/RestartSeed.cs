using System.Globalization;

namespace Super;

/// <summary>
/// Under <c>dotnet watch</c>, an edit that hot reload can't apply restarts the game. This
/// keeps the first run's seed for those restarts, so the world comes back the same. A new
/// <c>dotnet watch</c>, or a run without it, picks a new seed as usual.
/// </summary>
internal static class RestartSeed
{
    /// <summary>Kept beside the program, where only runs from source will find it.</summary>
    internal const string FileName = "super-restart-seed.txt";

    /// <summary>The variable <c>dotnet watch</c> sets: 1 for its first run, then one more for each restart.</summary>
    internal const string WatchIterationVariable = "DOTNET_WATCH_ITERATION";

    internal readonly record struct Choice(int Seed, bool Kept);

    /// <param name="watchIteration">The value of <see cref="WatchIterationVariable"/>, or null without <c>dotnet watch</c>.</param>
    /// <param name="folder">Where the seed is kept between restarts.</param>
    /// <param name="fresh">Picks a new seed.</param>
    internal static Choice Pick(string? watchIteration, string folder, Func<int> fresh)
    {
        if (watchIteration is null)
        {
            return new Choice(fresh(), Kept: false);
        }

        string path = Path.Combine(folder, FileName);
        if (watchIteration != "1" && TryRead(path, out int kept))
        {
            return new Choice(kept, Kept: true);
        }

        int seed = fresh();
        try
        {
            File.WriteAllText(path, seed.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Without the file the next restart picks its own seed, which is how it was before.
        }

        return new Choice(seed, Kept: false);
    }

    private static bool TryRead(string path, out int seed)
    {
        seed = 0;
        try
        {
            return File.Exists(path)
                && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
