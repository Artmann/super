namespace Super;

/// <summary>The sounds and ambiences a game named, each read once. Reached through the content's <c>Sounds</c>.</summary>
public sealed class ContentSounds
{
    private readonly IContentFiles _files;
    private readonly Func<string, byte[], Func<string, SoundClip>, AmbienceDefinition> _readAmbience;
    private readonly List<SoundClip> _clips = [];
    private readonly Dictionary<string, SoundRef> _sounds = [];
    private readonly List<AmbienceDefinition> _ambiences = [];
    private readonly Dictionary<string, AmbienceRef> _ambienceRefs = [];

    /// <param name="files">Where the cooked sounds and ambiences are read from.</param>
    /// <param name="readAmbience">Reads an ambience file's bytes, given its path, reading each clip it names by path.</param>
    internal ContentSounds(IContentFiles files, Func<string, byte[], Func<string, SoundClip>, AmbienceDefinition> readAmbience)
    {
        _files = files;
        _readAmbience = readAmbience;
    }

    /// <summary>
    /// Names a cooked sound by its name, such as <c>sounds/hit.wav</c>. Naming it again gives
    /// the same one. A name that isn't a cooked sound, or can't be read, stops here with a
    /// <see cref="ContentException"/> naming it.
    /// </summary>
    public SoundRef Sound(string name)
    {
        if (!_sounds.TryGetValue(name, out var sound))
        {
            ContentNames.Require(_files, name, ContentKind.Sound);
            _clips.Add(SoundClip.Read(name, CookedBytes.Read(_files, name, "sound")));
            sound = new SoundRef(_clips.Count - 1);
            _sounds.Add(name, sound);
        }

        return sound;
    }

    /// <summary>
    /// Names an ambience by the name of its .ambience.json file. Naming it again gives the
    /// same one. Any mistake in the file, or in a sound it names, stops here with a
    /// <see cref="ContentException"/> naming the file.
    /// </summary>
    public AmbienceRef Ambience(string name)
    {
        if (!_ambienceRefs.TryGetValue(name, out var ambience))
        {
            ContentNames.Require(_files, name, ContentKind.Data);
            _ambiences.Add(_readAmbience(name, CookedBytes.Read(_files, name, "ambience"), clipPath => ClipOf(Sound(clipPath))));
            ambience = new AmbienceRef(_ambiences.Count - 1);
            _ambienceRefs.Add(name, ambience);
        }

        return ambience;
    }

    /// <summary>The clip a sound names.</summary>
    internal SoundClip ClipOf(SoundRef sound) => _clips[sound.Index];

    /// <summary>Names a clip made in code, for tests whose clip has to fail or be watched as it plays.</summary>
    internal SoundRef AddClip(SoundClip clip)
    {
        _clips.Add(clip);
        return new SoundRef(_clips.Count - 1);
    }

    /// <summary>The ambience a ref names.</summary>
    internal AmbienceDefinition AmbienceOf(AmbienceRef ambience) => _ambiences[ambience.Index];
}
