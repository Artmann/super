using System.Runtime.InteropServices;

namespace Super;

/// <summary>
/// Which of <see cref="ContentItems"/>' slots were let go, which the window still has to free, and which can be taken
/// again. Models, textures and interface images can be let go and named again all the time, as a game that streams
/// meshes made in code does, so their slots are taken again once the window has freed what it made of them. Each slot
/// counts how many times it was given back, and their refs carry that count, so a ref kept from before still says it
/// was unloaded instead of naming what took its place. Other kinds keep their slot for good once let go.
/// </summary>
internal sealed class ItemSlots
{
    // For each slot: how many times it was given back, and whether it may be taken again once it is.
    private readonly List<int> _generations = [];
    private readonly List<bool> _reusable = [];

    private readonly HashSet<int> _unloaded = [];
    private readonly List<int> _pendingUnloads = [];
    private readonly Stack<int> _free = [];
    private readonly List<int> _retaken = [];

    /// <summary>
    /// How many slots the window has started loading, from the first. A slot past them can be given back at once,
    /// since the window made nothing of it yet. Without a window it stays 0.
    /// </summary>
    public int Seen { get; set; }

    /// <summary>The index and generation a reusable item named next takes.</summary>
    public (int Index, int Generation) Next =>
        _free.TryPeek(out int slot) ? (slot, _generations[slot - 1]) : (_generations.Count + 1, 0);

    /// <summary>Whether an item was let go. Cheap, so drawing can ask every frame.</summary>
    public bool IsUnloaded(int index) => _unloaded.Count > 0 && _unloaded.Contains(index);

    /// <summary>
    /// Whether what a ref named is gone: let go, or let go and its slot taken by something else since. An index that
    /// names no slot, such as a built-in shape's, isn't gone. Cheap, so drawing can ask every frame.
    /// </summary>
    public bool IsGone(int index, int generation) =>
        index >= 1 && index <= _generations.Count && (_generations[index - 1] != generation || IsUnloaded(index));

    /// <summary>Items let go since the window last looked. Indices into the loader, not <see cref="ContentItems.Items"/>.</summary>
    public ReadOnlySpan<int> PendingUnloads => CollectionsMarshal.AsSpan(_pendingUnloads);

    /// <summary>
    /// Slots the window had loaded something into that a new item has taken since it last looked, so it must load
    /// them again.
    /// </summary>
    public ReadOnlySpan<int> Retaken => CollectionsMarshal.AsSpan(_retaken);

    public void ClearRetaken() => _retaken.Clear();

    /// <summary>A new slot at the end, and its index.</summary>
    public int Append(bool reusable)
    {
        _generations.Add(0);
        _reusable.Add(reusable);
        return _generations.Count;
    }

    /// <summary>Takes a slot an unloaded item gave back, if there is one, and gives its index and generation.</summary>
    public bool TryRetake(out int slot, out int generation)
    {
        if (!_free.TryPop(out slot))
        {
            generation = 0;
            return false;
        }

        _unloaded.Remove(slot);
        if (slot <= Seen)
        {
            _retaken.Add(slot);
        }

        generation = _generations[slot - 1];
        return true;
    }

    /// <summary>
    /// Marks a slot let go. One the window has seen waits for it to free what it made; one it hasn't is given back at
    /// once, and this says so, since it then counts as loaded.
    /// </summary>
    public bool Unload(int index)
    {
        _unloaded.Add(index);
        if (index <= Seen)
        {
            _pendingUnloads.Add(index);
            return false;
        }

        GiveBack(index);
        return true;
    }

    /// <summary>
    /// Called once the window has freed what the pending unloads made: gives back their slots that can be taken
    /// again, and clears the list.
    /// </summary>
    public void ReleasePendingUnloads()
    {
        foreach (int index in _pendingUnloads)
        {
            GiveBack(index);
        }

        _pendingUnloads.Clear();
    }

    /// <summary>Lets a slot be taken again, if its kind allows, and moves its generation on so old refs stay gone.</summary>
    private void GiveBack(int index)
    {
        if (_reusable[index - 1])
        {
            _generations[index - 1]++;
            _free.Push(index);
        }
    }
}
