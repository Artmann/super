namespace Super.Ecs;

public sealed partial class World : ITickHook
{
    void ITickHook.BeforeUpdate() => RememberTransforms();

    /// <summary>
    /// Copies every <see cref="Smoothed"/> entity's transform into its memory of last
    /// tick. The game loop does this before each Update.
    /// </summary>
    internal void RememberTransforms()
    {
        int transformType = TypeIdOrMissing<Transform>();
        int smoothedType = TypeIdOrMissing<Smoothed>();
        if (transformType < 0 || smoothedType < 0)
        {
            return;
        }

        int swimType = TypeIdOrMissing<Swim>();
        ulong mask = (1UL << transformType) | (1UL << smoothedType);
        int index = -1;
        while (NextArchetype(ref index, mask) is { } archetype)
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
