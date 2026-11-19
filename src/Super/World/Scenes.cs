namespace Super;

/// <summary>
/// Swaps what's in the world: <see cref="Go"/> destroys the open scene's entities and builds the next. Ask for it in
/// your game's constructor. Scenes are for where in the world the game is; <see cref="States{TState}"/> are for where
/// the game is, such as a menu or paused, and a game can use either or both.
/// </summary>
public sealed class Scenes(World world)
{
    private Action<Scene>? _next;
    private int _built;

    /// <summary>The open scene, or none before the first <see cref="Go"/>.</summary>
    public Scene? Current { get; private set; }

    /// <summary>
    /// Goes to a new scene once this tick ends: the tick finishes in the old one, then its entities are destroyed and
    /// <paramref name="build"/> runs. Asking twice in a tick keeps the last. The build can't read input, as it runs
    /// between ticks.
    /// </summary>
    public void Go(Action<Scene> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _next = build;
    }

    /// <summary>Builds the open scene again from the start, once this tick ends, for a restart or after a code change.</summary>
    public void Rebuild()
    {
        if (Current is null)
        {
            throw new InvalidOperationException("There's no scene to rebuild yet. Call Go first.");
        }

        _next = Current.Build;
    }

    /// <summary>Swaps in the scene asked for, at the end of a tick, after state changes.</summary>
    internal void Apply()
    {
        if (_next is not { } build)
        {
            return;
        }

        _next = null;
        if (Current is { } old)
        {
            Clear(old.Id);
        }

        Current = new Scene(world, ++_built, build);
        build(Current);
    }

    // Members are gathered first, since destroying one moves the rows after it.
    private void Clear(int scene)
    {
        int memberType = world.Archetypes.TypeIdOrMissing<SceneMember>();
        if (memberType < 0)
        {
            return;
        }

        var members = new List<Entity>();
        int index = -1;
        while (world.Archetypes.NextMatching(ref index, default(ComponentMask).With(memberType)) is { } archetype)
        {
            var tags = archetype.Column<SceneMember>(memberType).Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                if (tags[i].Scene == scene)
                {
                    members.Add(archetype.Entities[i]);
                }
            }
        }

        foreach (var member in members)
        {
            if (world.IsAlive(member))
            {
                world.Destroy(member);
            }
        }
    }
}
