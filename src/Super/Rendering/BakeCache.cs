namespace Super;

/// <summary>
/// Chunk bakes made on this machine, by their file name (which holds the input hash), least recently read dropped
/// first. A file's last-write time is when it was last used, set from the <see cref="TimeProvider"/> on every read and
/// write. Writes come from the thread pool and reads from the render thread, so the class locks around its own work.
/// See "The bake cache" in docs/design.md.
/// </summary>
internal sealed class BakeCache
{
    /// <summary>The most bytes kept, 512 MB.</summary>
    public const long DefaultCapacity = 512L * 1024 * 1024;

    private const string Unfinished = ".tmp";

    private readonly Lock _lock = new();
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly long _capacity;

    /// <param name="folder">Where the files go. Made on the first write.</param>
    /// <param name="time">Says when a file was used.</param>
    /// <param name="capacity">The most bytes of bakes kept; the least recently used go first.</param>
    public BakeCache(string folder, TimeProvider time, long capacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _folder = Path.GetFullPath(folder);
        _time = time;
        _capacity = capacity;
    }

    /// <summary>The bytes of bakes kept now.</summary>
    public long Size
    {
        get
        {
            lock (_lock)
            {
                return Files().Sum(file => file.Length);
            }
        }
    }

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

    /// <summary>The bytes of <paramref name="file"/>, or null when it isn't kept. Marks it used now.</summary>
    public byte[]? Read(string file)
    {
        string path = PathFor(file);
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
            return bytes;
        }
    }

    /// <summary>
    /// Keeps <paramref name="bytes"/> as <paramref name="file"/>, written to a temp file and then moved in, so a read
    /// never sees half a bake and a crash leaves the old one. Then drops the least recently used files until the
    /// total is at most the capacity. A file larger than the capacity isn't kept.
    /// </summary>
    /// <exception cref="IOException">The write failed; nothing of the new file is left.</exception>
    public void Write(string file, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string path = PathFor(file);
        lock (_lock)
        {
            if (bytes.LongLength > _capacity)
            {
                // Whatever was kept under this name is the old bake, which a new one is meant to replace.
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string unfinished = path + Unfinished;
            try
            {
                File.WriteAllBytes(unfinished, bytes);
                File.SetLastWriteTimeUtc(unfinished, _time.GetUtcNow().UtcDateTime);
                File.Move(unfinished, path, overwrite: true);
            }
            catch
            {
                File.Delete(unfinished);
                throw;
            }

            Trim();
        }
    }

    // Deletes the oldest files, by use then name, until the total fits.
    private void Trim()
    {
        var files = Files().OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.Ordinal).ToList();
        long total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= _capacity)
            {
                break;
            }

            long length = file.Length;
            file.Delete();
            total -= length;
        }
    }

    // The bakes kept, leaving out a temp file from a write that crashed.
    private IEnumerable<FileInfo> Files() =>
        Directory.Exists(_folder)
            ? new DirectoryInfo(_folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !file.Name.EndsWith(Unfinished, StringComparison.Ordinal))
            : [];

    private string PathFor(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        string path = Path.GetFullPath(Path.Combine(_folder, file));
        if (Path.IsPathRooted(file) || !path.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The bake name '{file}' must stay inside the cache folder.", nameof(file));
        }

        return path;
    }
}
