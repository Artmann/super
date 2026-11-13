using System.Numerics;

namespace Rendition;

/// <summary>
/// An area a frame names whose bake is missing or stale, so the renderer may bake it in the background.
/// </summary>
/// <param name="File">Where its bake goes; copies of a chunk share it.</param>
/// <param name="Name">The space's or chunk's name.</param>
/// <param name="Inputs">What the frame says it is made from.</param>
/// <param name="Chunk">The chunk's index in the frame, or -1 for the space.</param>
/// <param name="Centre">The middle of its box in the world.</param>
internal readonly record struct BakeWant(string File, string Name, BakeInputs Inputs, int Chunk, Vector3 Centre);

/// <summary>
/// Picks the next area to bake in the background from each frame's wants, one at a time. Every area gets its first
/// quality before any area gets its next; then the nearest to the camera goes first, ties by file name. A running bake
/// carries on while its area isn't named, and is cancelled when its file is named with other inputs. A file, inputs and
/// quality once finished is never started again, so a saved bake the store hasn't read yet, or one still stale, doesn't
/// loop. Keeps its lists from frame to frame, so a frame allocates nothing once it has seen as many areas before. See
/// "Background bakes" in docs/design.md.
/// </summary>
internal sealed class BakeQueue
{
    private readonly BakeQuality[] _qualities;
    private readonly HashSet<(string File, ulong Inputs, BakeQuality Quality)> _finished = [];

    // This frame's wants, one per file: the copy nearest the camera.
    private readonly List<BakeWant> _areas = [];
    private readonly Dictionary<string, int> _byFile = new(StringComparer.Ordinal);

    private (string File, BakeInputs Inputs)? _running;

    /// <exception cref="ArgumentException"><paramref name="qualities"/> is empty.</exception>
    public BakeQueue(IReadOnlyList<BakeQuality> qualities)
    {
        ArgumentNullException.ThrowIfNull(qualities);
        if (qualities.Count == 0)
        {
            throw new ArgumentException("A background bake needs at least one quality to bake at.", nameof(qualities));
        }

        _qualities = [.. qualities];
    }

    /// <summary>How many areas the last frame named, besides the running one, that still wait for a bake.</summary>
    public int Waiting { get; private set; }

    /// <summary>Whether a bake was started and has neither finished nor been stopped.</summary>
    public bool Running => _running is not null;

    /// <summary>
    /// Takes this frame's wants. Returns what to start, at <paramref name="quality"/>, or null to carry on with the
    /// running bake or to rest. <paramref name="cancelRunning"/> is true when the running bake's file is named with
    /// other inputs, so its bake would be stale: drop it, and start what this returns, if anything.
    /// </summary>
    public BakeWant? Update(ReadOnlySpan<BakeWant> wants, Vector3 camera, out bool cancelRunning, out BakeQuality quality)
    {
        quality = _qualities[0];
        KeepNearest(wants, camera);
        cancelRunning = CancelStaleRunning();
        int best = PickNext(camera, out int bestQuality, out int waiting);
        if (_running is not null || best < 0)
        {
            Waiting = waiting;
            return null;
        }

        Waiting = waiting - 1;
        var start = _areas[best];
        _running = (start.File, start.Inputs);
        quality = _qualities[bestQuality];
        return start;
    }

    // Fills _areas with one want per file: the copy nearest the camera.
    private void KeepNearest(ReadOnlySpan<BakeWant> wants, Vector3 camera)
    {
        _areas.Clear();
        _byFile.Clear();
        foreach (ref readonly var want in wants)
        {
            if (!_byFile.TryGetValue(want.File, out int index))
            {
                _byFile.Add(want.File, _areas.Count);
                _areas.Add(want);
            }
            else if (Vector3.DistanceSquared(want.Centre, camera) < Vector3.DistanceSquared(_areas[index].Centre, camera))
            {
                _areas[index] = want;
            }
        }
    }

    // Forgets the running bake when this frame names its file with other inputs, so its bake would be stale.
    private bool CancelStaleRunning()
    {
        if (_running is { } running && _byFile.TryGetValue(running.File, out int named)
            && _areas[named].Inputs != running.Inputs)
        {
            _running = null;
            return true;
        }

        return false;
    }

    // The index in _areas of the area to start next, or -1 when none waits: the lowest next quality, then the nearest
    // to the camera, then the first file name. Counts the areas that wait, besides the running one.
    private int PickNext(Vector3 camera, out int bestQuality, out int waiting)
    {
        int best = -1;
        Candidate bestCandidate = default;
        waiting = 0;
        for (int i = 0; i < _areas.Count; i++)
        {
            var area = _areas[i];
            int next = IsRunning(area.File) ? -1 : NextQuality(area.File, area.Inputs);
            if (next < 0)
            {
                continue;
            }

            waiting++;
            var candidate = new Candidate(next, Vector3.DistanceSquared(area.Centre, camera), area.File);
            if (best < 0 || candidate.GoesBefore(bestCandidate))
            {
                best = i;
                bestCandidate = candidate;
            }
        }

        bestQuality = best < 0 ? int.MaxValue : bestCandidate.Quality;
        return best;
    }

    private bool IsRunning(string file) =>
        _running is { } running && string.Equals(running.File, file, StringComparison.Ordinal);

    // An area that waits, by what orders it: its next quality's index, its squared distance to the camera, its file.
    private readonly record struct Candidate(int Quality, float Distance, string File)
    {
        public bool GoesBefore(Candidate other)
        {
            if (Quality != other.Quality)
            {
                return Quality < other.Quality;
            }

            if (Distance != other.Distance)
            {
                return Distance < other.Distance;
            }

            return string.CompareOrdinal(File, other.File) < 0;
        }
    }

    /// <summary>
    /// Records that <paramref name="file"/> was baked from <paramref name="inputs"/> at <paramref name="quality"/>, or
    /// failed to, so it is never started again. Ends the running bake when it is this one.
    /// </summary>
    public void Finished(string file, BakeInputs inputs, BakeQuality quality)
    {
        _finished.Add((file, inputs.Combined, quality));
        if (_running is { } running && string.Equals(running.File, file, StringComparison.Ordinal) && running.Inputs == inputs)
        {
            _running = null;
        }
    }

    /// <summary>Ends the running bake without finishing it, so its area is started again while a frame names it.</summary>
    public void Stopped() => _running = null;

    /// <summary>
    /// Tells the queue a frame names <paramref name="file"/> made from <paramref name="inputs"/>, wanted or not. True
    /// when that is the running bake's file with other inputs, so its bake would be stale and would overwrite a file
    /// that may already be fresh: the queue forgets it, and the caller drops it.
    /// </summary>
    public bool Named(string file, BakeInputs inputs)
    {
        if (_running is { } running && string.Equals(running.File, file, StringComparison.Ordinal)
            && running.Inputs != inputs)
        {
            _running = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether an area a frame names wants a bake: its saved bake in <paramref name="store"/> is missing or stale, or
    /// fresh at a quality before the last of the queue's, or fresh while this queue has baked some of its qualities but
    /// not all. A fresh saved bake counts as finished at its own quality and every one before, so the next to start is
    /// the one after it, and a game that saved its preview and restarted still gets its final.
    /// </summary>
    public bool Wants(IBakeStore store, string file, BakeInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.IsFreshQuietly(file, inputs))
        {
            return true;
        }

        if (store.SavedQuality(file) is not { } saved)
        {
            return Unfinished(file, inputs);
        }

        ulong combined = inputs.Combined;
        foreach (var quality in _qualities)
        {
            if (quality <= saved)
            {
                _finished.Add((file, combined, quality));
            }
        }

        return NextQuality(file, inputs) >= 0;
    }

    /// <summary>
    /// Whether some of <paramref name="file"/>'s qualities are finished from <paramref name="inputs"/> but not all, so
    /// an area whose saved preview made it fresh still wants its final.
    /// </summary>
    public bool Unfinished(string file, BakeInputs inputs)
    {
        ulong combined = inputs.Combined;
        bool any = false;
        bool all = true;
        foreach (var quality in _qualities)
        {
            bool done = _finished.Contains((file, combined, quality));
            any |= done;
            all &= done;
        }

        return any && !all;
    }

    // The index of the first quality not yet finished for this file and inputs, or -1 when every one is.
    private int NextQuality(string file, BakeInputs inputs)
    {
        ulong combined = inputs.Combined;
        for (int q = 0; q < _qualities.Length; q++)
        {
            if (!_finished.Contains((file, combined, _qualities[q])))
            {
                return q;
            }
        }

        return -1;
    }
}
