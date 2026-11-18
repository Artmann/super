namespace Super;

/// <summary>
/// Keeps a <see cref="BakeCache"/> within its capacity after each write. It runs under a lock of its own that only
/// writes take, so two writes never delete the same files, and a read never waits for it.
/// </summary>
internal sealed class BakeCacheTrimmer(CachedBakeFiles files, HeldBakes held, TimeProvider time, long capacity)
{
    // A temp file this much older than now is left from a crash, not from a write under way.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private bool _swept;

    /// <summary>
    /// Sweeps temp files left by a crash the first time, writes the uses read from memory onto their files, then deletes
    /// the oldest files, by use then name, until the total fits, leaving out <paramref name="written"/>, the file just
    /// written. What to delete is worked out first and deleted after; a delete that fails is left for the next trim.
    /// </summary>
    public void Trim(string written)
    {
        lock (_lock)
        {
            if (!_swept)
            {
                _swept = true;
                files.SweepUnfinished(time, AbandonedAfter);
            }

            foreach (var (name, at) in held.TakeUsed())
            {
                CachedBakeFiles.TryMarkUsed(files.PathFor(name), at);
            }

            DropOldest(written);
        }
    }

    private void DropOldest(string written)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var all = files.All().ToList();
        long total = all.Sum(file => file.Length);
        var oldestFirst = all
            .Where(file => !string.Equals(file.FullName, written, comparison))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.Ordinal);
        foreach (var file in oldestFirst)
        {
            if (total <= capacity)
            {
                break;
            }

            // In use or locked: the cache is over its capacity until a later trim gets it.
            long length = file.Length;
            if (CachedBakeFiles.TryDelete(file.FullName))
            {
                total -= length;
                held.Drop(HeldBakes.Key(Path.GetRelativePath(files.Folder, file.FullName)));
            }
        }
    }
}
