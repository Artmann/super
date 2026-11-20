namespace Super;

/// <summary>
/// Plays a particle effect once where the entity is, then destroys the entity, such as the sparkle where a coin was
/// or the dust where something landed. Spawn one and forget it: <c>world.Spawn(Transform.At(spot), new Burst(Sparkle))</c>.
/// It starts on the tick it's spawned, follows the entity's <see cref="Transform"/> and <see cref="Parent"/>, and
/// <see cref="Hidden"/> hides it. For a burst on something that stays, spawn it as that entity's child. For an
/// effect that keeps going, use an <see cref="Emitter"/>.
/// </summary>
/// <param name="Effect">What to play. A <see cref="ParticleEmitter"/> can be passed on its own.</param>
public record struct Burst(ParticleEffect Effect)
{
    /// <summary>The tick it started on, or none until the end of the tick it was spawned in.</summary>
    internal long? Started;
}

/// <summary>Starts every <see cref="Burst"/>, draws it, and clears it away once it has played.</summary>
internal static class WorldBursts
{
    /// <summary>
    /// Starts the bursts spawned this tick and destroys the ones that have played to the end. The game loop calls it
    /// after each Update.
    /// </summary>
    public static void Advance(World world, long tick, float tickSeconds)
    {
        if (world.Archetypes.TypeIdOrMissing<Burst>() < 0)
        {
            return;
        }

        foreach (var row in world.Query<Burst>())
        {
            ref var burst = ref row.First;
            ArgumentNullException.ThrowIfNull(burst.Effect);
            burst.Started ??= tick;
            if ((tick - burst.Started.Value) * tickSeconds >= burst.Effect.Duration)
            {
                world.Destroy(row.Entity);
            }
        }
    }

    /// <summary>Draws every burst that has started, at its age on screen.</summary>
    public static void Describe(View view, World world)
    {
        if (world.Archetypes.TypeIdOrMissing<Burst>() < 0)
        {
            return;
        }

        foreach (var row in world.Query<Burst>())
        {
            if (row.First.Started is not { } started || WorldFrame.IsHidden(world, row.Entity))
            {
                continue;
            }

            var matrix = WorldFrame.WorldMatrix(world, row.Entity, view.Blend, view.Clock.PoseAhead);
            view.Effects.Particles(HashCode.Combine(row.Entity.Id, started), row.First.Effect, matrix, view.SecondsSince(started));
        }
    }
}
