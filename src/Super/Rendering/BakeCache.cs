namespace Super;

/// <summary>
/// Chunk bakes made on this machine, by their file name (which holds the input hash), least recently read dropped
/// first. A file's last-write time is when it was last used, set from the <see cref="TimeProvider"/> on every read and
/// write. Writes come from the thread pool and reads from the render thread, so a read never waits for another
/// thread's file work: the lock guards only a small map in memory, and the writing, moving and trimming happen outside
/// it. See "The bake cache" in docs/design.md.
/// </summary>
internal sealed class BakeCache
{
    /// <summary>The most bytes kept, 512 MB.</summary>
    public const long DefaultCapacity = 512L * 1024 * 1024;

    // How many freshly written bakes are also held in memory, until each is read once.
    private const int HeldInMemory = 4;

    private const string Unfinished = ".tmp";

    // A temp file this much older than now is left from a crash, not from a write under way.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    // Guards _held only. Nothing is read, written or deleted while it is taken.
    private readonly Lock _lock = new();

    // Taken by writes, for the sweep and the trim, so two don't delete the same files. A read never takes it.
    private readonly Lock _trimLock = new();

    private readonly List<(string File, byte[] Bytes)> _held = [];

    // The bakes read from memory and when, for the next trim to write onto their files, which a read can't without
    // touching the disk.
    private readonly Dictionary<string, DateTime> _usedFromMemory = [];
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly long _capacity;
    private bool _swept;

    /// <param name="folder">Where the files go. Made on the first write.</param>
    /// <param name="time">Says when a file was used.</param>
    /// <param name="capacity">The most bytes of bakes kept; the least recently used go first.</param>
    public BakeCache(string folder, TimeProvider time, long capacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        _time = time;
        _capacity = capacity;
    }

    /// <summary>Runs after a read's file is read and before its use time is set. For tests.</summary>
    internal Action? AfterRead { get; set; }

    /// <summary>Runs after a write's temp file is complete and before it is moved in, outside every lock. For tests.</summary>
    internal Action? BeforeMove { get; set; }

    /// <summary>The bytes of bakes kept now.</summary>
    public long Size => Files().Sum(file => file.Length);

    /// <summary>LOCALAPPDATA\{id}\Bakes on Windows, ~/Library/Caches/{id}/Bakes on macOS, $XDG_CACHE_HOME/{id}/bakes or ~/.cache/{id}/bakes on Linux.</summary>
    public static string FolderFor(string gameId, UserDataPlatform platform, Func<string, string?> environment)
    {
        UserData.CheckId(gameId);
        return platform switch
        {
            UserDataPlatform.Windows => UserData.Join(UserData.Needed(environment, "LOCALAPPDATA"), '\\', gameId + "\\Bakes"),
            UserDataPlatform.MacOS => UserData.Join(UserData.Needed(environment, "HOME"), '/', "Library/Caches/" + gameId + "/Bakes"),
            _ => environment("XDG_CACHE_HOME") is { Length: > 0 } cache
                ? UserData.Join(cache, '/', gameId + "/bakes")
                : UserData.Join(UserData.Needed(environment, "HOME"), '/', ".cache/" + gameId + "/bakes"),
        };
    }

    /// <summary>The usual folder for a game's bakes on this machine.</summary>
    public static string FolderFor(string gameId)
    {
        var platform = OperatingSystem.IsWindows() ? UserDataPlatform.Windows
            : OperatingSystem.IsMacOS() ? UserDataPlatform.MacOS
            : UserDataPlatform.Linux;
        return FolderFor(gameId, platform, Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// The bytes of <paramref name="file"/>, or null when it isn't kept. Marks it used now. A bake written a moment ago
    /// comes from memory, once, with no disk access. Otherwise the file is read, which is a disk read on the calling
    /// thread, but it never waits for a write, a move or a trim going on in another thread.
    /// </summary>
    public byte[]? Read(string file)
    {
        string path = PathFor(file);
        lock (_lock)
        {
            int held = _held.FindIndex(entry => entry.File == file);
            if (held >= 0)
            {
                byte[] bytes = _held[held].Bytes;
                _held.RemoveAt(held);
                _usedFromMemory[file] = _time.GetUtcNow().UtcDateTime;
                return bytes;
            }
        }

        byte[] read;
        try
        {
            // Sharing delete lets a write move a new file over this one while it is read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = new byte[stream.Length];
            stream.ReadExactly(read);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        AfterRead?.Invoke();

        try
        {
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The bake is good; it only won't count as used now.
        }

        return read;
    }

    /// <summary>
    /// Keeps <paramref name="bytes"/> as <paramref name="file"/>, written to a temp file of its own and then moved in,
    /// so a read never sees half a bake and a crash leaves the old one. Then drops the least recently used files, never
    /// this one, until the total is at most the capacity; a file that can't be deleted is left. A file larger than the
    /// capacity isn't kept. The bytes are also held in memory until read once, so reading the bake straight after
    /// touches no disk. Call it off the render thread.
    /// </summary>
    /// <exception cref="IOException">The write failed; nothing of the new file is left.</exception>
    public void Write(string file, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string path = PathFor(file);
        if (bytes.LongLength > _capacity)
        {
            // Whatever was kept under this name is the old bake, which a new one is meant to replace.
            File.Delete(path);
            Unhold(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string unfinished = $"{path}.{Guid.NewGuid():N}{Unfinished}";
        try
        {
            File.WriteAllBytes(unfinished, bytes);
            File.SetLastWriteTimeUtc(unfinished, _time.GetUtcNow().UtcDateTime);
            BeforeMove?.Invoke();
            File.Move(unfinished, path, overwrite: true);
        }
        catch
        {
            File.Delete(unfinished);
            throw;
        }

        lock (_lock)
        {
            _held.RemoveAll(entry => entry.File == file);
            _usedFromMemory.Remove(file);
            _held.Add((file, bytes));
            if (_held.Count > HeldInMemory)
            {
                _held.RemoveAt(0);
            }
        }

        Trim(path);
    }

    private void Unhold(string file)
    {
        file = file.Replace('\\', '/');
        lock (_lock)
        {
            _held.RemoveAll(entry => entry.File.Replace('\\', '/') == file);
            _usedFromMemory.Remove(file);
        }
    }

    // Sweeps temp files left by a crash the first time, then deletes the oldest files, by use then name, until the total
    // fits, leaving out the file just written. What to delete is worked out first and deleted after; a delete that
    // fails is left for the next trim.
    private void Trim(string written)
    {
        lock (_trimLock)
        {
            if (!_swept)
            {
                _swept = true;
                Sweep();
            }

            KeyValuePair<string, DateTime>[] used;
            lock (_lock)
            {
                used = [.. _usedFromMemory];
                _usedFromMemory.Clear();
            }

            foreach (var (name, at) in used)
            {
                try
                {
                    File.SetLastWriteTimeUtc(PathFor(name), at);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked: it simply isn't counted as used then.
                }
            }

            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var files = Files().ToList();
            long total = files.Sum(file => file.Length);
            var oldestFirst = files
                .Where(file => !string.Equals(file.FullName, written, comparison))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.FullName, StringComparer.Ordinal);
            foreach (var file in oldestFirst)
            {
                if (total <= _capacity)
                {
                    break;
                }

                long length = file.Length;
                try
                {
                    file.Delete();
                    total -= length;
                    Unhold(Path.GetRelativePath(_folder, file.FullName).Replace('\\', '/'));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // In use or locked: the cache is over its capacity until a later trim gets it.
                }
            }
        }
    }

    private void Sweep()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - AbandonedAfter;
        foreach (var file in new DirectoryInfo(_folder).EnumerateFiles("*" + Unfinished, SearchOption.AllDirectories))
        {
            try
            {
                if (file.CreationTimeUtc < cutoff)
                {
                    file.Delete();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left for the next session.
            }
        }
    }

    // The bakes kept, leaving out temp files.
    private IEnumerable<FileInfo> Files() =>
        Directory.Exists(_folder)
            ? new DirectoryInfo(_folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !file.Name.EndsWith(Unfinished, StringComparison.Ordinal))
            : [];

    private string PathFor(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        string path = Path.GetFullPath(Path.Combine(_folder, file));
        string inside = _folder.EndsWith(Path.DirectorySeparatorChar) ? _folder : _folder + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(file) || !path.StartsWith(inside, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The bake name '{file}' must stay inside the cache folder.", nameof(file));
        }

        return path;
    }
}
