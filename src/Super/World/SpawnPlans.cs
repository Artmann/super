namespace Super;

/// <summary>
/// Where a spawn of some component types ends up, worked out once and kept: the archetype it lands in, the required
/// values to write after the given ones, and the types that land, in the order they would have one at a time. A spawn
/// then puts the entity straight into that archetype instead of moving it through one archetype per component.
/// </summary>
internal sealed class SpawnPlans(ArchetypeTable types)
{
    private readonly Dictionary<SpawnKey, SpawnPlan> _plans = [];

    // The plans of spawns given a list of any length, keyed by the scene type and then the given types.
    private readonly Dictionary<int[], SpawnPlan> _listed = new(new TypeLists());
    private int _requiredVersion;

    /// <summary>
    /// The plan for a spawn given the type IDs in <paramref name="key"/>, in call order. A scene's spawn puts its
    /// scene type on first.
    /// </summary>
    public SpawnPlan For(in SpawnKey key)
    {
        Refresh();
        if (!_plans.TryGetValue(key, out var plan))
        {
            Span<int> given = [key.First, key.Second, key.Third, key.Fourth, key.Fifth];
            plan = Make(key.Scene, given);
            _plans.Add(key, plan);
        }

        return plan;
    }

    /// <summary>
    /// The plan for a spawn given any number of types: the scene's type first, or -1 for none, then the type IDs in
    /// call order.
    /// </summary>
    public SpawnPlan For(ReadOnlySpan<int> sceneThenGiven)
    {
        Refresh();
        var plans = _listed.GetAlternateLookup<ReadOnlySpan<int>>();
        if (!plans.TryGetValue(sceneThenGiven, out var plan))
        {
            plan = Make(sceneThenGiven[0], sceneThenGiven[1..]);
            plans[sceneThenGiven] = plan;
        }

        return plan;
    }

    // A requirement added since is part of every spawn of its type, so every plan is made again.
    private void Refresh()
    {
        if (_requiredVersion != types.Required.Version)
        {
            _plans.Clear();
            _listed.Clear();
            _requiredVersion = types.Required.Version;
        }
    }

    // Walks the same steps that adding the components one at a time takes, so the archetypes are made in the same
    // order and the same values win: the given ones first, then what each new one requires, depth first.
    private SpawnPlan Make(int scene, ReadOnlySpan<int> given)
    {
        var archetype = types.Empty;
        var landed = new List<int>();
        var writes = new List<RequiredComponents.Requirement>();
        if (scene >= 0)
        {
            archetype = types.With(archetype, scene);
            landed.Add(scene);
        }

        var fresh = new List<int>(given.Length);
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

    /// <summary>Compares lists of type IDs by what's in them, and looks one up from a span without making an array.</summary>
    private sealed class TypeLists : IEqualityComparer<int[]>, IAlternateEqualityComparer<ReadOnlySpan<int>, int[]>
    {
        public bool Equals(int[]? x, int[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] list) => GetHashCode(list.AsSpan());

        public bool Equals(ReadOnlySpan<int> alternate, int[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<int> alternate)
        {
            var hash = new HashCode();
            foreach (int type in alternate)
            {
                hash.Add(type);
            }

            return hash.ToHashCode();
        }

        public int[] Create(ReadOnlySpan<int> alternate) => alternate.ToArray();
    }
}

/// <summary>
/// What a spawn is given: the type IDs of its components, in call order, with -1 for the unused ones, and the type
/// that marks its scene, or -1 when it belongs to none.
/// </summary>
internal readonly record struct SpawnKey(int Scene, int First, int Second = -1, int Third = -1, int Fourth = -1, int Fifth = -1);

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
