namespace Super;

/// <summary>Names a sound clip. <see cref="ContentSounds.Sound"/> hands these out.</summary>
public readonly record struct SoundRef(int Index);

/// <summary>Names an ambience. <see cref="ContentSounds.Ambience"/> hands these out.</summary>
public readonly record struct AmbienceRef(int Index);
