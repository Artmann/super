namespace Super;

/// <summary>
/// Which of the view's reflection probes to ask the renderer to photograph this frame. Each key keeps the slot the
/// renderer gave it. A probe is asked for again when its request changes, and then as its
/// <see cref="ReflectionProbeRequest.Update"/> says: when the light on it changes, never, or again each time its last
/// picture is drawn. A key no longer asked for is forgotten alone, and the rest keep their pictures.
/// </summary>
internal sealed class ReflectionProbeSchedule
{
    private readonly Dictionary<int, (ReflectionProbeRequest Request, int Lighting, int Slot)> _asked = [];
    private readonly List<int> _forgotten = [];

    /// <summary>
    /// Whether to ask for a picture of <paramref name="request"/> now, lit as <paramref name="hash"/> says, while the
    /// renderer is or isn't still <paramref name="waiting"/> to draw the last one asked for. Asking changes nothing; call
    /// <see cref="Asked"/> once the renderer has it in line.
    /// </summary>
    public bool ShouldCapture(in ReflectionProbeRequest request, int hash, bool waiting)
    {
        if (!_asked.TryGetValue(request.Key, out var asked) || asked.Request != request)
        {
            return true;
        }

        return request.Update switch
        {
            ProbeUpdate.OnChange => asked.Lighting != hash,
            ProbeUpdate.Always => !waiting,
            _ => false,
        };
    }

    /// <summary>
    /// Records that the renderer has <paramref name="request"/> in line for a picture lit as <paramref name="hash"/>,
    /// into its <paramref name="slot"/>.
    /// </summary>
    public void Asked(in ReflectionProbeRequest request, int hash, int slot) => _asked[request.Key] = (request, hash, slot);

    /// <summary>The renderer's slot for a key's probe, or -1 when it has none.</summary>
    public int SlotOf(int key) => _asked.TryGetValue(key, out var asked) ? asked.Slot : -1;

    /// <summary>
    /// Forgets every probe that isn't among <paramref name="requests"/>, and gives their slots, so
    /// the caller can remove them from the renderer too. The list is reused by the next call.
    /// </summary>
    public IReadOnlyList<int> Forget(ReadOnlySpan<ReflectionProbeRequest> requests)
    {
        _forgotten.Clear();
        foreach (var (key, asked) in _asked)
        {
            bool wanted = false;
            foreach (var request in requests)
            {
                wanted |= request.Key == key;
            }

            if (!wanted)
            {
                _forgotten.Add(asked.Slot);
                _asked.Remove(key);
            }
        }

        return _forgotten;
    }
}
