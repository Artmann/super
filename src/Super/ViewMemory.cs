using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Super;

/// <summary>
/// Names one remembered thing: a key, and the place in the code that asked. The compiler
/// gives each place one file string, so comparing the reference is enough and costs nothing.
/// </summary>
internal readonly struct MemorySite(int key, string file, int line) : IEquatable<MemorySite>
{
    private readonly int _key = key;
    private readonly string _file = file;
    private readonly int _line = line;

    public bool Equals(MemorySite other) =>
        _key == other._key && _line == other._line && ReferenceEquals(_file, other._file);

    public override bool Equals(object? other) => other is MemorySite site && Equals(site);

    public override int GetHashCode() => HashCode.Combine(_key, _line, RuntimeHelpers.GetHashCode(_file));
}

/// <summary>Lets a frame sweep every kind of memory without knowing what each one holds.</summary>
internal abstract class MemorySlots
{
    /// <summary>Drops everything that wasn't touched in this frame.</summary>
    public abstract void Forget(long frame);
}

/// <summary>One kind of memory, kept by place and key.</summary>
internal sealed class MemorySlots<T> : MemorySlots
{
    private readonly Dictionary<MemorySite, (T Value, long Frame)> _slots = [];

    /// <summary>The slot for a site, marked as used in this frame. It's only good until the next call.</summary>
    public ref T Touch(in MemorySite site, long frame, out bool isNew)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, site, out bool exists);
        isNew = !exists;
        slot.Frame = frame;
        return ref slot.Value;
    }

    public override void Forget(long frame)
    {
        foreach (var (site, slot) in _slots)
        {
            // Removing while going through a Dictionary is allowed.
            if (slot.Frame != frame)
            {
                _slots.Remove(site);
            }
        }
    }
}

/// <summary>Where an animated value is headed, and since when.</summary>
internal record struct ViewMotion(float From, float To, double Start, float Seconds)
{
    public readonly float At(double now)
    {
        if (Seconds <= 0f)
        {
            return To;
        }

        float done = Math.Clamp((float)((now - Start) / Seconds), 0f, 1f);
        return float.Lerp(From, To, done * done * (3f - 2f * done));
    }
}

/// <summary>What a <see cref="View.Cached"/> call drew at one version.</summary>
internal sealed class ViewRecording
{
    public long Version { get; set; }

    public List<ModelDraw> Models { get; } = [];
}

/// <summary>What a <see cref="View"/> keeps from one frame to the next.</summary>
internal sealed class ViewMemory
{
    private readonly Dictionary<Type, MemorySlots> _kinds = [];

    public MemorySlots<ViewMotion> Motions { get; }

    public MemorySlots<ViewRecording> Recordings { get; }

    public ViewMemory()
    {
        Motions = Kind<ViewMotion>();
        Recordings = Kind<ViewRecording>();
    }

    /// <summary>The slots for one kind of value. Each kind gets one, so nothing has to be boxed.</summary>
    public MemorySlots<T> Kind<T>()
    {
        if (_kinds.TryGetValue(typeof(T), out var known))
        {
            return (MemorySlots<T>)known;
        }

        var added = new MemorySlots<T>();
        _kinds[typeof(T)] = added;
        return added;
    }

    public void Forget(long frame)
    {
        foreach (var kind in _kinds.Values)
        {
            kind.Forget(frame);
        }
    }
}
