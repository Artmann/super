namespace Super;

/// <summary>
/// Turns what <see cref="Content"/> names into something drawable. The slow part of each
/// item runs on a background thread, and the upload runs on the main thread inside
/// <see cref="Update"/> or <see cref="Get"/>, since only that thread may touch the GPU.
/// While streaming, finished items upload until the frame's <see cref="ItemProgress.UploadBudget"/> is used, and at
/// least one does, so a loading screen keeps moving and many small items still land in a few frames.
/// Otherwise everything uploads before the frame is drawn, in the order it was named.
/// </summary>
internal sealed class ContentLoader<TLoaded>(TLoaded first, Func<object, object> prepare, Func<object, Content, TLoaded> upload)
    where TLoaded : class
{
    private readonly List<TLoaded?> _loaded = [first];
    private readonly List<Task<object>?> _preparing = [null];
    private int _firstWaiting = 1;

    /// <summary>
    /// Starts an item's background work. A thread pool task, unless a test needs work that waits on it to
    /// have threads of its own, so a busy pool can't hold it up.
    /// </summary>
    public Func<Func<object>, Task<object>> Start { get; init; } = work => Task.Run(work);

    /// <summary>What the uploads are timed by, so a test can say how long each one takes.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Starts anything newly named, then uploads what this frame should.</summary>
    public void Update(Content content)
    {
        long started = Clock.GetTimestamp();
        var items = content.Store.Items;
        while (_loaded.Count <= items.Count)
        {
            object item = items[_loaded.Count - 1];
            _loaded.Add(null);
            _preparing.Add(Start(() => prepare(item)));
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

            if (!content.Store.Loading.Streaming)
            {
                Finish(i, content);
            }
            else if (_preparing[i]!.IsCompleted)
            {
                Finish(i, content);
                if (Clock.GetElapsedTime(started) >= content.Store.Loading.UploadBudget)
                {
                    return;
                }
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
            content.Store.Loading.ItemLoaded(index);
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
        content.Store.Loading.ItemLoaded(index);
        return loaded;
    }
}
