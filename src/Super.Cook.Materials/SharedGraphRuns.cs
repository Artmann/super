using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Super.Cook.Materials;

/// <summary>
/// Graph runs shared by every source of a cook, and the cooks after it in the same process, so a graph that two models
/// and a material of its own all run at one size runs once. Two sources that want the same run at once wait for one of
/// them to make it. A run is keyed by everything it depends on but the images the graph reads; those are checked against
/// the hashes the run kept before it's shared, so a run made from other images is never handed out.
/// </summary>
internal static class SharedGraphRuns
{
    /// <summary>One graph's images, packed, with what it read and what the material checks found in it.</summary>
    /// <param name="Reads">Each image the graph read, by its path from the graph's or variant's folder, and the SHA-256 of its bytes.</param>
    internal sealed record Run(PackedImages Packed, IReadOnlyList<(string Path, byte[] Hash)> Reads, MaterialChecks.MaterialFindings Findings);

    /// <summary>
    /// How many bytes of runs are kept for later sources. Past it, every kept run is dropped. A run of more than an eighth
    /// of it, such as one at 4096 pixels, isn't kept after it's made, though the sources waiting for it then still get it.
    /// </summary>
    internal const long MaxSharedBytes = 1L << 30;

    private static readonly ConcurrentDictionary<string, Slot> Slots = new(StringComparer.Ordinal);

    private static long _sharedBytes;

    private static int _made;

    /// <summary>Whether runs are shared at all. Tests turn it off to check that sharing changes no byte.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>How many graph runs have been made in this process, shared or not. For tests.</summary>
    internal static int Made => Volatile.Read(ref _made);

    /// <summary>Drops every kept run. For tests.</summary>
    internal static void Forget()
    {
        Slots.Clear();
        Interlocked.Exchange(ref _sharedBytes, 0);
    }

    internal static void CountRun() => Interlocked.Increment(ref _made);

    /// <summary>
    /// The run kept under <paramref name="key"/>, or the one another source is making, or else what <paramref name="make"/>
    /// makes. Only a run that worked is shared. When the shared one failed, or read images that differ from the ones
    /// <paramref name="input"/> has, <paramref name="make"/> runs again, so each source reports its own errors.
    /// </summary>
    /// <param name="folder">The run file's folder from the source's folder, where the images it read are.</param>
    public static Run? GetOrMake(string? key, CookInput input, string folder, Func<Run?> make)
    {
        if (key is null || !Enabled)
        {
            return make();
        }

        var mine = new Slot();
        var slot = Slots.GetOrAdd(key, mine);
        if (!ReferenceEquals(slot, mine))
        {
            Run? shared;
            using (input.Time("wait for shared graph runs"))
            {
                shared = slot.Result.Task.GetAwaiter().GetResult();
            }

            return shared is not null && SameImages(input, folder, shared) ? shared : make();
        }

        Run? made = null;
        try
        {
            made = make();
        }
        finally
        {
            if (made is null || !Keep(made))
            {
                Slots.TryRemove(new KeyValuePair<string, Slot>(key, mine));
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

    /// <returns>Whether the run is kept for later sources.</returns>
    private static bool Keep(Run run)
    {
        var pixels = run.Packed.Pixels;
        long bytes = (long)pixels.Color.Length + pixels.Normal.Length + pixels.Surface.Length + pixels.Height.Length;
        if (bytes > MaxSharedBytes / 8)
        {
            return false;
        }

        if (Interlocked.Add(ref _sharedBytes, bytes) > MaxSharedBytes)
        {
            Slots.Clear();
            Interlocked.Exchange(ref _sharedBytes, 0);
            return false;
        }

        return true;
    }

    private sealed class Slot
    {
        public TaskCompletionSource<Run?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
