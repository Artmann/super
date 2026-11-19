namespace Super;

/// <summary>
/// A number for each component type, the same in every world, so a world finds its own ID for a type with an array
/// read instead of a dictionary lookup.
/// </summary>
internal static class ComponentIndex
{
    private static int _last = -1;

    public static int Next() => Interlocked.Increment(ref _last);
}

/// <summary>The type's number, given out the first time any world asks for it.</summary>
internal static class ComponentIndex<T>
{
    public static readonly int Value = ComponentIndex.Next();
}
