namespace Super;

/// <summary>
/// Each entity's children, kept as their <see cref="Parent"/> components land, change and leave, so destroying an
/// entity finds its children without walking every parented entity in the world.
/// <para>
/// A parent can also be changed by writing through a reference, from <see cref="World.Get{T}"/> or a query, which the
/// index can't see. Handing out such a reference marks the index unsure, and the next lookup rebuilds it from the
/// components once. Each lookup also checks every child it returns against its component, so one moved away is skipped.
/// </para>
/// </summary>
internal sealed class ChildIndex
{
    private readonly Dictionary<Entity, List<Entity>> _byParent = [];
    private readonly Stack<List<Entity>> _spare = [];

    // By child ID: the parent it's listed under, and where in that parent's list, so it leaves in one step.
    private Link[] _links = new Link[64];
    private bool _unsure;

    /// <summary>Marks that a parent may have been written by reference, so the next lookup rebuilds the index.</summary>
    public void MarkUnsure() => _unsure = true;

    /// <summary>Lists <paramref name="child"/> under <paramref name="parent"/>, and no longer under any other.</summary>
    public void Set(Entity child, Entity parent)
    {
        if (child.Id >= _links.Length)
        {
            Array.Resize(ref _links, Math.Max(child.Id + 1, _links.Length * 2));
        }

        ref var link = ref _links[child.Id];
        if (link.Listed)
        {
            if (link.Parent == parent && link.Child == child)
            {
                return;
            }

            Unset(child);
        }

        if (!_byParent.TryGetValue(parent, out var children))
        {
            children = _spare.Count > 0 ? _spare.Pop() : [];
            _byParent.Add(parent, children);
        }

        link = new Link(child, parent, children.Count, Listed: true);
        children.Add(child);
    }

    /// <summary>Takes <paramref name="child"/> off its parent's list, when it's on one.</summary>
    public void Unset(Entity child)
    {
        if (child.Id >= _links.Length || !_links[child.Id].Listed || _links[child.Id].Child != child)
        {
            return;
        }

        ref var link = ref _links[child.Id];
        var children = _byParent[link.Parent];

        // The last child fills the hole, so taking one off is one step however many there are.
        var last = children[^1];
        children[link.Position] = last;
        _links[last.Id].Position = link.Position;
        children.RemoveAt(children.Count - 1);
        if (children.Count == 0)
        {
            _byParent.Remove(link.Parent);
            _spare.Push(children);
        }

        link = default;
    }

    /// <summary>
    /// Takes <paramref name="parent"/>'s list out of the index, or null when it has no children. The caller hands it
    /// back with <see cref="Return"/>, once it has destroyed them.
    /// </summary>
    public List<Entity>? Take(World world, Entity parent)
    {
        if (_unsure)
        {
            Rebuild(world);
        }

        if (!_byParent.Remove(parent, out var children))
        {
            return null;
        }

        foreach (var child in children)
        {
            _links[child.Id] = default;
        }

        return children;
    }

    /// <summary>A copy of <paramref name="parent"/>'s children, leaving the index as it is.</summary>
    public List<Entity> CopyOf(World world, Entity parent)
    {
        if (_unsure)
        {
            Rebuild(world);
        }

        return _byParent.TryGetValue(parent, out var children) ? [.. children] : [];
    }

    public void Return(List<Entity> children)
    {
        children.Clear();
        _spare.Push(children);
    }

    // Lists every parented entity again from its component, in query order.
    private void Rebuild(World world)
    {
        _unsure = false;
        foreach (var (_, children) in _byParent)
        {
            Return(children);
        }

        _byParent.Clear();
        Array.Clear(_links);
        int parentType = world.Archetypes.TypeIdOrMissing<Parent>();
        if (parentType < 0)
        {
            return;
        }

        int index = -1;
        while (world.Archetypes.NextMatching(ref index, default(ComponentMask).With(parentType)) is { } archetype)
        {
            var parents = archetype.Column<Parent>(parentType).Values;
            for (int row = 0; row < archetype.Count; row++)
            {
                Set(archetype.Entities[row], parents[row].Value);
            }
        }
    }

    private record struct Link(Entity Child, Entity Parent, int Position, bool Listed);
}
