using System.Security.Cryptography;

namespace Super.Cook;

/// <summary>
/// The objects a cook cache keeps, in its <c>objects</c> folder by their SHA-256. The record cache and the step cache both
/// keep objects there from parallel workers, so one object is written by one of them at a time.
/// </summary>
internal static class CacheObjects
{
    // Locks for object writes, picked by the object's path. A fixed set, shared by every cook in the process, so the
    // record and step caches gate the same object with the same lock and the set doesn't grow with each object.
    private static readonly object[] Gates = [.. Enumerable.Range(0, 64).Select(_ => new object())];

    /// <summary>The file of the object <paramref name="hash"/> in the cache in <paramref name="folder"/>.</summary>
    public static string PathOf(string folder, string hash)
    {
        return Path.Combine(folder, "objects", hash[..2], hash);
    }

    /// <summary>
    /// Keeps an object in the cache in <paramref name="folder"/> unless it has it already, and notes it in
    /// <paramref name="usage"/>. When another worker has written it first, it is only noted.
    /// </summary>
    /// <exception cref="IOException">It couldn't be written.</exception>
    public static void Save(string folder, string hash, byte[] bytes, CacheUsage usage)
    {
        var info = new FileInfo(PathOf(folder, hash));
        lock (Gates[(int)((uint)CacheFolder.PathComparer.GetHashCode(info.FullName) % (uint)Gates.Length)])
        {
            if (info.Exists)
            {
                usage.Used(info.FullName, info.LastWriteTimeUtc);
                return;
            }

            WriteAtomically(info.FullName, bytes);
            usage.Wrote(info.FullName);
        }
    }

    /// <summary>
    /// The bytes of the object <paramref name="hash"/> in the cache in <paramref name="folder"/>, checked against its hash,
    /// and notes it in <paramref name="usage"/>. Null when it isn't there, can't be read or is damaged.
    /// </summary>
    public static byte[]? Read(string folder, string hash, CacheUsage usage)
    {
        if (!ContentManifest.IsHash(hash))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(PathOf(folder, hash));
            if (!info.Exists)
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(info.FullName);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
            {
                return null;
            }

            usage.Used(info.FullName, info.LastWriteTimeUtc);
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a temp file and moves it into place, so a reader never sees half a file.</summary>
    public static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
