namespace Super.Ecs;

public sealed partial class World : IStateHook
{
    private readonly List<Entity> _leaving = [];

    void IStateHook.Exited<TState>(TState state) => DestroyScopedTo(state);

    // Whatever was just placed for the new state should appear there, not glide from the old one.
    void IStateHook.Entered() => RememberTransforms();

    /// <summary>Destroys every entity scoped to this state. Collects first, since destroying moves rows.</summary>
    private void DestroyScopedTo<TState>(TState state)
        where TState : struct, Enum
    {
        int scopedType = TypeIdOrMissing<ScopedTo<TState>>();
        if (scopedType < 0)
        {
            return;
        }

        var same = EqualityComparer<TState>.Default;
        ulong mask = 1UL << scopedType;
        int index = -1;
        while (NextArchetype(ref index, mask) is { } archetype)
        {
            var scopes = archetype.Column<ScopedTo<TState>>(scopedType).Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                if (same.Equals(scopes[i].State, state))
                {
                    _leaving.Add(archetype.Entities[i]);
                }
            }
        }

        foreach (var entity in _leaving)
        {
            Destroy(entity);
        }

        _leaving.Clear();
    }
}
