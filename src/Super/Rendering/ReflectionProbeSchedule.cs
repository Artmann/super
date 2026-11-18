namespace Super;

/// <summary>
/// Which of the view's reflection probes to photograph this frame. Each key keeps the slot the
/// renderer gave it, and is captured again only when its request or the lighting differs from its
/// last capture. A key no longer asked for is forgotten alone, and the rest keep their pictures.
/// </summary>
internal sealed class ReflectionProbeSchedule
{
    private readonly Dictionary<int, (ReflectionProbeRequest Request, int Lighting, int Slot)> _captured = [];
    private readonly List<int> _forgotten = [];

    /// <summary>
    /// Whether <paramref name="request"/> must be photographed now, lit as <paramref name="hash"/>
    /// says: it never was, or it or the lighting changed since. Asking changes nothing; call
    /// <see cref="Captured"/> once the renderer has taken the picture.
    /// </summary>
    public bool ShouldCapture(in ReflectionProbeRequest request, int hash) =>
        !_captured.TryGetValue(request.Key, out var captured) || captured.Request != request || captured.Lighting != hash;

    /// <summary>
    /// Records that <paramref name="request"/> was photographed lit as <paramref name="hash"/>,
    /// into the renderer's <paramref name="slot"/>.
    /// </summary>
    public void Captured(in ReflectionProbeRequest request, int hash, int slot) => _captured[request.Key] = (request, hash, slot);

    /// <summary>The renderer's slot for a key's probe, or -1 when it has none.</summary>
    public int SlotOf(int key) => _captured.TryGetValue(key, out var captured) ? captured.Slot : -1;

    /// <summary>
    /// Forgets every probe that isn't among <paramref name="requests"/>, and gives their slots, so
    /// the caller can remove them from the renderer too. The list is reused by the next call.
    /// </summary>
    public IReadOnlyList<int> Forget(ReadOnlySpan<ReflectionProbeRequest> requests)
    {
        _forgotten.Clear();
        foreach (var (key, captured) in _captured)
        {
            bool asked = false;
            foreach (var request in requests)
            {
                asked |= request.Key == key;
            }

            if (!asked)
            {
                _forgotten.Add(captured.Slot);
                _captured.Remove(key);
            }
        }

        return _forgotten;
    }
}
