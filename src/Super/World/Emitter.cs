namespace Super;

/// <summary>
/// Plays a particle effect at the entity for as long as it has this, such as a torch's flames or a chimney's smoke.
/// A new burst starts every <paramref name="Every"/> seconds of game time and plays its whole
/// <see cref="ParticleEffect.Duration"/>, so bursts overlap and the effect never runs out. Each burst is worked out
/// from the entity and the time alone, so the same moment always looks the same, paused, loaded or recorded. It
/// follows the entity's <see cref="Transform"/> and <see cref="Parent"/>, and <see cref="Hidden"/> stops it.
/// </summary>
/// <param name="Effect">What each burst draws. A <see cref="ParticleEmitter"/> can be passed on its own.</param>
/// <param name="Every">Seconds between bursts, more than 0. An effect born over one second looks steady at 1.</param>
public readonly record struct Emitter(ParticleEffect Effect, float Every = 1f);
