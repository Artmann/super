namespace Super;

/// <summary>
/// The folder a <see cref="BakeCache"/> keeps its files in, and the file work on it. A file that can't be read, marked
/// used or deleted, because it is gone, locked by another handle or not a file, is never an error here: the cache works
/// around it.
/// </summary>
internal sealed class CachedBakeFiles(string folder)
{
    private const string Unfinished = ".tmp";

    /// <summary>Where the files go, as a full path.</summary>
    public string Folder { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>The full path of <paramref name="file"/>, which must stay inside the folder.</summary>
    public string PathFor(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        string path = Path.GetFullPath(Path.Combine(Folder, file));
        string inside = Folder.EndsWith(Path.DirectorySeparatorChar) ? Folder : Folder + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(file) || !path.StartsWith(inside, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The bake name '{file}' must stay inside the cache folder.", nameof(file));
        }

        return path;
    }

    /// <summary>The bakes kept, leaving out temp files.</summary>
    public IEnumerable<FileInfo> All() =>
        Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !file.Name.EndsWith(Unfinished, StringComparison.Ordinal))
            : [];

    /// <summary>The bytes of the file at <paramref name="path"/>, or null when it can't be read.</summary>
    public static byte[]? ReadWhole(string path)
    {
        try
        {
            // Sharing delete lets a write move a new file over this one while it is read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] read = new byte[stream.Length];
            stream.ReadExactly(read);
            return read;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a temp file of its own, marks it used at <paramref name="at"/> and moves it
    /// over <paramref name="path"/>, so a read never sees half a bake and a crash leaves the old one.
    /// <paramref name="beforeMove"/> runs between the two, for tests.
    /// </summary>
    /// <exception cref="IOException">The write failed; nothing of the new file is left.</exception>
    public void WriteWhole(string path, byte[] bytes, DateTime at, Action? beforeMove)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string unfinished = $"{path}.{Guid.NewGuid():N}{Unfinished}";
        try
        {
            File.WriteAllBytes(unfinished, bytes);
            File.SetLastWriteTimeUtc(unfinished, at);
            beforeMove?.Invoke();
            File.Move(unfinished, path, overwrite: true);
        }
        catch
        {
            File.Delete(unfinished);
            throw;
        }
    }

    /// <summary>Sets the file's use time, its last-write time, unless it is gone or locked.</summary>
    public static void TryMarkUsed(string path, DateTime at)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, at);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It simply isn't counted as used then.
        }
    }

    /// <summary>Deletes the file, and says whether it could; one in use or locked is left for a later try.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the temp files last written more than <paramref name="age"/> before now, which a crash left rather than a
    /// write under way. Last written, not created: creation times aren't kept on every file system.
    /// </summary>
    public void SweepUnfinished(TimeProvider time, TimeSpan age)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime - age;
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*" + Unfinished, SearchOption.AllDirectories))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
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
}
