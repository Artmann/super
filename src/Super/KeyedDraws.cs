using System.Globalization;
using System.Runtime.InteropServices;

namespace Super;

/// <summary>
/// The things of one kind placed this frame, such as decals, each under its own key, up to a limit. <see cref="Check"/>
/// refuses a full frame or a key placed before, with a message naming the kind.
/// </summary>
internal sealed class KeyedDraws<T>(int most, string kind, string kinds)
{
    private readonly List<T> _items = [];
    private readonly HashSet<int> _keys = [];

    public ReadOnlySpan<T> Items => CollectionsMarshal.AsSpan(_items);

    /// <summary>Refuses a key when the frame is full or the key was placed already.</summary>
    public void Check(int key)
    {
        if (_items.Count == most)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"At most {most} {kinds} fit in a frame."));
        }

        if (_keys.Contains(key))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{char.ToUpperInvariant(kind[0])}{kind[1..]} {key} was already placed this frame. Give each {kind} its own key."), nameof(key));
        }
    }

    /// <summary>Places an item that passed <see cref="Check"/>.</summary>
    public void Add(int key, T item)
    {
        _keys.Add(key);
        _items.Add(item);
    }

    public void Clear()
    {
        _items.Clear();
        _keys.Clear();
    }
}
