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
    private readonly AudioCommands _commands = new();
    private readonly float[] _volumes = new float[Enum.GetValues<Channel>().Length];

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
        Mixing = new AudioMixing(random.Stream("audio"), _commands, drops);
        Device = new AudioDevice(Mixing);
        Loops = new AudioLoops(content, _commands, drops);
        Ambiences = new AudioAmbiences(content, _commands);
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
    internal AudioCommands Commands => _commands;

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
        AudioChecks.CheckVolume(volume, nameof(volume));
        if (!(pitchVariation >= 0 && pitchVariation < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchVariation), pitchVariation, "pitchVariation must be from 0 up to, but not including, 1.");
        }

        AudioChecks.CheckPlace(at, near, far);
        Channel on = channel ?? (at.HasValue ? Channel.Effects : Channel.Interface);
        AudioChecks.CheckChannel(on, nameof(channel));

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
        _commands.Push(new AudioCommand { Kind = AudioCommandKind.StartVoice, Id = AudioIdPool.NoId, Start = start });
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
        _commands.Push(new AudioCommand { Kind = AudioCommandKind.ChannelVolume, Channel = channel, Value = volume });
    }

    /// <summary>The volume last set for the channel. Each starts at 1.</summary>
    public float Volume(Channel channel)
    {
        AudioChecks.CheckChannel(channel, nameof(channel));
        return _volumes[(int)channel];
    }

    /// <summary>
    /// Ends the frame: sends the camera as the listener and the time scale, then hands over everything queued since
    /// the last hand-over.
    /// </summary>
    internal void EndFrame(Camera listener, float scale)
    {
        _commands.Push(new AudioCommand { Kind = AudioCommandKind.Listener, Position = listener.Position, Forward = listener.Target - listener.Position });
        _commands.Push(new AudioCommand { Kind = AudioCommandKind.WorldRate, Value = scale });
        _commands.Publish();
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

        if (!(seconds >= 0) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "seconds must be a number of at least zero.");
        }

        _commands.Publish();
        Device.MixFor(seconds);
    }
}
