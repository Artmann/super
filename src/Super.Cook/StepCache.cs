using System.Security.Cryptography;
using System.Text;

namespace Super.Cook;

/// <summary>
/// Keeps what slow steps inside a cook made, such as a graph run or a compressed texture, in the cook cache's
/// <c>steps</c> folder, by a key that names everything the step read. A source that cooks again because one of its
/// inputs changed then redoes only the steps whose inputs changed. Each entry ends with the SHA-256 of what it holds,
/// so a damaged entry is a miss, never a wrong result. Entries are written to a temp file and moved into place, so
/// workers and builds can share the folder. Every entry found or kept is noted in <paramref name="usage"/>, so trimming
/// the cache keeps it.
/// </summary>
internal sealed class StepCache(string folder, CacheUsage? usage = null)
{
    private const int HashLength = 32;

    private readonly string _root = Path.GetFullPath(folder);
    private readonly string _folder = Path.Combine(Path.GetFullPath(folder), "steps");
    private readonly CacheUsage _usage = usage ?? new CacheUsage(DateTime.UtcNow);

    /// <summary>What was kept for <paramref name="key"/> of <paramref name="step"/>, or null when nothing was, or it was damaged.</summary>
    public byte[]? Find(string step, string key)
    {
        string path = EntryFile(step, key);
        try
        {
            byte[] bytes;
            DateTime lastWrite;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            }

            if (bytes.Length < HashLength)
            {
                return null;
            }

            var payload = bytes.AsSpan(0, bytes.Length - HashLength);
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength)))
            {
                return null;
            }

            _usage.Used(path, lastWrite);
            return payload.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps <paramref name="bytes"/> for <paramref name="key"/>. Failing to write is never a failure of the cook.</summary>
    public void Keep(string step, string key, byte[] bytes)
    {
        string path = EntryFile(step, key);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Write(SHA256.HashData(bytes));
            }

            File.Move(temp, path, overwrite: true);
            _usage.Wrote(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation, as in CookCache.Save.
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Keeps a step's result that is also an object of the cook, such as a compressed texture a model writes, without
    /// keeping its bytes twice: the object goes into the cook cache's <c>objects</c>, where the cook would put it anyway,
    /// and the entry holds only its hash.
    /// </summary>
    /// <param name="hash">The SHA-256 of <paramref name="bytes"/> in lowercase hex.</param>
    public void KeepObject(string step, string key, string hash, byte[] bytes)
    {
        try
        {
            CookCache.SaveObject(_root, hash, bytes, _usage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Keep(step, key, Encoding.ASCII.GetBytes(hash));
    }

    /// <summary>
    /// The object kept with <see cref="KeepObject"/> for <paramref name="key"/> of <paramref name="step"/>, checked against
    /// its hash, or null when there's no entry or the object is gone or damaged, such as after trimming the cache.
    /// </summary>
    public (string Hash, byte[] Bytes)? FindObject(string step, string key)
    {
        if (Find(step, key) is not { } entry)
        {
            return null;
        }

        string hash = Encoding.ASCII.GetString(entry);
        return CookCache.ReadObject(_root, hash, _usage) is { } bytes ? (hash, bytes) : null;
    }

    private string EntryFile(string step, string key)
    {
        if (step.Length == 0 || !step.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException($"A step's name is letters, digits and dashes, not \"{step}\".", nameof(step));
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_folder, step, hash[..2], hash);
    }
}
