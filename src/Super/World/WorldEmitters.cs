namespace Super;

/// <summary>What the world puts in a frame's particles by itself: the bursts of every <see cref="Emitter"/>.</summary>
internal static class WorldEmitters
{
    /// <summary>Draws every emitter's bursts that are still playing at the time on screen.</summary>
    public static void Describe(View view, World world)
    {
        int type = world.Archetypes.TypeIdOrMissing<Emitter>();
        if (type < 0)
        {
            return;
        }

        // The time on screen, counted from the first tick.
        double now = view.SecondsSince(0);
        world.BeginIteration();
        try
        {
            int index = -1;
            while (world.Archetypes.NextMatching(ref index, default(ComponentMask).With(type)) is { } archetype)
            {
                var emitters = archetype.Column<Emitter>(type).Values;
                for (int i = 0; i < archetype.Count; i++)
                {
                    var entity = archetype.Entities[i];
                    if (!WorldFrame.IsHidden(world, entity))
                    {
                        Bursts(view, world, entity, in emitters[i], now);
                    }
                }
            }
        }
        finally
        {
            world.EndIteration();
        }
    }

    /// <summary>
    /// Draws each burst started at a multiple of Every that's still playing, keyed by entity and start, so its
    /// particles stay its own. Bursts before the first tick count too, so an emitter is already going when it appears.
    /// </summary>
    private static void Bursts(View view, World world, Entity entity, in Emitter emitter, double now)
    {
        ArgumentNullException.ThrowIfNull(emitter.Effect);
        if (!(emitter.Every > 0f) || float.IsInfinity(emitter.Every))
        {
            throw new ArgumentOutOfRangeException(nameof(emitter), emitter.Every,
                $"An emitter's Every must be more than zero seconds. Entity {entity.Id}'s Emitter has Every {emitter.Every}.");
        }

        var matrix = WorldFrame.WorldMatrix(world, entity, view.Blend);
        long last = (long)Math.Floor(now / emitter.Every);
        long first = last - (long)Math.Ceiling(emitter.Effect.Duration / emitter.Every);
        for (long start = last; start >= first; start--)
        {
            float age = (float)(now - start * (double)emitter.Every);
            view.Effects.Particles(HashCode.Combine(entity.Id, start), emitter.Effect, matrix, age);
        }
    }
}
