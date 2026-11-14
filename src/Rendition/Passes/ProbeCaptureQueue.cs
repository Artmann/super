using System.Numerics;

namespace Rendition;

/// <summary>
/// Every capture <see cref="ProbeScheduler"/> has been told of, in the order they were made, so the clean probes
/// updated longest ago are at the front and finding them reads only as many as are wanted. A capture stands while
/// its probe is still clean and hasn't been captured again since. One that no longer stands is skipped when it is
/// met, and dropped when the queue is packed down. The queue reads the scheduler's own state, update frames and
/// positions, in the arrays it is given.
/// </summary>
internal sealed class ProbeCaptureQueue(byte[]?[] state, long[]?[] updated, Vector3[]?[] position, byte clean)
{
    private Capture[] _captures = [];
    private int _start;
    private int _end;

    // The oldest clean probes of one TakeOldest, before they are sorted.
    private ProbeCandidate[] _oldest = [];

    /// <summary>
    /// Fills <paramref name="into"/> with the clean probes updated longest ago, as many as it holds or there are, and
    /// returns how many. Whole frames of captures are read from the front until there are enough, and the last frame
    /// read is what the distance has to choose from. <paramref name="now"/> is the frame ages are counted from.
    /// </summary>
    public int TakeOldest(Span<ProbeCandidate> into, long now, Vector3 camera, Vector3 forward)
    {
        while (_start < _end && !Stands(_captures[_start]))
        {
            _start++;
        }

        if (into.IsEmpty)
        {
            return 0;
        }

        int found = ReadOldest(into.Length, now, camera, forward);
        var oldest = _oldest.AsSpan(0, found);
        oldest.Sort();

        // A probe captured, scrolled away and captured again in one frame has two captures that stand, side by side
        // once sorted.
        int taken = 0;
        for (int i = 0; i < found && taken < into.Length; i++)
        {
            if (i > 0 && oldest[i].Group == oldest[i - 1].Group && oldest[i].Slot == oldest[i - 1].Slot)
            {
                continue;
            }

            into[taken++] = oldest[i];
        }

        return taken;
    }

    // Reads whole frames of standing captures from the front into _oldest until there are `room` or more, and returns
    // how many.
    private int ReadOldest(int room, long now, Vector3 camera, Vector3 forward)
    {
        int found = 0;
        long frame = -1;
        for (int i = _start; i < _end; i++)
        {
            var capture = _captures[i];
            if (!Stands(capture))
            {
                continue;
            }

            if (found >= room && capture.Frame != frame)
            {
                break;
            }

            frame = capture.Frame;
            if (found == _oldest.Length)
            {
                Array.Resize(ref _oldest, Math.Max(found * 2, 256));
            }

            float rank = ProbeCandidate.Rank(position[capture.Group]![capture.Slot], capture.Group, camera, forward);
            _oldest[found++] = new ProbeCandidate(clean, now - capture.Frame, rank, capture.Group, capture.Slot);
        }

        return found;
    }

    private bool Stands(in Capture capture)
    {
        return state[capture.Group]![capture.Slot] == clean && updated[capture.Group]![capture.Slot] == capture.Frame;
    }

    /// <summary>
    /// Adds a capture to the end, first dropping the ones that no longer stand if the array is full. Given room for
    /// twice the probes it only grows when one frame captures the same probes again and again with a scroll between,
    /// since otherwise one capture stands for each probe.
    /// </summary>
    public void Record(long frame, int group, int slot)
    {
        if (_end == _captures.Length)
        {
            int kept = 0;
            for (int i = _start; i < _end; i++)
            {
                if (Stands(_captures[i]))
                {
                    _captures[kept++] = _captures[i];
                }
            }

            _start = 0;
            _end = kept;
            if (kept == _captures.Length)
            {
                Array.Resize(ref _captures, Math.Max(kept * 2, 16));
            }
        }

        _captures[_end++] = new Capture(frame, group, slot);
    }

    /// <summary>Drops every capture of a group, whose probes are being replaced.</summary>
    public void Forget(int group)
    {
        int kept = 0;
        for (int i = _start; i < _end; i++)
        {
            if (_captures[i].Group != group)
            {
                _captures[kept++] = _captures[i];
            }
        }

        _start = 0;
        _end = kept;
    }

    /// <summary>Makes room for <paramref name="count"/> captures, so recording them allocates nothing.</summary>
    public void Reserve(int count)
    {
        if (_captures.Length < count)
        {
            Array.Resize(ref _captures, count);
        }
    }

    private readonly record struct Capture(long Frame, int Group, int Slot);
}
