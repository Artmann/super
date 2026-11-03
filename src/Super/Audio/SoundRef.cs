namespace Super;

/// <summary>Names a sound clip. <see cref="Content.Sound"/> hands these out.</summary>
public readonly record struct SoundRef(int Index);

/// <summary>Names an ambience. <see cref="Content.Ambience"/> hands these out.</summary>
public readonly record struct AmbienceRef(int Index);
