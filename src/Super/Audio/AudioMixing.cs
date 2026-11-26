namespace Super;

/// <summary>
/// The mixer side of <see cref="Audio"/>: whatever mixes (the audio thread of a <see cref="DeviceOutput"/>, or the game
/// loop in manual mode) takes the game's commands from here and mixes. Only that one thread touches it at a time.
/// </summary>
internal sealed class AudioMixing
{
    private const int ScratchFrames = 2048;

    private readonly Mixer _mixer;
    private readonly AudioCommands _commands;
    private readonly VoiceDropReport _drops;
    private readonly MixCommandApplier _applier;
    private readonly float[] _scratch = new float[ScratchFrames * 2];
    private readonly PlayingSound[] _snapshot = new PlayingSound[Mixer.MaxVoices];
    private double _carriedFrames;

    /// <param name="random">Seeds ambience timing in the mixer. See <see cref="Mixer"/>.</param>
    public AudioMixing(RandomStream random, AudioCommands commands, VoiceDropReport drops)
    {
        _mixer = new Mixer(random);
        _commands = commands;
        _drops = drops;
        _applier = new MixCommandApplier(_mixer);
        _mixer.OnDropped = drops.MarkDropped;
    }

    /// <summary>The mixer's sample clock, frames mixed so far. Readable from any thread.</summary>
    public long Clock => _mixer.Clock;

    /// <summary>How many sounds failed as they started or played, and the latest message.</summary>
    public MixFailures Failures => _mixer.Failures;

    /// <summary>Takes the commands handed over so far, then mixes <c>stereo.Length / 2</c> frames.</summary>
    public void Render(Span<float> stereo)
    {
        Drain();
        _mixer.Mix(stereo);
        _drops.Mixed(_mixer.FreeVoices);
    }

    /// <summary>
    /// Mixes this many seconds, a block at a time, writing each block to <paramref name="recording"/> if there is one.
    /// The fraction of a frame left over is carried to the next call, so a long run doesn't drift from the clock.
    /// </summary>
    public void MixFor(float seconds, AudioRecording? recording)
    {
        double exact = _carriedFrames + (double)seconds * Mixer.SampleRate;
        long frames = (long)Math.Round(exact);
        _carriedFrames = exact - frames;
        Drain();
        while (frames > 0)
        {
            int block = (int)Math.Min(frames, ScratchFrames);
            Span<float> mixed = _scratch.AsSpan(0, block * 2);
            Render(mixed);
            recording?.Write(mixed);
            frames -= block;
        }
    }

    /// <summary>What the mixer is playing, once it has taken the commands handed over so far.</summary>
    public PlayingSound[] Playing()
    {
        Drain();
        int count = _mixer.Snapshot(_snapshot);
        return _snapshot.AsSpan(0, count).ToArray();
    }

    /// <summary>Takes the commands handed over so far and throws them away, for when nothing mixes any more.</summary>
    public void Discard()
    {
        while (_commands.TryPop(out _))
        {
        }
    }

    /// <summary>Ends every sound, so streamed clips close their readers.</summary>
    public void StopAll()
    {
        _mixer.StopAll();
    }

    private void Drain()
    {
        while (_commands.TryPop(out AudioCommand command))
        {
            _applier.Apply(command);
        }
    }
}

/// <summary>
/// Applies the game's commands to the mixer, mapping the game's voice and ambience ids to the mixer's own. A clip that
/// fails to open costs only its own sound: the mixer counts it in <see cref="Mixer.Failures"/> and later commands for it
/// do nothing, as for a voice the limit dropped.
/// </summary>
internal sealed class MixCommandApplier
{
    private readonly Mixer _mixer;
    private readonly MixIdMap _voices = new(AudioLoops.MaxIds, Mixer.Dropped);
    private readonly MixIdMap _ambiences = new(AudioAmbiences.MaxIds, Mixer.NoAmbience);

    public MixCommandApplier(Mixer mixer)
    {
        _mixer = mixer;
    }

    public void Apply(in AudioCommand command)
    {
        if (ApplyToVoice(command) || ApplyToAmbience(command))
        {
            return;
        }

        ApplyToListening(command);
    }

    private bool ApplyToVoice(in AudioCommand command)
    {
        switch (command.Kind)
        {
            case AudioCommandKind.StartVoice:
                StartVoice(command);
                return true;
            case AudioCommandKind.StopVoice:
                _mixer.Stop(_voices.MixerId(command.Id), command.FadeSeconds);
                _voices.Forget(command.Id);
                return true;
            case AudioCommandKind.VoiceVolume:
                _mixer.SetVolume(_voices.MixerId(command.Id), command.Value, command.FadeSeconds);
                return true;
            case AudioCommandKind.MoveVoice:
                _mixer.SetPosition(_voices.MixerId(command.Id), command.Position, command.Velocity, command.Value);
                return true;
            default:
                return false;
        }
    }

    private bool ApplyToAmbience(in AudioCommand command)
    {
        switch (command.Kind)
        {
            case AudioCommandKind.StartAmbience:
                StartAmbience(command);
                return true;
            case AudioCommandKind.AmbienceVolume:
                _mixer.SetAmbienceVolume(_ambiences.MixerId(command.Id), command.Value, command.FadeSeconds);
                return true;
            case AudioCommandKind.StopAmbience:
                _mixer.StopAmbience(_ambiences.MixerId(command.Id), command.FadeSeconds);
                _ambiences.Forget(command.Id);
                return true;
            default:
                return false;
        }
    }

    private void ApplyToListening(in AudioCommand command)
    {
        switch (command.Kind)
        {
            case AudioCommandKind.ChannelVolume:
                _mixer.Listening.SetChannelVolume(command.Channel, command.Value);
                break;
            case AudioCommandKind.Listener:
                _mixer.Listening.SetListener(command.Position, command.Forward, command.Velocity);
                break;
            case AudioCommandKind.WorldRate:
                _mixer.Listening.SetWorldRate(command.Value);
                break;
            case AudioCommandKind.DopplerScale:
                _mixer.Listening.SetDopplerScale(command.Value);
                break;
            case AudioCommandKind.DuckRule:
                _mixer.Listening.Ducks.SetRule(command.Channel, command.By, command.Value, command.FadeSeconds, command.ReleaseSeconds);
                break;
            case AudioCommandKind.DuckFor:
                _mixer.Listening.Ducks.DuckFor(command.Channel, command.Value, command.HoldSeconds, command.FadeSeconds, command.ReleaseSeconds);
                break;
        }
    }

    private void StartVoice(in AudioCommand command)
    {
        if (command.Id == AudioIdPool.NoId)
        {
            StartOnMixer(command.Start);
            return;
        }

        // The game frees an id when it sends the stop, so a slot still holding a game id here means that
        // stop was dropped from a full queue. Stopping the old voice now keeps it from playing forever.
        if (_voices.TryStale(command.Id, out int stale))
        {
            _mixer.Stop(stale, AudioLoops.FadeOutSeconds);
        }

        VoiceStart tagged = command.Start;
        tagged.Tag = command.Id;
        _voices.Bind(command.Id, StartOnMixer(tagged));
    }

    private void StartAmbience(in AudioCommand command)
    {
        // As for voices: a game id still here means its stop was dropped.
        if (_ambiences.TryStale(command.Id, out int stale))
        {
            _mixer.StopAmbience(stale, AudioLoops.FadeOutSeconds);
        }

        _ambiences.Bind(command.Id, StartAmbienceOnMixer(command));
    }

    private int StartOnMixer(in VoiceStart start)
    {
        try
        {
            return _mixer.Start(start);
        }
        catch (Exception problem)
        {
            _mixer.Failures.Tally(problem);
            return Mixer.Dropped;
        }
    }

    private int StartAmbienceOnMixer(in AudioCommand command)
    {
        try
        {
            return _mixer.StartAmbience(command.Ambience!, command.Value, command.FadeSeconds);
        }
        catch (Exception problem)
        {
            _mixer.Failures.Tally(problem);
            return Mixer.NoAmbience;
        }
    }
}

/// <summary>
/// Mixer side, per game id slot: the game id last started there and the mixer id it got. The game id is checked as
/// well as its slot, so a command whose start was dropped from a full queue can't reach whatever an older id in the
/// same slot started.
/// </summary>
internal sealed class MixIdMap
{
    private readonly int[] _gameIds;
    private readonly int[] _mixerIds;
    private readonly int _none;

    /// <param name="none">The mixer id for a game id that has none: the mixer refused it, or it was never started.</param>
    public MixIdMap(int slots, int none)
    {
        _gameIds = new int[slots];
        _mixerIds = new int[slots];
        _none = none;
        Array.Fill(_gameIds, AudioIdPool.NoId);
        Array.Fill(_mixerIds, none);
    }

    /// <summary>The mixer id this game id got, or the none id if its slot has moved on.</summary>
    public int MixerId(int id)
    {
        int slot = AudioIdPool.Slot(id);
        return _gameIds[slot] == id ? _mixerIds[slot] : _none;
    }

    /// <summary>True when the id's slot still holds an earlier game id, with the mixer id that one got.</summary>
    public bool TryStale(int id, out int mixerId)
    {
        int slot = AudioIdPool.Slot(id);
        mixerId = _mixerIds[slot];
        return _gameIds[slot] != AudioIdPool.NoId;
    }

    public void Bind(int id, int mixerId)
    {
        int slot = AudioIdPool.Slot(id);
        _gameIds[slot] = id;
        _mixerIds[slot] = mixerId;
    }

    /// <summary>Frees the id's slot, if this id still holds it.</summary>
    public void Forget(int id)
    {
        int slot = AudioIdPool.Slot(id);
        if (_gameIds[slot] == id)
        {
            _gameIds[slot] = AudioIdPool.NoId;
            _mixerIds[slot] = _none;
        }
    }
}
