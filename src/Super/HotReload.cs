using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Super.HotReload))]

namespace Super;

/// <summary>
/// Hears from .NET hot reload, as run by <c>dotnet watch</c> or an IDE, when it has changed
/// the program's code. The runtime finds this class through the attribute above and calls
/// it from its own thread, so all it does is count. Each game notices at the start of its
/// next tick, on the game's own thread.
/// </summary>
internal static class HotReload
{
    private static int _changes;

    /// <summary>How many times the code has changed since the program started.</summary>
    internal static int Changes => Volatile.Read(ref _changes);

    /// <summary>
    /// Whether the code can change while the program runs. True under <c>dotnet watch</c> and
    /// an IDE's hot reload, false in a plain <c>dotnet run</c> or a published game.
    /// </summary>
    internal static bool IsOn => MetadataUpdater.IsSupported;

    /// <summary>
    /// Called by the runtime after an update. Hot reload keeps every type the same type, so
    /// what Super keeps by type, such as the world's component ids, is still right.
    /// </summary>
    internal static void ClearCache(Type[]? updatedTypes)
    {
    }

    /// <summary>Called by the runtime once an update is in place.</summary>
    internal static void UpdateApplication(Type[]? updatedTypes) => Interlocked.Increment(ref _changes);
}
