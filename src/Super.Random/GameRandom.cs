using System.Text;

namespace Super;

/// <summary>
/// The game's source of randomness. One seed gives any number of named streams, and
/// each stream has its own sequence, so a new roll in combat can't change the weather.
/// </summary>
public sealed class GameRandom(int seed)
{
    private readonly Dictionary<string, RandomStream> _streams = [];

    public int Seed { get; } = seed;

    /// <summary>
    /// The stream with this name. Asking twice gives the same stream, and the sequence
    /// depends only on the seed and the name, not on the order streams are asked for.
    /// </summary>
    public RandomStream Stream(string name)
    {
        if (!_streams.TryGetValue(name, out var stream))
        {
            stream = new RandomStream(RandomStream.Mix((ulong)Seed) ^ HashName(name));
            _streams.Add(name, stream);
        }

        return stream;
    }

    /// <summary>
    /// FNV-1a. string.GetHashCode can't be used here because it changes between runs of
    /// the same program, which would make replays impossible.
    /// </summary>
    private static ulong HashName(string name)
    {
        ulong hash = 14695981039346656037;
        foreach (byte value in Encoding.UTF8.GetBytes(name))
        {
            hash = (hash ^ value) * 1099511628211;
        }

        return hash;
    }
}
