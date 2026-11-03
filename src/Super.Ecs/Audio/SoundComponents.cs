namespace Super.Ecs;

/// <summary>
/// Makes an entity play a looping sound for as long as it has this. With a <see cref="Transform"/> (and
/// <see cref="Smoothed"/> and <see cref="Parent"/>, as for drawing) the sound sits where the entity is drawn and
/// follows it; without one it's flat. It fades in over 0.1 s when added and out over 0.3 s when removed or when its
/// entity goes, and it pauses and slows with <see cref="GameTime.Scale"/>. Near and Far are the distances, in
/// metres, within which it's at full volume and beyond which it's silent.
/// </summary>
public record struct Sound(SoundRef Clip, float Volume = 1, Channel Channel = Channel.Effects, float Near = 2, float Far = 50);

/// <summary>
/// Makes an entity play an ambience for as long as it has this: it fades in over 2 s and out over 2 s. Put it on an
/// entity with <see cref="ScopedTo{TState}"/> and the ambience belongs to that state. It sits on
/// <see cref="Channel.Ambience"/> and ignores <see cref="GameTime.Scale"/>.
/// </summary>
public record struct Ambience(AmbienceRef Value, float Volume = 1);
