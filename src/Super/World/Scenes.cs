namespace Super;

/// <summary>
/// Swaps what's in the world: <see cref="Go"/> destroys the open scene's entities and builds the next. Ask for it in
/// your game's constructor. Scenes are for where in the world the game is; <see cref="States{TState}"/> are for where
/// the game is, such as a menu or paused, and a game can use either or both.
/// </summary>
public sealed class Scenes(World world, Transitions transitions)
{
    private Action<Scene>? _next;
    private int _built;

    // A transition to start at the swap, for the kinds that show the old picture over the new.
    private Transition? _atSwap;

    // The transition the last Go started, so IsChanging can tell it's still on screen.
    private Transition? _started;

    // A fade is going out: the swap waits for its cut, which sets _cutDue.
    private bool _waitingForCut;
    private bool _cutDue;

    /// <summary>The world the scenes are built in, for the loop's own steps over it.</summary>
    internal World World => world;

    /// <summary>The open scene, or none before the first <see cref="Go"/>.</summary>
    public Scene? Current { get; private set; }

    /// <summary>Whether a <see cref="Go"/> hasn't swapped yet, or its transition is still on screen.</summary>
    public bool IsChanging => _next is not null || (_started is not null && ReferenceEquals(transitions.Playing, _started));

    /// <summary>
    /// Goes to a new scene once this tick ends: the tick finishes in the old one, then its entities are destroyed and
    /// <paramref name="build"/> runs. Asking twice in a tick keeps the last. The build can't read input, as it runs
    /// between ticks. With a fade, the swap waits for the end of the tick the screen is all colour, and asking again
    /// before then only changes what gets built. The other transitions start at the swap and show the old picture
    /// going.
    /// </summary>
    public void Go(Action<Scene> build, Transition? transition = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        _next = build;
        if (_waitingForCut)
        {
            return;
        }

        _atSwap = null;
        if (transition is null)
        {
            return;
        }

        if (transition.Kind != TransitionKind.Fade)
        {
            _atSwap = transition;
            return;
        }

        _waitingForCut = true;
        _cutDue = false;
        _started = transition;
        transitions.Play(transition, () => _cutDue = true);
    }

    /// <summary>
    /// Builds the open scene again from the start, once this tick ends, for a restart or after a code change. After a
    /// <see cref="Go"/> that hasn't swapped yet it does nothing, as that scene is about to be built fresh anyway.
    /// A transition covers it the way it covers a <see cref="Go"/>.
    /// </summary>
    public void Rebuild(Transition? transition = null)
    {
        if (_next is not null)
        {
            return;
        }

        if (Current is null)
        {
            throw new InvalidOperationException("There's no scene to rebuild yet. Call Go first.");
        }

        Go(Current.Build, transition);
    }

    /// <summary>Swaps in the scene asked for, at the end of a tick, after state changes, once a fade's cut is due.</summary>
    internal void Apply()
    {
        if (_next is not { } build || (_waitingForCut && !_cutDue))
        {
            return;
        }

        _next = null;
        _waitingForCut = false;
        _cutDue = false;
        if (_atSwap is { } transition)
        {
            _atSwap = null;
            _started = transition;
            transitions.Play(transition);
        }

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
