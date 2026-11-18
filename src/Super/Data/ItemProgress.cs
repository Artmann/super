namespace Super;

/// <summary>
/// Which named items the window has finished loading. Without a window, or anywhere but the interactive one,
/// everything counts as loaded at once, so the speed of a disk can't change what a seed does.
/// </summary>
internal sealed class ItemProgress
{
    private readonly List<bool> _itemLoaded = [];
    private int _loaded;
    private int _batchStart;

    /// <summary>True only in the interactive window, where loading happens over real time.</summary>
    public bool Streaming { get; set; }

    /// <summary>Set by tests, to keep a loading screen up for as long as they need it.</summary>
    public bool Held { get; set; }

    /// <summary>Whether everything named so far is ready to draw.</summary>
    public bool IsLoaded => !Held && (!Streaming || _loaded >= _itemLoaded.Count);

    /// <summary>
    /// How far loading has got, from 0 to 1. It counts what's been named since the last
    /// time everything was loaded, so a second loading screen starts again from 0.
    /// </summary>
    public float Fraction
    {
        get
        {
            if (IsLoaded)
            {
                return 1f;
            }

            return Held ? 0f : (_loaded - _batchStart) / (float)(_itemLoaded.Count - _batchStart);
        }
    }

    /// <summary>Counts one more item named, not loaded yet.</summary>
    public void Added() => _itemLoaded.Add(false);

    /// <summary>Counts one more item as ready. The window calls this as it finishes each one.</summary>
    public void ItemLoaded(int index)
    {
        _itemLoaded[index - 1] = true;
        _loaded++;
        if (_loaded == _itemLoaded.Count)
        {
            _batchStart = _loaded;
        }
    }

    /// <summary>Whether the item at <paramref name="index"/> is ready to draw, unloaded or not.</summary>
    public bool IsReady(int index) => !Held && (!Streaming || _itemLoaded[index - 1]);
}
