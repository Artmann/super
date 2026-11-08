namespace Super;

/// <summary>
/// Which of the view's reflection probes to photograph this frame. Each key keeps the slot it
/// was first captured into, and is captured again only when its request or the lighting differs
/// from its last capture. The renderer can't drop one probe, so when a key is no longer asked
/// for, every probe is forgotten and the rest are captured again.
/// </summary>
internal sealed class ReflectionProbeSchedule
{
    private readonly List<(ReflectionProbeRequest Request, int Lighting)> _captured = [];

    /// <summary>
    /// Whether <paramref name="request"/> must be photographed now, lit as <paramref name="hash"/>
    /// says. Answering yes counts it as captured, so the caller must then capture it.
    /// </summary>
    public bool ShouldCapture(in ReflectionProbeRequest request, int hash)
    {
        int slot = SlotOf(request.Key);
        if (slot < 0)
        {
            _captured.Add((request, hash));
            return true;
        }

        if (_captured[slot] == (request, hash))
        {
            return false;
        }

        _captured[slot] = (request, hash);
        return true;
    }

    /// <summary>The renderer's slot for a key's probe, or -1 when it has none.</summary>
    public int SlotOf(int key)
    {
        for (int i = 0; i < _captured.Count; i++)
        {
            if (_captured[i].Request.Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Forgets every probe when one of them isn't among <paramref name="requests"/>, and says
    /// whether it did, so the caller can clear the renderer's probes too.
    /// </summary>
    public bool Forget(ReadOnlySpan<ReflectionProbeRequest> requests)
    {
        foreach (var (captured, _) in _captured)
        {
            bool asked = false;
            foreach (var request in requests)
            {
                asked |= request.Key == captured.Key;
            }

            if (!asked)
            {
                _captured.Clear();
                return true;
            }
        }

        return false;
    }
}
