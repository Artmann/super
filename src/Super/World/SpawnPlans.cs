namespace Super;

/// <summary>
/// Where a spawn of some component types ends up, worked out once and kept: the archetype it lands in, the required
/// values to write after the given ones, and the types that land, in the order they would have one at a time. A spawn
/// then puts the entity straight into that archetype instead of moving it through one archetype per component.
/// </summary>
internal sealed class SpawnPlans(ArchetypeTable types)
{
    private readonly Dictionary<Key, SpawnPlan> _plans = [];
    private int _requiredVersion;

    /// <summary>
    /// The plan for a spawn given these type IDs, in call order, with -1 for the unused ones. A scene's spawn puts its
    /// <paramref name="sceneType"/> on first, and -1 means it belongs to no scene.
    /// </summary>
    public SpawnPlan For(int sceneType, int first, int second, int third, int fourth)
    {
        // A requirement added since is part of every spawn of its type, so every plan is made again.
        if (_requiredVersion != types.Required.Version)
        {
            _plans.Clear();
            _requiredVersion = types.Required.Version;
        }

        var key = new Key(sceneType, first, second, third, fourth);
        if (!_plans.TryGetValue(key, out var plan))
        {
            plan = Make(key);
            _plans.Add(key, plan);
        }

        return plan;
    }

    // Walks the same steps that adding the components one at a time takes, so the archetypes are made in the same
    // order and the same values win: the given ones first, then what each new one requires, depth first.
    private SpawnPlan Make(Key key)
    {
        var archetype = types.Empty;
        var landed = new List<int>();
        var writes = new List<RequiredComponents.Requirement>();
        if (key.Scene >= 0)
        {
            archetype = types.With(archetype, key.Scene);
            landed.Add(key.Scene);
        }

        Span<int> given = [key.First, key.Second, key.Third, key.Fourth];
        var fresh = new List<int>(4);
        foreach (int type in given)
        {
            if (type >= 0 && !archetype.Mask.Has(type))
            {
                archetype = types.With(archetype, type);
                landed.Add(type);
                fresh.Add(type);
            }
        }

        foreach (int type in fresh)
        {
            Bring(ref archetype, type, landed, writes);
        }

        return new SpawnPlan(archetype, [.. landed], [.. writes]);
    }

    private void Bring(ref Archetype archetype, int type, List<int> landed, List<RequiredComponents.Requirement> writes)
    {
        if (types.Required.Of(type) is not { } requirements)
        {
            return;
        }

        foreach (var requirement in requirements)
        {
            bool has = archetype.Mask.Has(requirement.Needed);
            if (has && !requirement.Replace)
            {
                continue;
            }

            writes.Add(requirement);
            if (!has)
            {
                archetype = types.With(archetype, requirement.Needed);
                landed.Add(requirement.Needed);
                Bring(ref archetype, requirement.Needed, landed, writes);
            }
        }
    }

    private readonly record struct Key(int Scene, int First, int Second, int Third, int Fourth);
}

/// <summary>One kind of spawn, made in one move: see <see cref="SpawnPlans"/>.</summary>
internal sealed class SpawnPlan(Archetype target, int[] landed, RequiredComponents.Requirement[] required)
{
    /// <summary>The archetype the new entity lands in.</summary>
    public Archetype Target { get; } = target;

    /// <summary>Every type the entity gets, in the order its <c>OnAdded</c> handlers run.</summary>
    public int[] Landed { get; } = landed;

    /// <summary>The required values to write after the given ones, in order, so a later one wins.</summary>
    public RequiredComponents.Requirement[] Required { get; } = required;
}
