using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// The probes that aren't clean, each once, in the order they were listed, so a frame looks at them and not at every
/// probe. A probe stays listed after it is captured until <see cref="DropCaptured"/>, which the next frame calls. It
/// reads the probes' states in the arrays it is given, where <paramref name="clean"/> means clean.
/// </summary>
internal sealed class ProbeWaitList(byte[]?[] state, byte clean)
{
    private readonly List<ProbeId> _ids = [];

    // Per group and slot, whether the probe is in _ids.
    private readonly bool[]?[] _listed = new bool[]?[state.Length];

    /// <summary>Lists a probe, unless it is listed already.</summary>
    public void Enlist(int group, int slot)
    {
        var listed = _listed[group]!;
        if (!listed[slot])
        {
            listed[slot] = true;
            _ids.Add(new ProbeId(group, slot));
        }
    }

    /// <summary>Drops a group's probes, which are being replaced by <paramref name="count"/> new ones, none listed yet.</summary>
    public void DropGroup(int group, int count)
    {
        _ids.RemoveAll(id => id.Cascade == group);
        _listed[group] = new bool[count];
    }

    /// <summary>Makes room to list <paramref name="probes"/> probes without growing.</summary>
    public void ReserveFor(int probes) => _ids.EnsureCapacity(probes);

    /// <summary>Drops the probes that have been captured since they were listed, and returns the rest in order.</summary>
    public ReadOnlySpan<ProbeId> DropCaptured()
    {
        int kept = 0;
        for (int i = 0; i < _ids.Count; i++)
        {
            var id = _ids[i];
            if (state[id.Cascade]![id.Slot] == clean)
            {
                _listed[id.Cascade]![id.Slot] = false;
                continue;
            }

            _ids[kept++] = id;
        }

        _ids.RemoveRange(kept, _ids.Count - kept);
        return CollectionsMarshal.AsSpan(_ids);
    }
}
