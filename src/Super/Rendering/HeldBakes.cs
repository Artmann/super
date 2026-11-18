namespace Super;

/// <summary>
/// The bakes a <see cref="BakeCache"/> wrote last, also held in memory until each is read once, so reading a bake
/// straight after it was written touches no disk. Reads come from the render thread and writes from the thread pool,
/// so one lock guards the list, and nothing is read, written or deleted while it is taken.
/// </summary>
internal sealed class HeldBakes(TimeProvider time, long capacity)
{
    // How many freshly written bakes are held, within the capacity in bytes.
    private const int MostHeld = 4;

    private readonly Lock _lock = new();
    private readonly List<(string File, byte[] Bytes)> _held = [];

    // The bakes read from memory and when, for the next trim to write onto their files, which a read can't without
    // touching the disk.
    private readonly Dictionary<string, DateTime> _usedFromMemory = [];

    /// <summary>A name as the cache keys it, with forward slashes, so a name given either way finds the same bake.</summary>
    public static string Key(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        return file.Replace('\\', '/');
    }

    /// <summary>The held bytes of <paramref name="file"/>, let go and marked used now, or null when they aren't held.</summary>
    public byte[]? Take(string file)
    {
        lock (_lock)
        {
            int held = _held.FindIndex(entry => entry.File == file);
            if (held < 0)
            {
                return null;
            }

            byte[] bytes = _held[held].Bytes;
            _held.RemoveAt(held);
            _usedFromMemory[file] = time.GetUtcNow().UtcDateTime;
            return bytes;
        }
    }

    /// <summary>
    /// Holds the bytes just written as <paramref name="file"/> in place of any held before, unless they are larger than
    /// the capacity, and lets go of the oldest until the held bakes fit.
    /// </summary>
    public void Hold(string file, byte[] bytes)
    {
        lock (_lock)
        {
            _held.RemoveAll(entry => entry.File == file);
            _usedFromMemory.Remove(file);
            if (bytes.LongLength > capacity)
            {
                return;
            }

            _held.Add((file, bytes));
            long heldBytes = _held.Sum(entry => entry.Bytes.LongLength);
            while (_held.Count > MostHeld || heldBytes > capacity)
            {
                heldBytes -= _held[0].Bytes.LongLength;
                _held.RemoveAt(0);
            }
        }
    }

    /// <summary>Lets go of <paramref name="file"/>, a name from <see cref="Key"/>, and forgets that it was read from memory.</summary>
    public void Drop(string file)
    {
        lock (_lock)
        {
            _held.RemoveAll(entry => entry.File == file);
            _usedFromMemory.Remove(file);
        }
    }

    /// <summary>The bakes read from memory since the last call, and when.</summary>
    public KeyValuePair<string, DateTime>[] TakeUsed()
    {
        lock (_lock)
        {
            KeyValuePair<string, DateTime>[] used = [.. _usedFromMemory];
            _usedFromMemory.Clear();
            return used;
        }
    }
}
