namespace Super;

/// <summary>
/// Game thread: the ambiences the game holds an id for. Starting one picks its id here and returns at once; the start
/// reaches the mixer with the frame's other commands.
/// </summary>
internal sealed class AudioAmbiences
{
    /// <summary>How many ambiences the game can hold ids for at once.</summary>
    public const int MaxIds = 64;

    public const float FadeSeconds = 2f;

    private readonly Content _content;
    private readonly AudioCommands _commands;
    private readonly AudioIdPool _ids = new(MaxIds);

    public AudioAmbiences(Content content, AudioCommands commands)
    {
        _content = content;
        _commands = commands;
    }

    /// <summary>Starts an ambience, fading in over 2 s, and returns its id at once, or -1 when every id is taken.</summary>
    public int Start(AmbienceRef ambience, float volume)
    {
        AudioChecks.CheckVolume(volume, nameof(volume));
        AmbienceDefinition definition = _content.Sounds.AmbienceOf(ambience);
        int id = _ids.Take();
        if (id != AudioIdPool.NoId && !_commands.Push(new AudioCommand { Kind = AudioCommandKind.StartAmbience, Id = id, Ambience = definition, Value = volume, FadeSeconds = FadeSeconds }))
        {
            _ids.Release(id);
            return AudioIdPool.NoId;
        }

        return id;
    }

    /// <summary>Fades the ambience to the new volume over 2 s.</summary>
    public void ChangeVolume(int ambience, float volume)
    {
        AudioChecks.CheckVolume(volume, nameof(volume));
        if (_ids.Holds(ambience))
        {
            _commands.Push(new AudioCommand { Kind = AudioCommandKind.AmbienceVolume, Id = ambience, Value = volume, FadeSeconds = FadeSeconds });
        }
    }

    /// <summary>Fades the ambience out over 2 s. Its id is free again at once.</summary>
    public void Stop(int ambience)
    {
        if (_ids.Release(ambience))
        {
            _commands.Push(new AudioCommand { Kind = AudioCommandKind.StopAmbience, Id = ambience, FadeSeconds = FadeSeconds });
        }
    }
}
