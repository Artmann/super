
namespace Super.Physics;

/// <summary>Orders entities by id, then generation, so lists of them come out the same on every run.</summary>
internal sealed class EntityOrder : IComparer<Entity>
{
    public static readonly EntityOrder Instance = new();

    // List.Sort wraps an IComparer in a new delegate on every call; sorting by this one allocates nothing.
    public static readonly Comparison<Entity> Comparison = Instance.Compare;

    public int Compare(Entity a, Entity b) => a.Id != b.Id ? a.Id.CompareTo(b.Id) : a.Generation.CompareTo(b.Generation);
}
