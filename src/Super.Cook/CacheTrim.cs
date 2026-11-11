using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Super.Cook;

/// <summary>What trimming the cook cache did, for <c>--timings</c> and <c>super-cook cache</c>.</summary>
/// <param name="Size">The cache's size afterwards, in bytes.</param>
/// <param name="Limit">The limit it was trimmed to, in bytes, or 0 for none.</param>
/// <param name="Deleted">How many files were deleted, least recently used first, stale temp files included.</param>
/// <param name="Freed">How many bytes the deleted files held.</param>
/// <param name="Used">How many bytes of the cache the cook itself used, which are never deleted.</param>
public sealed record CacheTrim(long Size, long Limit, int Deleted, long Freed, long Used)
{
    /// <summary>Whether what the cook used is more than the limit on its own, so the cache can't get under it.</summary>
    public bool CookAloneOverLimit => Limit > 0 && Used > Limit;

    /// <summary>One line for <c>--timings</c>, such as <c>Cache 3.9 GB of 4.0 GB: deleted 12 least recently used files, 1.1 GB.</c></summary>
    public string Describe()
    {
        string limit = Limit > 0 ? " of " + CacheSize.Format(Limit) : ", no limit";
        string deleted = Deleted > 0
            ? $": deleted {Deleted} least recently used {(Deleted == 1 ? "file" : "files")}, {CacheSize.Format(Freed)}."
            : ".";
        return $"Cache {CacheSize.Format(Size)}{limit}{deleted}";
    }

    /// <summary>The warning for a cook that needs more cache than the limit allows, or null when it doesn't.</summary>
    public string? Warning() => CookAloneOverLimit
        ? $"This cook uses {CacheSize.Format(Used)} of the cook cache, more than its limit of {CacheSize.Format(Limit)}, so the cache is kept over it. "
            + "Raise the limit with SUPER_CACHE_SIZE or --cache-limit, such as --cache-limit 8GB."
        : null;
}

/// <summary>
/// The files of the cook cache that one cook used: records it restored or wrote, their objects, and step entries.
/// Using a file sets its last-write time to now, so the cache's last-write times say when each file was last used and
/// trimming deletes the least recently used first. A file used in the last <see cref="TouchAfter"/> isn't touched
/// again, so a warm cook within the hour of the last writes nothing. Safe to use from several workers.
/// </summary>
internal sealed class CacheUsage
{
    /// <summary>How stale a file's last-write time must be before using it sets it again: the grain of the cache's LRU order.</summary>
    public static readonly TimeSpan TouchAfter = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, byte> _used = new(CacheFolder.PathComparer);

    public CacheUsage(DateTime started)
    {
        Started = started;
    }

    /// <summary>When the cook started, in UTC. Files written after it may belong to another cook running beside this one.</summary>
    public DateTime Started { get; }

    /// <summary>Notes that the cook used <paramref name="path"/>, last written at <paramref name="lastWriteUtc"/>, and marks it used now.</summary>
    public void Used(string path, DateTime lastWriteUtc)
    {
        _used.TryAdd(path, 0);
        if (Started - lastWriteUtc > TouchAfter)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, Started);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Gone, or held by another program. It was used either way, and a missed touch only makes it look older.
            }
        }
    }

    /// <summary>Notes that the cook wrote <paramref name="path"/>, which is new, so it needs no touch.</summary>
    public void Wrote(string path)
    {
        _used.TryAdd(path, 0);
    }

    public bool Contains(string path) => _used.ContainsKey(path);
}

/// <summary>
/// The cook cache's folder: <c>records</c>, <c>objects</c>, <c>steps/&lt;step&gt;</c> and <c>hashes.json</c>. Measures it
/// and trims it to a size, least recently used first, with one listing of the folder and the sizes and times it gives.
/// </summary>
public static class CacheFolder
{
    /// <summary>How old a temp file must be before trimming deletes it. Younger ones may be a write still going on.</summary>
    public static readonly TimeSpan StaleTemp = TimeSpan.FromHours(1);

    /// <summary>
    /// How old a file's last-write time may be and still have been used in the last <see cref="StaleTemp"/>: a use only
    /// sets it when it's more than <see cref="CacheUsage.TouchAfter"/> old. <c>super-cook cache --trim</c> keeps files this
    /// new, since a cook may be running and using them.
    /// </summary>
    public static readonly TimeSpan RecentlyUsed = CacheUsage.TouchAfter + StaleTemp;

    /// <summary>What <see cref="Parts"/> calls files in the folder that the cache didn't write. They're never deleted.</summary>
    public const string NotTheCaches = "not the cache's";

    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The parts trimming may delete from. Anything else, such as <c>hashes.json</c>, is counted but kept.</summary>
    private static readonly string[] Evictable = ["records", "objects", "steps"];

    /// <summary>
    /// The names of the files a cook cache writes, from its folder with <c>/</c> between folders: objects and step entries
    /// named by their hash in a folder named by its first two digits, records named by their key, the hash index, and the
    /// temp files each of them is written through. Clearing and trimming delete nothing else, so pointing them at the
    /// wrong folder can't delete its files.
    /// </summary>
    private static readonly Regex OursPattern = new(
        "^(objects/(?<hh>[0-9a-f]{2})/\\k<hh>[0-9a-f]{62}"
            + "|records/[0-9a-f]{64}\\.json"
            + "|steps/[A-Za-z0-9-]+/(?<sh>[0-9a-f]{2})/\\k<sh>[0-9a-f]{62}"
            + "|hashes\\.json|durations\\.json)"
            + "(\\.[0-9a-f]{32}\\.tmp)?$",
        RegexOptions.CultureInvariant);

    /// <summary>One file of the cache, as the listing found it.</summary>
    /// <param name="Ours">Whether its name is one the cache writes. Other files are counted apart and never deleted.</param>
    internal readonly record struct Entry(string Path, string Part, long Size, DateTime LastWriteUtc, bool IsTemp, bool Ours = true);

    /// <summary>Whether <paramref name="relative"/>, a path from the cache's folder, names a file the cache writes.</summary>
    internal static bool IsOurs(string relative) => OursPattern.IsMatch(relative.Replace('\\', '/'));

    /// <summary>
    /// Every file in the cache, with its part: <c>objects</c>, <c>records</c>, <c>steps/&lt;step&gt;</c>, <c>other</c> for
    /// files at the top such as <c>hashes.json</c>, or <see cref="NotTheCaches"/> for a file whose name the cache doesn't
    /// write. A cache that isn't there is empty.
    /// </summary>
    internal static List<Entry> List(string folder)
    {
        folder = Path.GetFullPath(folder);
        var entries = new List<Entry>();
        if (!Directory.Exists(folder))
        {
            return entries;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var listing = new FileSystemEnumerable<Entry>(folder, (ref FileSystemEntry entry) =>
        {
            string path = entry.ToFullPath();
            bool ours = IsOurs(Path.GetRelativePath(folder, path));
            return new Entry(path, ours ? PartOf(folder, path) : NotTheCaches, entry.Length, entry.LastWriteTimeUtc.UtcDateTime,
                entry.FileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase), ours);
        }, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

        try
        {
            entries.AddRange(listing);
        }
        catch (DirectoryNotFoundException)
        {
            // Deleted while it was being listed, such as by super-cook cache --clear.
        }

        return entries;
    }

    /// <summary>Total bytes per part, largest first, as <c>super-cook cache</c> shows them.</summary>
    public static List<(string Part, long Size, int Files)> Parts(string folder)
    {
        return [.. List(folder)
            .GroupBy(entry => entry.Part, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Sum(entry => entry.Size), group.Count()))
            .OrderByDescending(part => part.Item2)
            .ThenBy(part => part.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Trims the cache in <paramref name="folder"/> to <paramref name="limit"/> bytes now, outside a cook, as
    /// <c>super-cook cache --trim</c> does. Files whose last-write time is within <see cref="RecentlyUsed"/> are kept, which
    /// covers every file used in the last <see cref="StaleTemp"/>, since a cook may be running and using them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder holds cooked content, not a cache.</exception>
    public static CacheTrim Trim(string folder, long limit)
    {
        return TrimNow(folder, limit, DateTime.UtcNow);
    }

    /// <summary><see cref="Trim(string, long)"/> at <paramref name="now"/>, in UTC.</summary>
    internal static CacheTrim TrimNow(string folder, long limit, DateTime now)
    {
        return Trim(folder, limit, new CacheUsage(now - RecentlyUsed), now);
    }

    /// <summary>
    /// Deletes the least recently used files of <c>records</c>, <c>objects</c> and <c>steps</c> until the cache is no bigger
    /// than <paramref name="limit"/>, and temp files older than <see cref="StaleTemp"/>. Never deletes what
    /// <paramref name="usage"/> says this cook used, nor anything written since it started, which may be another cook's,
    /// nor a file whose name the cache doesn't write, which isn't counted in its size either.
    /// A file that vanishes first, or can't be deleted because it's open, is skipped. Deleting a record leaves its objects,
    /// and deleting an object leaves the records that name it: both are then a miss, never a wrong result.
    /// </summary>
    /// <param name="limit">The most bytes to keep, or 0 for no limit, which only clears stale temp files.</param>
    /// <param name="now">The time now, in UTC, which ages temp files.</param>
    /// <exception cref="InvalidOperationException">The folder holds cooked content, not a cache.</exception>
    internal static CacheTrim Trim(string folder, long limit, CacheUsage usage, DateTime now)
    {
        RefuseContent(folder);
        var entries = List(folder);
        DateTime started = usage.Started;
        long size = 0;
        long used = 0;
        int deleted = 0;
        long freed = 0;
        var candidates = new List<Entry>();
        foreach (var entry in entries)
        {
            if (!entry.Ours)
            {
                continue;
            }

            if (entry.IsTemp && now - entry.LastWriteUtc > StaleTemp)
            {
                if (TryDelete(entry.Path))
                {
                    deleted++;
                    freed += entry.Size;
                    continue;
                }
            }

            size += entry.Size;
            if (usage.Contains(entry.Path))
            {
                used += entry.Size;
            }
            else if (!entry.IsTemp && entry.LastWriteUtc < started && Array.IndexOf(Evictable, entry.Part.Split('/')[0]) >= 0)
            {
                candidates.Add(entry);
            }
        }

        if (limit > 0 && size > limit)
        {
            candidates.Sort(EvictionOrder);
            foreach (var entry in candidates)
            {
                if (size <= limit)
                {
                    break;
                }

                if (TryDelete(entry.Path))
                {
                    size -= entry.Size;
                    deleted++;
                    freed += entry.Size;
                }
            }
        }

        return new CacheTrim(size, limit, deleted, freed, used);
    }

    /// <summary>
    /// The order trimming deletes in: oldest first, and at the same time a record before the objects and steps it names,
    /// which are bigger to make again.
    /// </summary>
    internal static int EvictionOrder(Entry a, Entry b)
    {
        int byTime = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
        return byTime != 0 ? byTime : (a.Part == "records" ? 0 : 1).CompareTo(b.Part == "records" ? 0 : 1);
    }

    /// <summary>
    /// Deletes every file the cache writes: objects, records, step entries, <c>hashes.json</c> and their temp files, then
    /// the folders under <c>records</c>, <c>objects</c> and <c>steps</c> that are left empty. A file whose name the cache
    /// doesn't write is left, and so is its folder, in case it was pointed at a folder that holds more. Files in use are
    /// left too. Returns how many files and bytes it deleted.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder holds cooked content, not a cache.</exception>
    public static (int Files, long Bytes) Clear(string folder)
    {
        RefuseContent(folder);
        int files = 0;
        long bytes = 0;
        foreach (var entry in List(folder))
        {
            if (entry.Ours && TryDelete(entry.Path))
            {
                files++;
                bytes += entry.Size;
            }
        }

        string full = Path.GetFullPath(folder);
        foreach (string part in Evictable)
        {
            string top = Path.Combine(full, part);
            if (!Directory.Exists(top))
            {
                continue;
            }

            // Deepest first, so a folder whose folders were all empty is empty by the time it's tried.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            var folders = Directory.EnumerateDirectories(top, "*", options).OrderByDescending(path => path.Length).Append(top);
            foreach (string empty in folders)
            {
                try
                {
                    Directory.Delete(empty, recursive: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Not empty, gone already, or open in another program.
                }
            }
        }

        return (files, bytes);
    }

    /// <summary>Stops clearing or trimming a content folder, whose objects are named as the cache's are.</summary>
    private static void RefuseContent(string folder)
    {
        string full = Path.GetFullPath(folder);
        if (File.Exists(Path.Combine(full, ContentManifest.FileName)))
        {
            throw new InvalidOperationException(
                $"{full} holds cooked content, with a {ContentManifest.FileName}, not a cook cache, so it isn't cleared or trimmed. "
                + "Point --cache or SUPER_CACHE at the cache's own folder.");
        }
    }

    /// <summary>Deletes a file. True when it's gone, including when another cook got there first.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Open in another program, such as a game reading its hard-linked copy. It's tried again next time.
            return false;
        }
    }

    private static string PartOf(string folder, string path)
    {
        var relative = path.AsSpan(folder.Length).TrimStart(['/', '\\']);
        int first = relative.IndexOfAny('/', '\\');
        if (first < 0)
        {
            return "other";
        }

        var top = relative[..first];
        if (top.SequenceEqual("steps"))
        {
            var rest = relative[(first + 1)..];
            int second = rest.IndexOfAny('/', '\\');
            return second < 0 ? "other" : "steps/" + rest[..second].ToString();
        }

        return top.ToString();
    }
}
