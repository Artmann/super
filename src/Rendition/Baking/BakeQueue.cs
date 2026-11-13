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
        cancelRunning = false;
        quality = _qualities[0];
        _areas.Clear();
        _byFile.Clear();
        foreach (ref readonly var want in wants)
        {
            if (_byFile.TryGetValue(want.File, out int index))
            {
                if (Vector3.DistanceSquared(want.Centre, camera) < Vector3.DistanceSquared(_areas[index].Centre, camera))
                {
                    _areas[index] = want;
                }
            }
            else
            {
                _byFile.Add(want.File, _areas.Count);
                _areas.Add(want);
            }
        }

        if (_running is { } running && _byFile.TryGetValue(running.File, out int named)
            && _areas[named].Inputs != running.Inputs)
        {
            cancelRunning = true;
            _running = null;
        }

        int best = -1;
        int bestQuality = int.MaxValue;
        float bestDistance = float.MaxValue;
        int waiting = 0;
        for (int i = 0; i < _areas.Count; i++)
        {
            var area = _areas[i];
            if (_running is { } current && string.Equals(current.File, area.File, StringComparison.Ordinal))
            {
                continue;
            }

            int next = NextQuality(area.File, area.Inputs);
            if (next < 0)
            {
                continue;
            }

            waiting++;
            float distance = Vector3.DistanceSquared(area.Centre, camera);
            if (next < bestQuality
                || (next == bestQuality && distance < bestDistance)
                || (next == bestQuality && distance == bestDistance
                    && string.CompareOrdinal(area.File, _areas[best].File) < 0))
            {
                best = i;
                bestQuality = next;
                bestDistance = distance;
            }
        }

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
