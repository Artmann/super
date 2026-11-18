namespace Super;

/// <summary>
/// Chunk bakes made on this machine, by their file name (which holds the input hash), least recently read dropped
/// first. A file's last-write time is when it was last used, set from the <see cref="TimeProvider"/> on every read and
/// write. Writes come from the thread pool and reads from the render thread, so a read never waits for another
/// thread's file work: the lock in <see cref="HeldBakes"/> guards only a small map in memory, and the writing, moving and
/// trimming happen outside it. See "The bake cache" in docs/design.md.
/// </summary>
internal sealed class BakeCache
{
    /// <summary>The most bytes kept, 512 MB.</summary>
    public const long DefaultCapacity = 512L * 1024 * 1024;

    /// <summary>The most bytes of freshly written bakes held in memory, 64 MB.</summary>
    public const long DefaultHeldCapacity = 64L * 1024 * 1024;

    private readonly HeldBakes _held;
    private readonly CachedBakeFiles _files;
    private readonly BakeCacheTrimmer _trimmer;
    private readonly TimeProvider _time;
    private readonly long _capacity;

    /// <param name="folder">Where the files go. Made on the first write.</param>
    /// <param name="time">Says when a file was used.</param>
    /// <param name="capacity">The most bytes of bakes kept; the least recently used go first.</param>
    /// <param name="heldCapacity">The most bytes of freshly written bakes also held in memory; the oldest go first.</param>
    public BakeCache(string folder, TimeProvider time, long capacity = DefaultCapacity, long heldCapacity = DefaultHeldCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(heldCapacity);
        _files = new CachedBakeFiles(folder);
        _time = time;
        _capacity = capacity;
        _held = new HeldBakes(time, heldCapacity);
        _trimmer = new BakeCacheTrimmer(_files, _held, time, capacity);
    }

    /// <summary>Runs after a read's file is read and before its use time is set. For tests.</summary>
    internal Action? AfterRead { get; set; }

    /// <summary>Runs after a write's temp file is complete and before it is moved in, outside every lock. For tests.</summary>
    internal Action? BeforeMove { get; set; }

    /// <summary>Where the files go, as a full path.</summary>
    public string Folder => _files.Folder;

    /// <summary>The bytes of bakes kept now.</summary>
    public long Size => _files.All().Sum(file => file.Length);

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

    /// <summary>
    /// Where live play keeps its bakes: <c>Bakes</c> inside <paramref name="userDataFolder"/> when <c>--user-data</c>
    /// gives one, so nothing goes to the player's profile, and otherwise <see cref="FolderFor(string, UserDataPlatform, Func{string, string?})"/>.
    /// Null, with the reason written to <paramref name="log"/>, when the environment names no folder, so the game plays
    /// without a cache instead of stopping.
    /// </summary>
    internal static string? PlayFolder(
        string gameId, string? userDataFolder, UserDataPlatform platform, Func<string, string?> environment, TextWriter log)
    {
        if (userDataFolder is not null)
        {
            return Path.Combine(userDataFolder, "Bakes");
        }

        try
        {
            return FolderFor(gameId, platform, environment);
        }
        catch (InvalidOperationException e)
        {
            log.WriteLine($"Chunk bakes made while playing won't be kept: {e.Message}");
            return null;
        }
    }

    /// <summary>The platform this runs on, for <see cref="FolderFor(string, UserDataPlatform, Func{string, string?})"/>.</summary>
    internal static UserDataPlatform ThisPlatform =>
        OperatingSystem.IsWindows() ? UserDataPlatform.Windows
        : OperatingSystem.IsMacOS() ? UserDataPlatform.MacOS
        : UserDataPlatform.Linux;

    /// <summary>The usual folder for a game's bakes on this machine.</summary>
    public static string FolderFor(string gameId) => FolderFor(gameId, ThisPlatform, Environment.GetEnvironmentVariable);

    /// <summary>
    /// The bytes of <paramref name="file"/>, or null when it isn't kept. Marks it used now. A bake written a moment ago
    /// comes from memory, once, with no disk access. Otherwise the file is read, which is a disk read on the calling
    /// thread, but it never waits for a write, a move or a trim going on in another thread.
    /// </summary>
    public byte[]? Read(string file)
    {
        file = HeldBakes.Key(file);
        string path = _files.PathFor(file);
        if (_held.Take(file) is { } held)
        {
            return held;
        }

        // Gone, locked by another handle, or not a file: it isn't kept, so the chunk is baked again.
        if (CachedBakeFiles.ReadWhole(path) is not { } read)
        {
            return null;
        }

        AfterRead?.Invoke();

        // A problem here leaves the bake good; it only won't count as used now.
        CachedBakeFiles.TryMarkUsed(path, _time.GetUtcNow().UtcDateTime);
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
        file = HeldBakes.Key(file);
        string path = _files.PathFor(file);
        if (bytes.LongLength > _capacity)
        {
            // Whatever was kept under this name is the old bake, which a new one is meant to replace. Held open by a
            // reader, the old bake stays until a later write or trim gets it.
            _held.Drop(file);
            CachedBakeFiles.TryDelete(path);
            return;
        }

        _files.WriteWhole(path, bytes, _time.GetUtcNow().UtcDateTime, BeforeMove);
        _held.Hold(file, bytes);
        _trimmer.Trim(path);
    }
}
