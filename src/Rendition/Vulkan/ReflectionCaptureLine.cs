namespace Rendition.Vulkan;

/// <summary>
/// The reflection probes waiting for a capture, by cube slot, in the order they're captured: one a frame, from the
/// front. A probe is in line at most once.
/// </summary>
internal sealed class ReflectionCaptureLine
{
    private readonly List<(int Slot, ReflectionProbe Placement)> _waiting = [];

    /// <summary>Puts a probe at the back of the line, or when it's already waiting gives it the new placement in its place.</summary>
    public void Ask(int slot, in ReflectionProbe probe)
    {
        int at = IndexOf(slot);
        if (at >= 0)
        {
            _waiting[at] = (slot, probe);
        }
        else
        {
            _waiting.Add((slot, probe));
        }
    }

    /// <summary>Whether the probe in a slot is in line.</summary>
    public bool Holds(int slot) => IndexOf(slot) >= 0;

    /// <summary>Takes the probe at the front of the line, or returns false when none waits.</summary>
    public bool TakeNext(out int slot, out ReflectionProbe probe)
    {
        if (_waiting.Count == 0)
        {
            slot = -1;
            probe = default;
            return false;
        }

        (slot, probe) = _waiting[0];
        _waiting.RemoveAt(0);
        return true;
    }

    /// <summary>Puts a probe at the front of the line, unless it's already in line.</summary>
    public void PutFirst(int slot, in ReflectionProbe probe)
    {
        if (IndexOf(slot) < 0)
        {
            _waiting.Insert(0, (slot, probe));
        }
    }

    /// <summary>Takes the probe in a slot out of the line, if it's in it.</summary>
    public void Drop(int slot)
    {
        int at = IndexOf(slot);
        if (at >= 0)
        {
            _waiting.RemoveAt(at);
        }
    }

    public void Clear() => _waiting.Clear();

    private int IndexOf(int slot) => _waiting.FindIndex(waiting => waiting.Slot == slot);
}
