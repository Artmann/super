using System.Security.Cryptography;
using System.Text;

namespace Super.Cook;

/// <summary>
/// Keeps what slow steps inside a cook made, such as a graph run or a compressed texture, in the cook cache's
/// <c>steps</c> folder, by a key that names everything the step read. A source that cooks again because one of its
/// inputs changed then redoes only the steps whose inputs changed. Each entry ends with the SHA-256 of what it holds,
/// so a damaged entry is a miss, never a wrong result. Entries are written to a temp file and moved into place, so
/// workers and builds can share the folder.
/// </summary>
internal sealed class StepCache(string folder)
{
    private const int HashLength = 32;

    private readonly string _folder = Path.Combine(Path.GetFullPath(folder), "steps");

    /// <summary>What was kept for <paramref name="key"/> of <paramref name="step"/>, or null when nothing was, or it was damaged.</summary>
    public byte[]? Find(string step, string key)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(EntryFile(step, key));
            if (bytes.Length < HashLength)
            {
                return null;
            }

            var payload = bytes.AsSpan(0, bytes.Length - HashLength);
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength)))
            {
                return null;
            }

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
