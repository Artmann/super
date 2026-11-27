namespace Super;

/// <summary>
/// Which named items the window has finished loading. Without a window, or anywhere but the interactive one,
/// everything counts as loaded at once, so the speed of a disk can't change what a seed does. A slot taken again
/// after an unload counts as a new item, so the counts only grow while the list stays as long as the slots.
/// </summary>
internal sealed class ItemProgress
{
    private readonly List<bool> _itemLoaded = [];
    private int _named;
    private int _loaded;
    private int _batchStart;

    /// <summary>True only in the interactive window, where loading happens over real time.</summary>
    public bool Streaming { get; set; }

    /// <summary>
    /// How long a frame may spend sending finished items to the GPU while streaming, from
    /// <see cref="GraphicsScreen.UploadBudget"/>. One item always goes, however long it takes.
    /// </summary>
    public TimeSpan UploadBudget { get; set; } = TimeSpan.FromMilliseconds(GraphicsScreen.DefaultUploadBudget);

    /// <summary>Set by tests, to keep a loading screen up for as long as they need it.</summary>
    public bool Held { get; set; }

    /// <summary>Whether everything named so far is ready to draw.</summary>
    public bool IsLoaded => !Held && (!Streaming || _loaded >= _named);

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

            return Held ? 0f : (_loaded - _batchStart) / (float)(_named - _batchStart);
        }
    }

    /// <summary>Counts one more item named, not loaded yet, in a new slot or in one an unloaded item left.</summary>
    public void Added(int index)
    {
        if (index > _itemLoaded.Count)
        {
            _itemLoaded.Add(false);
        }
        else
        {
            _itemLoaded[index - 1] = false;
        }

        _named++;
    }

    /// <summary>
    /// Counts the item at <paramref name="index"/> as ready. The window calls this as it finishes each one. An item
    /// already counted isn't counted again, as one dropped before the window saw it is reached by the window later.
    /// </summary>
    public void ItemLoaded(int index)
    {
        if (_itemLoaded[index - 1])
        {
            return;
        }

        _itemLoaded[index - 1] = true;
        _loaded++;
        if (_loaded == _named)
        {
            _batchStart = _loaded;
        }
    }

    /// <summary>Whether the item at <paramref name="index"/> is ready to draw, unloaded or not.</summary>
    public bool IsReady(int index) => !Held && (!Streaming || _itemLoaded[index - 1]);
}
