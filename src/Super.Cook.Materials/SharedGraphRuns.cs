using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Super.Cook.Materials;

/// <summary>
/// Graph runs shared by every source of a cook, and the cooks after it in the same process, so a graph that two models
/// and a material of its own all run at one size runs once. Two sources that want the same run at once wait for one of
/// them to make it. A run is keyed by everything it depends on but the images the graph reads; those are checked against
/// the hashes the run kept before it's shared, so a run made from other images is never handed out. A source that finds
/// one made from other images, as after an image was edited, runs the graph itself and its run takes the old one's place.
/// </summary>
internal static class SharedGraphRuns
{
    /// <summary>One graph's images, packed, with what it read and what the material checks found in it.</summary>
    /// <param name="Reads">Each image the graph read, by its path from the graph's or variant's folder, and the SHA-256 of its bytes.</param>
    internal sealed record Run(PackedImages Packed, IReadOnlyList<(string Path, byte[] Hash)> Reads, MaterialChecks.MaterialFindings Findings);

    /// <summary>The bytes of runs kept for later sources when nothing sets <see cref="MaxSharedBytes"/>.</summary>
    internal const long DefaultMaxSharedBytes = 1L << 30;

    private static readonly ConcurrentDictionary<string, Slot> Slots = new(StringComparer.Ordinal);

    // Guards the bytes kept, which slots count in them, and replacing or dropping a slot.
    private static readonly Lock Gate = new();

    private static long _sharedBytes;

    private static long _keptCount;

    private static int _made;

    /// <summary>
    /// How many bytes of runs are kept for later sources. Past it, the runs kept longest are dropped until the rest fit;
    /// a run still being made is never dropped, since sources wait for it. A run of more than an eighth of it, such as one
    /// at 4096 pixels, isn't kept after it's made, though the sources waiting for it then still get it. Tests set it lower.
    /// </summary>
    internal static long MaxSharedBytes { get; set; } = DefaultMaxSharedBytes;

    /// <summary>Whether runs are shared at all. Tests turn it off to check that sharing changes no byte.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>How many graph runs have been made in this process, shared or not. For tests.</summary>
    internal static int Made => Volatile.Read(ref _made);

    /// <summary>How many bytes of runs are kept now. For tests.</summary>
    internal static long SharedBytes
    {
        get
        {
            lock (Gate)
            {
                return _sharedBytes;
            }
        }
    }

    /// <summary>Drops every kept run. For tests.</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            Slots.Clear();
            _sharedBytes = 0;
        }
    }

    internal static void CountRun() => Interlocked.Increment(ref _made);

    /// <summary>
    /// The run kept under <paramref name="key"/>, or the one another source is making, or else what <paramref name="make"/>
    /// makes. Only a run that worked is shared. When the shared one failed, <paramref name="make"/> runs again, so each
    /// source reports its own errors. When it read images that differ from the ones <paramref name="input"/> has,
    /// <paramref name="make"/> runs again and its run is kept in the old one's place.
    /// </summary>
    /// <param name="folder">The run file's folder from the source's folder, where the images it read are.</param>
    public static Run? GetOrMake(string? key, CookInput input, string folder, Func<Run?> make)
    {
        if (key is null || !Enabled)
        {
            return make();
        }

        var mine = new Slot();
        while (true)
        {
            var slot = Slots.GetOrAdd(key, mine);
            if (ReferenceEquals(slot, mine))
            {
                break;
            }

            Run? shared;
            using (input.Time("wait for shared graph runs"))
            {
                shared = slot.Result.Task.GetAwaiter().GetResult();
            }

            if (shared is null)
            {
                return make();
            }

            if (SameImages(input, folder, shared))
            {
                return shared;
            }

            // Made from other images. Unless another source has replaced it already, this source's run takes its place;
            // if one has, its run is checked in turn.
            if (Replace(key, slot, mine))
            {
                break;
            }
        }

        Run? made = null;
        try
        {
            made = make();
        }
        finally
        {
            if (made is null || !Keep(key, mine, made))
            {
                Drop(key, mine);
            }

            mine.Result.SetResult(made);
        }

        return made;
    }

    /// <summary>Whether every image <paramref name="run"/> read is the same for <paramref name="input"/>. Reading them tracks them, as running the graph would.</summary>
    private static bool SameImages(CookInput input, string folder, Run run)
    {
        foreach (var (path, hash) in run.Reads)
        {
            try
            {
                if (!SHA256.HashData(input.Read(folder + path)).AsSpan().SequenceEqual(hash))
                {
                    return false;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Replace(string key, Slot stale, Slot mine)
    {
        lock (Gate)
        {
            if (!Slots.TryUpdate(key, mine, stale))
            {
                return false;
            }

            Uncount(stale);
            return true;
        }
    }

    private static void Drop(string key, Slot slot)
    {
        lock (Gate)
        {
            if (Slots.TryRemove(new KeyValuePair<string, Slot>(key, slot)))
            {
                Uncount(slot);
            }
        }
    }

    // Under the gate.
    private static void Uncount(Slot slot)
    {
        _sharedBytes -= slot.Bytes;
        slot.Bytes = 0;
    }

    /// <returns>Whether the run is kept for later sources.</returns>
    private static bool Keep(string key, Slot mine, Run run)
    {
        var pixels = run.Packed.Pixels;
        long bytes = (long)pixels.Color.Length + pixels.Normal.Length + pixels.Surface.Length + pixels.Height.Length;
        lock (Gate)
        {
            long max = MaxSharedBytes;
            // A slot forgotten or replaced while its run was made isn't kept.
            if (bytes > max / 8 || !Slots.TryGetValue(key, out var current) || !ReferenceEquals(current, mine))
            {
                return false;
            }

            mine.Bytes = bytes;
            mine.Kept = ++_keptCount;
            _sharedBytes += bytes;
            if (_sharedBytes <= max)
            {
                return true;
            }

            // The runs kept longest go first. One still being made isn't counted yet, so it's never dropped.
            var finished = Slots
                .Where(pair => !ReferenceEquals(pair.Value, mine) && pair.Value.Bytes > 0 && pair.Value.Result.Task.IsCompleted)
                .OrderBy(pair => pair.Value.Kept)
                .ToList();
            foreach (var pair in finished)
            {
                if (_sharedBytes <= max)
                {
                    break;
                }

                if (Slots.TryRemove(pair))
                {
                    Uncount(pair.Value);
                }
            }

            return true;
        }
    }

    private sealed class Slot
    {
        public TaskCompletionSource<Run?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The bytes this slot's run counts in the kept total, or 0 when it isn't kept. Under the gate.</summary>
        public long Bytes { get; set; }

        /// <summary>When it was kept, as a count of runs kept before it. Under the gate.</summary>
        public long Kept { get; set; }
    }
}
