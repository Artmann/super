using System.Numerics;

namespace Super;

/// <summary>
/// Plays sounds and music. Ask for it in a constructor, like <see cref="Content"/>. Calls are cheap and never wait:
/// they're queued and reach the mixer at the end of the frame.
/// </summary>
/// <remarks>
/// The game thread never touches the <see cref="Mixer"/>. It queues <see cref="AudioCommand"/>s, and whatever mixes
/// (the game loop itself in manual mode, the audio thread of a <see cref="DeviceOutput"/> otherwise) takes them through
/// <see cref="Mixing"/> before each block. Loops and ambiences get ids chosen on the game thread
/// (<see cref="Loops"/> and <see cref="Ambiences"/>), so starting one returns at once; the mixer side maps them to its
/// own. <see cref="Device"/> says who mixes.
/// </remarks>
public sealed class Audio
{
    private readonly Content _content;
    private readonly RandomStream _pitch;
    private readonly float[] _volumes = new float[Enum.GetValues<Channel>().Length];
    private HeardMotion _listenerMotion;
    private float _dopplerScale = 1;

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
        var drops = new VoiceDropReport(AudioLoops.MaxIds);
        Mixing = new AudioMixing(random.Stream("audio"), Commands, drops);
        Device = new AudioDevice(Mixing);
        Loops = new AudioLoops(content, Commands, drops);
        Ambiences = new AudioAmbiences(content, Commands);
        Array.Fill(_volumes, 1f);
    }

    /// <summary>Game thread: looping voices, which the game holds an id for.</summary>
    internal AudioLoops Loops { get; }

    /// <summary>Game thread: ambiences, which the game holds an id for.</summary>
    internal AudioAmbiences Ambiences { get; }

    /// <summary>Game thread: who mixes, and the WAV recording.</summary>
    internal AudioDevice Device { get; }

    /// <summary>Mixer side: only whatever mixes may call it, apart from its clock.</summary>
    internal AudioMixing Mixing { get; }

    /// <summary>The queue to the mixer. Tests read its counts to see what a frame sent.</summary>
    internal AudioCommands Commands { get; } = new();

    /// <summary>
    /// What the mixer is playing, once it has taken the commands handed over so far. Manual mode only, since otherwise
    /// the mixer belongs to another thread.
    /// </summary>
    internal IReadOnlyList<PlayingSound> Playing
    {
        get
        {
            if (!Device.Manual)
            {
                throw new InvalidOperationException("What's playing can only be read when the game drives the audio, as in tests.");
            }

            return Mixing.Playing();
        }
    }

    /// <summary>
    /// Plays a clip once. Without <paramref name="at"/> it's flat, on <see cref="Channel.Interface"/>, and ignores the
    /// time scale. With it, it's placed in the world on <see cref="Channel.Effects"/>: full volume within
    /// <paramref name="near"/> metres of the camera, silent from <paramref name="far"/>, panned by where it is, and
    /// slowed and paused by <see cref="GameTime.Scale"/>. A placed sound stays where it started, but its pitch still
    /// follows the camera's own movement through the Doppler effect.
    /// </summary>
    /// <param name="pitchVariation">How far the pitch may move up or down, from 0 up to 1. 0.05 is ±5%.</param>
    /// <param name="doppler">How much a placed sound's pitch follows the Doppler effect: 0 not at all, 1 as in air.</param>
    public void Play(
        SoundRef clip,
        Vector3? at = null,
        float volume = 1,
        Channel? channel = null,
        float pitchVariation = 0,
        float near = 2,
        float far = 50,
        float doppler = 1)
    {
        AudioChecks.CheckVolume(volume, nameof(volume));
        AudioChecks.CheckDoppler(doppler, nameof(doppler));
        if (!(pitchVariation >= 0 && pitchVariation < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchVariation), pitchVariation, "pitchVariation must be from 0 up to, but not including, 1.");
        }

        AudioChecks.CheckPlace(at, near, far);
        Channel on = channel ?? (at.HasValue ? Channel.Effects : Channel.Interface);
        AudioChecks.CheckChannel(on, nameof(channel));

        var start = new VoiceStart
        {
            Clip = _content.Sounds.ClipOf(clip),
            Channel = on,
            Volume = volume,
            Pitch = pitchVariation > 0 ? 1 + _pitch.Between(-pitchVariation, pitchVariation) : 1,
            Position = at,
            Near = near,
            Far = far,
            FollowsScale = at.HasValue,
            Doppler = doppler,
        };
        Commands.Push(new AudioCommand { Kind = AudioCommandKind.StartVoice, Id = AudioIdPool.NoId, Start = start });
    }

    /// <summary>
    /// Plays one music track at a time, looping, on <see cref="Channel.Music"/>. A new track cross-fades from the
    /// old one over <paramref name="fadeSeconds"/>. The track already playing does nothing, so calling this every
    /// tick is fine. Null fades the music out.
    /// </summary>
    public void Music(SoundRef? clip, float fadeSeconds = 2)
    {
        Loops.PlayMusic(clip, fadeSeconds);
    }

    /// <summary>Sets a channel's volume, from 0 to 1. Master scales every other channel.</summary>
    public void Volume(Channel channel, float volume)
    {
        AudioChecks.CheckChannel(channel, nameof(channel));
        if (!(volume >= 0 && volume <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), volume, $"The {channel} volume must be from 0 to 1.");
        }

        _volumes[(int)channel] = volume;
        Commands.Push(new AudioCommand { Kind = AudioCommandKind.ChannelVolume, Channel = channel, Value = volume });
    }

    /// <summary>The volume last set for the channel. Each starts at 1.</summary>
    public float Volume(Channel channel)
    {
        AudioChecks.CheckChannel(channel, nameof(channel));
        return _volumes[(int)channel];
    }

    /// <summary>
    /// How strong the Doppler effect is for every placed sound, on top of each one's own amount: 1 as in air, 0 off,
    /// and above 1 for a cartoonish swoop. It starts at 1.
    /// </summary>
    public float Doppler
    {
        get => _dopplerScale;
        set
        {
            AudioChecks.CheckDoppler(value, nameof(Doppler));
            _dopplerScale = value;
            Commands.Push(new AudioCommand { Kind = AudioCommandKind.DopplerScale, Value = value });
        }
    }

    /// <summary>
    /// Dips <paramref name="target"/> by <paramref name="decibels"/> while any sound plays on <paramref name="by"/>,
    /// going down over <paramref name="attack"/> seconds and coming back over <paramref name="release"/> once the
    /// last one ends. It replaces the rule for that pair, and 0 dB removes it. Music and Ambience start out ducked
    /// under <see cref="Channel.Dialogue"/> by 10 and 6 dB.
    /// </summary>
    /// <param name="decibels">How far down, 0 or below. -6 is about half as loud, -10 about a third.</param>
    public void Duck(Channel target, Channel by, float decibels = -10, float attack = Ducking.AttackSeconds, float release = Ducking.ReleaseSeconds)
    {
        AudioChecks.CheckDuckChannel(target, nameof(target));
        AudioChecks.CheckDuckChannel(by, nameof(by));
        if (target == by)
        {
            throw new ArgumentException($"A channel can't duck under itself, but both target and by are {target}.", nameof(by));
        }

        AudioChecks.CheckDuck(decibels, attack, release);
        Commands.Push(new AudioCommand
        {
            Kind = AudioCommandKind.DuckRule,
            Channel = target,
            By = by,
            Value = decibels,
            FadeSeconds = attack,
            ReleaseSeconds = release,
        });
    }

    /// <summary>
    /// Dips a channel by <paramref name="decibels"/> for a moment, such as under a boss's roar: it goes down over
    /// <paramref name="attack"/> seconds, stays down until <paramref name="seconds"/> have passed since the call, then
    /// comes back over <paramref name="release"/>. Calling it again while one is under way keeps the deeper dip and
    /// the later end.
    /// </summary>
    public void DuckFor(Channel channel, float decibels, float seconds, float attack = Ducking.AttackSeconds, float release = Ducking.ReleaseSeconds)
    {
        AudioChecks.CheckDuckChannel(channel, nameof(channel));
        AudioChecks.CheckDuck(decibels, attack, release);
        AudioChecks.CheckSeconds(seconds, nameof(seconds));
        Commands.Push(new AudioCommand
        {
            Kind = AudioCommandKind.DuckFor,
            Channel = channel,
            Value = decibels,
            HoldSeconds = seconds,
            FadeSeconds = attack,
            ReleaseSeconds = release,
        });
    }

    /// <summary>
    /// Ends the frame: sends the camera as the listener, with how fast it moved up to <paramref name="now"/> (the
    /// time on screen, in seconds), and the time scale, then hands over everything queued since the last hand-over.
    /// </summary>
    internal void EndFrame(Camera listener, float scale, double now)
    {
        Vector3 velocity = _listenerMotion.Update(listener.Position, now);
        Commands.Push(new AudioCommand
        {
            Kind = AudioCommandKind.Listener,
            Position = listener.Position,
            Forward = listener.Target - listener.Position,
            Velocity = velocity,
        });
        Commands.Push(new AudioCommand { Kind = AudioCommandKind.WorldRate, Value = scale });
        Commands.Publish();
    }

    /// <summary>
    /// Manual mode only: hands over anything still queued, as a frame would, then mixes this many seconds. The
    /// fraction of a frame left over is carried to the next call, so a long run doesn't drift from the clock.
    /// </summary>
    internal void Advance(float seconds)
    {
        if (!Device.Manual)
        {
            throw new InvalidOperationException("The audio can only be advanced by hand when the game drives it, as in tests.");
        }

        AudioChecks.CheckSeconds(seconds, nameof(seconds));
        Commands.Publish();
        Device.MixFor(seconds);
    }
}
