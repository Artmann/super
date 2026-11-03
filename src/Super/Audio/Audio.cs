using System.Numerics;

namespace Super;

/// <summary>
/// Plays sounds and music. Ask for it in a constructor, like <see cref="Content"/>. Calls are cheap and never wait:
/// they're queued and reach the mixer at the end of the frame.
/// </summary>
/// <remarks>
/// The game thread never touches the <see cref="Mixer"/>. It queues <see cref="AudioCommand"/>s, and whatever mixes
/// (the game loop itself in manual mode, an audio thread otherwise) drains them before each block. Voices and
/// ambiences get ids chosen here, on the game thread, so starting one returns at once; the mixer side maps them to
/// its own.
/// </remarks>
public sealed class Audio
{
    /// <summary>How many loops (music included) and ambiences the game can hold ids for at once.</summary>
    internal const int MaxVoiceIds = 1024;
    internal const int MaxAmbienceIds = 64;

    internal const float LoopFadeInSeconds = 0.1f;
    internal const float LoopFadeOutSeconds = 0.3f;
    internal const float AmbienceFadeSeconds = 2f;

    private const int ScratchFrames = 2048;
    private const int NoId = -1;

    // Game thread.
    private readonly Content _content;
    private readonly RandomStream _pitch;
    private readonly AudioCommands _commands = new();
    private readonly IdPool _voiceIds = new(MaxVoiceIds);
    private readonly IdPool _ambienceIds = new(MaxAmbienceIds);
    private readonly float[] _volumes = new float[Enum.GetValues<Channel>().Length];
    private SoundRef? _music;
    private int _musicVoice = NoId;
    private double _carriedFrames;

    // Mixer side: only whatever renders touches these. Indexed by the slot of a game id.
    private readonly Mixer _mixer;
    private readonly int[] _voiceGameIds = new int[MaxVoiceIds];
    private readonly int[] _mixerVoices = new int[MaxVoiceIds];
    private readonly int[] _ambienceGameIds = new int[MaxAmbienceIds];
    private readonly int[] _mixerAmbiences = new int[MaxAmbienceIds];
    private readonly float[] _scratch = new float[ScratchFrames * 2];
    private readonly PlayingSound[] _snapshot = new PlayingSound[Mixer.MaxVoices];

    /// <param name="content">Where clips and ambiences are named.</param>
    /// <param name="seed">
    /// Seeds everything random in audio: pitch variation here, ambience timing in the mixer. It's never the game's
    /// own <see cref="GameRandom"/>, so audio can't change how a seed plays out.
    /// </param>
    internal Audio(Content content, int seed)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        var random = new GameRandom(seed);
        _pitch = random.Stream("audio pitch");
        _mixer = new Mixer(random.Stream("audio"));
        Array.Fill(_volumes, 1f);
        Array.Fill(_voiceGameIds, NoId);
        Array.Fill(_mixerVoices, Mixer.Dropped);
        Array.Fill(_ambienceGameIds, NoId);
        Array.Fill(_mixerAmbiences, Mixer.NoAmbience);
    }

    /// <summary>
    /// True when the game loop drives the mixer, a tick at a time, as in tests and headless runs. Until there's an
    /// output device that's always.
    /// </summary>
    internal bool Manual { get; } = true;

    /// <summary>Commands dropped because the queue to the mixer was full.</summary>
    internal int DroppedCommands => _commands.Dropped;

    /// <summary>
    /// What the mixer is playing, once it has taken the commands handed over so far. Manual mode only, since otherwise
    /// the mixer belongs to another thread.
    /// </summary>
    internal IReadOnlyList<PlayingSound> Playing
    {
        get
        {
            if (!Manual)
            {
                throw new InvalidOperationException("What's playing can only be read when the game drives the audio, as in tests.");
            }

            Drain();
            int count = _mixer.Snapshot(_snapshot);
            return _snapshot.AsSpan(0, count).ToArray();
        }
    }

    /// <summary>
    /// Plays a clip once. Without <paramref name="at"/> it's flat, on <see cref="Channel.Interface"/>, and ignores the
    /// time scale. With it, it's placed in the world on <see cref="Channel.Effects"/>: full volume within
    /// <paramref name="near"/> metres of the camera, silent from <paramref name="far"/>, panned by where it is, and
    /// slowed and paused by <see cref="GameTime.Scale"/>.
    /// </summary>
    /// <param name="pitchVariation">How far the pitch may move up or down, from 0 up to 1. 0.05 is ±5%.</param>
    public void Play(
        SoundRef clip,
        Vector3? at = null,
        float volume = 1,
        Channel? channel = null,
        float pitchVariation = 0,
        float near = 2,
        float far = 50)
    {
        CheckVolume(volume, nameof(volume));
        if (!(pitchVariation >= 0 && pitchVariation < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchVariation), pitchVariation, "pitchVariation must be from 0 up to, but not including, 1.");
        }

        CheckPlace(at, near, far);
        Channel on = channel ?? (at.HasValue ? Channel.Effects : Channel.Interface);
        CheckChannel(on, nameof(channel));

        var start = new VoiceStart
        {
            Clip = _content.Clip(clip),
            Channel = on,
            Volume = volume,
            Pitch = pitchVariation > 0 ? 1 + _pitch.Between(-pitchVariation, pitchVariation) : 1,
            Position = at,
            Near = near,
            Far = far,
            FollowsScale = at.HasValue,
        };
        Send(new AudioCommand { Kind = AudioCommandKind.StartVoice, Id = NoId, Start = start });
    }

    /// <summary>
    /// Plays one music track at a time, looping, on <see cref="Channel.Music"/>. A new track cross-fades from the
    /// old one over <paramref name="fadeSeconds"/>. The track already playing does nothing, so calling this every
    /// tick is fine. Null fades the music out.
    /// </summary>
    public void Music(SoundRef? clip, float fadeSeconds = 2)
    {
        if (!(fadeSeconds >= 0) || !float.IsFinite(fadeSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(fadeSeconds), fadeSeconds, "fadeSeconds must be a number of at least zero.");
        }

        if (clip == _music)
        {
            return;
        }

        SoundClip? next = clip is SoundRef named ? _content.Clip(named) : null;
        if (_musicVoice != NoId)
        {
            Stop(_musicVoice, fadeSeconds);
            _musicVoice = NoId;
        }

        _music = clip;
        if (next is not null)
        {
            _musicVoice = Start(new VoiceStart
            {
                Clip = next,
                Channel = Channel.Music,
                Loop = true,
                IsMusic = true,
                FadeInSeconds = fadeSeconds,
            });
        }
    }

    /// <summary>Sets a channel's volume, from 0 to 1. Master scales every other channel.</summary>
    public void Volume(Channel channel, float volume)
    {
        CheckChannel(channel, nameof(channel));
        if (!(volume >= 0 && volume <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), volume, $"The {channel} volume must be from 0 to 1.");
        }

        _volumes[(int)channel] = volume;
        Send(new AudioCommand { Kind = AudioCommandKind.ChannelVolume, Channel = channel, Value = volume });
    }

    /// <summary>The volume last set for the channel. Each starts at 1.</summary>
    public float Volume(Channel channel)
    {
        CheckChannel(channel, nameof(channel));
        return _volumes[(int)channel];
    }

    /// <summary>
    /// Starts a looping voice that follows the time scale, fading in over 0.1 s, and returns its id at once.
    /// Placed when <paramref name="at"/> is given. Returns -1 when every id is taken, and calls on -1 do nothing.
    /// </summary>
    internal int StartLoop(SoundRef clip, float volume, Channel channel, Vector3? at, float near, float far)
    {
        CheckVolume(volume, nameof(volume));
        CheckChannel(channel, nameof(channel));
        CheckPlace(at, near, far);
        return Start(new VoiceStart
        {
            Clip = _content.Clip(clip),
            Channel = channel,
            Volume = volume,
            Loop = true,
            Position = at,
            Near = near,
            Far = far,
            FadeInSeconds = LoopFadeInSeconds,
            FollowsScale = true,
        });
    }

    internal void Move(int voice, Vector3 at)
    {
        if (!IsFinite(at))
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "at must be a finite position.");
        }

        if (_voiceIds.Holds(voice))
        {
            Send(new AudioCommand { Kind = AudioCommandKind.MoveVoice, Id = voice, Position = at });
        }
    }

    /// <summary>Fades the loop to the new volume over 0.1 s.</summary>
    internal void ChangeVolume(int voice, float volume)
    {
        CheckVolume(volume, nameof(volume));
        if (_voiceIds.Holds(voice))
        {
            Send(new AudioCommand { Kind = AudioCommandKind.VoiceVolume, Id = voice, Value = volume, FadeSeconds = LoopFadeInSeconds });
        }
    }

    /// <summary>Fades the loop out over 0.3 s. Its id is free again at once.</summary>
    internal void StopLoop(int voice)
    {
        Stop(voice, LoopFadeOutSeconds);
    }

    /// <summary>Starts an ambience, fading in over 2 s, and returns its id at once, or -1 when every id is taken.</summary>
    internal int StartAmbience(AmbienceRef ambience, float volume)
    {
        CheckVolume(volume, nameof(volume));
        AmbienceDefinition definition = _content.AmbienceOf(ambience);
        int id = _ambienceIds.Take();
        if (id != NoId)
        {
            Send(new AudioCommand { Kind = AudioCommandKind.StartAmbience, Id = id, Ambience = definition, Value = volume, FadeSeconds = AmbienceFadeSeconds });
        }

        return id;
    }

    /// <summary>Fades the ambience to the new volume over 2 s.</summary>
    internal void ChangeAmbienceVolume(int ambience, float volume)
    {
        CheckVolume(volume, nameof(volume));
        if (_ambienceIds.Holds(ambience))
        {
            Send(new AudioCommand { Kind = AudioCommandKind.AmbienceVolume, Id = ambience, Value = volume, FadeSeconds = AmbienceFadeSeconds });
        }
    }

    /// <summary>Fades the ambience out over 2 s. Its id is free again at once.</summary>
    internal void StopAmbience(int ambience)
    {
        if (_ambienceIds.Release(ambience))
        {
            Send(new AudioCommand { Kind = AudioCommandKind.StopAmbience, Id = ambience, FadeSeconds = AmbienceFadeSeconds });
        }
    }

    /// <summary>
    /// Ends the frame: sends the camera as the listener and the time scale, then hands over everything queued since
    /// the last hand-over.
    /// </summary>
    internal void EndFrame(Camera listener, float scale)
    {
        Send(new AudioCommand { Kind = AudioCommandKind.Listener, Position = listener.Position, Forward = listener.Target - listener.Position });
        Send(new AudioCommand { Kind = AudioCommandKind.WorldRate, Value = scale });
        _commands.Publish();
    }

    /// <summary>
    /// Manual mode only: hands over anything still queued, as a frame would, then mixes this many seconds. The
    /// fraction of a frame left over is carried to the next call, so a long run doesn't drift from the clock.
    /// </summary>
    internal void Advance(float seconds)
    {
        if (!Manual)
        {
            throw new InvalidOperationException("The audio can only be advanced by hand when the game drives it, as in tests.");
        }

        _commands.Publish();
        double exact = _carriedFrames + (double)seconds * Mixer.SampleRate;
        long frames = (long)Math.Round(exact);
        _carriedFrames = exact - frames;
        Drain();
        while (frames > 0)
        {
            int block = (int)Math.Min(frames, ScratchFrames);
            Render(_scratch.AsSpan(0, block * 2));
            frames -= block;
        }
    }

    /// <summary>Mixer side: takes the commands handed over so far, then mixes <c>stereo.Length / 2</c> frames.</summary>
    internal void Render(Span<float> stereo)
    {
        Drain();
        _mixer.Mix(stereo);
    }

    private void Drain()
    {
        while (_commands.TryPop(out AudioCommand command))
        {
            Apply(command);
        }
    }

    private void Apply(in AudioCommand command)
    {
        switch (command.Kind)
        {
            case AudioCommandKind.StartVoice:
                int voice = StartOnMixer(command.Start);
                if (command.Id != NoId)
                {
                    int slot = IdPool.Slot(command.Id);
                    _voiceGameIds[slot] = command.Id;
                    _mixerVoices[slot] = voice;
                }

                break;
            case AudioCommandKind.StopVoice:
                _mixer.Stop(MixerVoice(command.Id), command.FadeSeconds);
                Forget(_voiceGameIds, _mixerVoices, command.Id, Mixer.Dropped);
                break;
            case AudioCommandKind.VoiceVolume:
                _mixer.SetVolume(MixerVoice(command.Id), command.Value, command.FadeSeconds);
                break;
            case AudioCommandKind.MoveVoice:
                _mixer.SetPosition(MixerVoice(command.Id), command.Position);
                break;
            case AudioCommandKind.StartAmbience:
                int slotOfAmbience = IdPool.Slot(command.Id);
                _ambienceGameIds[slotOfAmbience] = command.Id;
                _mixerAmbiences[slotOfAmbience] = StartAmbienceOnMixer(command);
                break;
            case AudioCommandKind.AmbienceVolume:
                _mixer.SetAmbienceVolume(MixerAmbience(command.Id), command.Value, command.FadeSeconds);
                break;
            case AudioCommandKind.StopAmbience:
                _mixer.StopAmbience(MixerAmbience(command.Id), command.FadeSeconds);
                Forget(_ambienceGameIds, _mixerAmbiences, command.Id, Mixer.NoAmbience);
                break;
            case AudioCommandKind.ChannelVolume:
                _mixer.SetChannelVolume(command.Channel, command.Value);
                break;
            case AudioCommandKind.Listener:
                _mixer.SetListener(command.Position, command.Forward);
                break;
            case AudioCommandKind.WorldRate:
                _mixer.SetWorldRate(command.Value);
                break;
        }
    }

    /// <summary>
    /// A clip that fails to open costs only its own sound: the mixer counts it in <see cref="Mixer.FailedVoices"/>
    /// and later commands for it do nothing, as for a voice the limit dropped.
    /// </summary>
    private int StartOnMixer(in VoiceStart start)
    {
        try
        {
            return _mixer.Start(start);
        }
        catch (Exception problem)
        {
            _mixer.RecordFailure(problem);
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
            _mixer.RecordFailure(problem);
            return Mixer.NoAmbience;
        }
    }

    // The game id is checked as well as its slot, so a command whose start was dropped from a full queue can't reach
    // whatever an older id in the same slot started.
    private int MixerVoice(int id)
    {
        int slot = IdPool.Slot(id);
        return _voiceGameIds[slot] == id ? _mixerVoices[slot] : Mixer.Dropped;
    }

    private int MixerAmbience(int id)
    {
        int slot = IdPool.Slot(id);
        return _ambienceGameIds[slot] == id ? _mixerAmbiences[slot] : Mixer.NoAmbience;
    }

    private static void Forget(int[] gameIds, int[] mixerIds, int id, int none)
    {
        int slot = IdPool.Slot(id);
        if (gameIds[slot] == id)
        {
            gameIds[slot] = NoId;
            mixerIds[slot] = none;
        }
    }

    private int Start(in VoiceStart start)
    {
        int id = _voiceIds.Take();
        if (id != NoId)
        {
            Send(new AudioCommand { Kind = AudioCommandKind.StartVoice, Id = id, Start = start });
        }

        return id;
    }

    private void Stop(int voice, float fadeSeconds)
    {
        if (_voiceIds.Release(voice))
        {
            Send(new AudioCommand { Kind = AudioCommandKind.StopVoice, Id = voice, FadeSeconds = fadeSeconds });
        }
    }

    private void Send(in AudioCommand command)
    {
        _commands.Push(command);
    }

    private static void CheckVolume(float volume, string name)
    {
        if (!(volume >= 0) || !float.IsFinite(volume))
        {
            throw new ArgumentOutOfRangeException(name, volume, $"{name} must be a number of at least zero.");
        }
    }

    private static void CheckChannel(Channel channel, string name)
    {
        if ((uint)channel > (uint)Channel.Interface)
        {
            throw new ArgumentOutOfRangeException(name, channel, $"{name} must be one of the Channel values.");
        }
    }

    private static void CheckPlace(Vector3? at, float near, float far)
    {
        if (at is Vector3 position && !IsFinite(position))
        {
            throw new ArgumentOutOfRangeException(nameof(at), position, "at must be a finite position.");
        }

        if (!(near >= 0) || !float.IsFinite(near))
        {
            throw new ArgumentOutOfRangeException(nameof(near), near, "near must be a distance of at least zero.");
        }

        if (!(far > near) || !float.IsFinite(far))
        {
            throw new ArgumentOutOfRangeException(nameof(far), far, "far must be further than near.");
        }
    }

    private static bool IsFinite(Vector3 position)
    {
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }

    /// <summary>
    /// Ids handed out on the game thread: a slot in the low 16 bits and the slot's use count above, so an id kept after
    /// it was released can't reach the next user of the slot. Preallocated, so taking and releasing never allocate.
    /// </summary>
    private sealed class IdPool
    {
        private const int SlotBits = 16;
        private const int GenerationMask = int.MaxValue >> SlotBits;

        private readonly int[] _free;
        private readonly int[] _generations;
        private readonly bool[] _held;
        private int _freeCount;

        public IdPool(int capacity)
        {
            _free = new int[capacity];
            _generations = new int[capacity];
            _held = new bool[capacity];
            for (int i = 0; i < capacity; i++)
            {
                // Lowest slot first, which keeps ids small and easy to read in a debugger.
                _free[i] = capacity - 1 - i;
            }

            _freeCount = capacity;
        }

        public static int Slot(int id) => id & ((1 << SlotBits) - 1);

        /// <summary>A fresh id, or -1 when every slot is in use.</summary>
        public int Take()
        {
            if (_freeCount == 0)
            {
                return NoId;
            }

            int slot = _free[--_freeCount];
            int generation = (_generations[slot] + 1) & GenerationMask;
            if (generation == 0)
            {
                generation = 1;
            }

            _generations[slot] = generation;
            _held[slot] = true;
            return slot | (generation << SlotBits);
        }

        public bool Holds(int id)
        {
            if (id < 0)
            {
                return false;
            }

            int slot = Slot(id);
            return slot < _held.Length && _held[slot] && _generations[slot] == id >> SlotBits;
        }

        /// <summary>Frees the id and returns true, or returns false for an id that isn't held.</summary>
        public bool Release(int id)
        {
            if (!Holds(id))
            {
                return false;
            }

            int slot = Slot(id);
            _held[slot] = false;
            _free[_freeCount++] = slot;
            return true;
        }
    }
}
