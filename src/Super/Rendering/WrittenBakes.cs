using System.Collections.Concurrent;
using Rendition;

namespace Super;

/// <summary>
/// The chunk bakes a <see cref="BakeSaver"/> wrote to the <see cref="BakeCache"/> on the thread pool, kept until the
/// render thread calls <see cref="ForgetIn"/>, which forgets what a <see cref="LightBakeStore"/> holds for each, so the
/// next frame reads the new bake instead of the old one or none.
/// </summary>
internal sealed class WrittenBakes
{
    private readonly ConcurrentQueue<string> _files = new();

    /// <summary>Notes that <paramref name="file"/> was written. Safe from any thread.</summary>
    public void Written(string file) => _files.Enqueue(file);

    /// <summary>Forgets in <paramref name="store"/> every file written since the last call. On the render thread.</summary>
    public void ForgetIn(LightBakeStore store)
    {
        while (_files.TryDequeue(out string? file))
        {
            store.Forget(file);
        }
    }
}
