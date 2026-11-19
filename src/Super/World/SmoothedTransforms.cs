namespace Super;

/// <summary>Keeps each <see cref="Smoothed"/> entity's memory of its last tick, which drawing blends from.</summary>
internal static class SmoothedTransforms
{
    /// <summary>
    /// Copies every <see cref="Smoothed"/> entity's transform into its memory of last
    /// tick. The game loop does this before each Update.
    /// </summary>
    public static void Remember(World world)
    {
        var archetypes = world.Archetypes;
        int transformType = archetypes.TypeIdOrMissing<Transform>();
        int smoothedType = archetypes.TypeIdOrMissing<Smoothed>();
        if (transformType < 0 || smoothedType < 0)
        {
            return;
        }

        int swimType = archetypes.TypeIdOrMissing<Swim>();
        ulong mask = (1UL << transformType) | (1UL << smoothedType);
        int index = -1;
        while (archetypes.NextMatching(ref index, mask) is { } archetype)
        {
            var transforms = archetype.Column<Transform>(transformType).Values;
            var smoothed = archetype.Column<Smoothed>(smoothedType).Values;
            var swims = archetype.ColumnOrNull<Swim>(swimType)?.Values;
            for (int i = 0; i < archetype.Count; i++)
            {
                smoothed[i].Previous = transforms[i];
                smoothed[i].HasPrevious = true;
                if (swims is not null)
                {
                    smoothed[i].PreviousPhase = swims[i].Phase;
                }
            }
        }
    }
}
