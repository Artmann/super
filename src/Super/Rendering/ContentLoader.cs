namespace Super;

/// <summary>
/// Turns what <see cref="Content"/> names into something drawable. The slow part of each
/// item runs on a background thread, and the upload runs on the main thread inside
/// <see cref="Update"/> or <see cref="Get"/>, since only that thread may touch the GPU.
/// While streaming, one finished item uploads per frame, so a loading screen keeps moving.
/// Otherwise everything uploads before the frame is drawn, in the order it was named.
/// </summary>
internal sealed class ContentLoader<TLoaded>(TLoaded first, Func<object, object> prepare, Func<object, Content, TLoaded> upload)
    where TLoaded : class
{
    private readonly List<TLoaded?> _loaded = [first];
    private readonly List<Task<object>?> _preparing = [null];
    private int _firstWaiting = 1;

    /// <summary>Starts anything newly named, then uploads what this frame should.</summary>
    public void Update(Content content)
    {
        while (_loaded.Count <= content.Items.Count)
        {
            object item = content.Items[_loaded.Count - 1];
            _loaded.Add(null);
            _preparing.Add(Task.Run(() => prepare(item)));
        }

        while (_firstWaiting < _loaded.Count && _loaded[_firstWaiting] is not null)
        {
            _firstWaiting++;
        }

        for (int i = _firstWaiting; i < _loaded.Count; i++)
        {
            if (_loaded[i] is not null)
            {
                continue;
            }

            if (!content.Streaming)
            {
                Finish(i, content);
            }
            else if (_preparing[i]!.IsCompleted)
            {
                Finish(i, content);
                return;
            }
        }
    }

    /// <summary>Hands back an item, waiting for it if it isn't ready. Something being drawn can't wait for later.</summary>
    public TLoaded Get(int index, Content content) => _loaded[index] ?? Finish(index, content);

    /// <summary>
    /// Takes what a slot holds and leaves a marker in its place. An item still being
    /// prepared is dropped unfinished and counted as loaded, so nothing waits for it.
    /// </summary>
    public TLoaded? Take(int index, TLoaded marker, Content content)
    {
        var loaded = _loaded[index];
        _loaded[index] = marker;
        if (loaded is null)
        {
            _preparing[index] = null;
            content.ItemLoaded(index);
        }

        return loaded;
    }

    private TLoaded Finish(int index, Content content)
    {
        // Rethrows here, on the main thread, if the background work failed.
        object prepared = _preparing[index]!.GetAwaiter().GetResult();
        var loaded = upload(prepared, content);
        _loaded[index] = loaded;
        _preparing[index] = null;
        content.ItemLoaded(index);
        return loaded;
    }
}
