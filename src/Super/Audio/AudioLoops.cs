using System.Numerics;

namespace Super;

/// <summary>What <see cref="AudioLoops.Start"/> plays. Volume defaults to 1, near to 2 m and far to 50 m.</summary>
internal readonly struct SoundLoop
{
    public SoundLoop()
    {
    }

    public SoundRef Clip { get; init; }

    public float Volume { get; init; } = 1;

    public Channel Channel { get; init; }

    /// <summary>Where the loop is in the world, or null for a flat loop.</summary>
    public Vector3? At { get; init; }

    public float Near { get; init; } = 2;

    public float Far { get; init; } = 50;

    /// <summary>How much a placed loop's pitch follows the Doppler effect: 0 not at all, 1 as in air.</summary>
    public float Doppler { get; init; }
}

/// <summary>
/// Game thread: the looping voices the game holds an id for, music among them. Starting one picks its id here and
/// returns at once; the start reaches the mixer with the frame's other commands.
/// </summary>
internal sealed class AudioLoops
{
    /// <summary>How many loops (music included) the game can hold ids for at once.</summary>
    public const int MaxIds = 1024;

    public const float FadeInSeconds = 0.1f;
    public const float FadeOutSeconds = 0.3f;

    private const int NoId = AudioIdPool.NoId;

    private readonly Content _content;
    private readonly AudioCommands _commands;
    private readonly VoiceDropReport _drops;
    private readonly AudioIdPool _ids = new(MaxIds);
    private SoundRef? _music;
    private int _musicVoice = NoId;

    public AudioLoops(Content content, AudioCommands commands, VoiceDropReport drops)
    {
        _content = content;
        _commands = commands;
        _drops = drops;
    }

    /// <summary>
    /// Starts a looping voice that follows the time scale, fading in over 0.1 s, and returns its id at once.
    /// Placed when <see cref="SoundLoop.At"/> is given. Returns -1 when every id is taken, and calls on -1 do nothing.
    /// </summary>
    public int Start(in SoundLoop loop)
    {
        AudioChecks.CheckVolume(loop.Volume, "volume");
        AudioChecks.CheckChannel(loop.Channel, "channel");
        AudioChecks.CheckPlace(loop.At, loop.Near, loop.Far);
        AudioChecks.CheckDoppler(loop.Doppler, "doppler");
        return StartVoice(new VoiceStart
        {
            Clip = _content.Sounds.ClipOf(loop.Clip),
            Channel = loop.Channel,
            Volume = loop.Volume,
            Loop = true,
            Position = loop.At,
            Near = loop.Near,
            Far = loop.Far,
            FadeInSeconds = FadeInSeconds,
            FollowsScale = true,
            Doppler = loop.Doppler,
        });
    }

    /// <summary>Moves a placed loop, giving how fast it moves and how much its pitch follows the Doppler effect.</summary>
    public void Move(int voice, Vector3 at, Vector3 velocity, float doppler)
    {
        if (!AudioChecks.IsFinite(at))
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "at must be a finite position.");
        }

        AudioChecks.CheckDoppler(doppler, nameof(doppler));
        if (_ids.Holds(voice))
        {
            _commands.Push(new AudioCommand { Kind = AudioCommandKind.MoveVoice, Id = voice, Position = at, Velocity = velocity, Value = doppler });
        }
    }

    /// <summary>Fades the loop to the new volume over 0.1 s.</summary>
    public void ChangeVolume(int voice, float volume)
    {
        AudioChecks.CheckVolume(volume, nameof(volume));
        if (_ids.Holds(voice))
        {
            _commands.Push(new AudioCommand { Kind = AudioCommandKind.VoiceVolume, Id = voice, Value = volume, FadeSeconds = FadeInSeconds });
        }
    }

    /// <summary>Fades the loop out over 0.3 s. Its id is free again at once.</summary>
    public void Stop(int voice)
    {
        StopVoice(voice, FadeOutSeconds);
    }

    /// <summary>
    /// True when the voice limit dropped this loop, at its start or later for a louder sound, so it is silent though
    /// its id is still held. Stop it and start it again to bring it back, once <see cref="TryClaimVoice"/> agrees.
    /// </summary>
    public bool WasDropped(int voice)
    {
        return _ids.Holds(voice) && _drops.WasDropped(voice);
    }

    /// <summary>See <see cref="VoiceDropReport.TryClaimVoice"/>.</summary>
    public bool TryClaimVoice() => _drops.TryClaimVoice();

    /// <summary>See <see cref="Audio.Music"/>.</summary>
    public void PlayMusic(SoundRef? clip, float fadeSeconds)
    {
        if (!(fadeSeconds >= 0) || !float.IsFinite(fadeSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(fadeSeconds), fadeSeconds, "fadeSeconds must be a number of at least zero.");
        }

        if (clip == _music)
        {
            return;
        }

        SoundClip? next = clip is SoundRef named ? _content.Sounds.ClipOf(named) : null;
        if (_musicVoice != NoId)
        {
            StopVoice(_musicVoice, fadeSeconds);
            _musicVoice = NoId;
        }

        _music = null;
        if (next is not null)
        {
            _musicVoice = StartVoice(new VoiceStart
            {
                Clip = next,
                Channel = Channel.Music,
                Loop = true,
                IsMusic = true,
                FadeInSeconds = fadeSeconds,
            });

            // A start that couldn't be sent leaves no track playing, so asking for it again tries again.
            if (_musicVoice != NoId)
            {
                _music = clip;
            }
        }
    }

    /// <summary>A fresh id for the voice, or -1 when every id is taken or the queue is full.</summary>
    private int StartVoice(in VoiceStart start)
    {
        int id = _ids.Take();
        if (id == NoId)
        {
            return NoId;
        }

        _drops.Forget(id);
        if (!_commands.Push(new AudioCommand { Kind = AudioCommandKind.StartVoice, Id = id, Start = start }))
        {
            _ids.Release(id);
            return NoId;
        }

        return id;
    }

    private void StopVoice(int voice, float fadeSeconds)
    {
        if (_ids.Release(voice))
        {
            _commands.Push(new AudioCommand { Kind = AudioCommandKind.StopVoice, Id = voice, FadeSeconds = fadeSeconds });
        }
    }
}

/// <summary>
/// What the mixer side tells the game thread about the voice limit: which loops it dropped, and how many voice slots
/// were free after the latest mix, with a count of mixes so the game can tell a fresh figure from one it has used.
/// </summary>
internal sealed class VoiceDropReport
{
    private const int NoId = AudioIdPool.NoId;

    // Both sides. Per id slot, the game id of a loop the voice limit dropped: written by the mixer side, cleared by
    // the game when it takes the slot again.
    private readonly int[] _droppedIds;
    private int _freeVoices = Mixer.MaxVoices;
    private int _mixes;

    // Game thread: the mix whose free slots it last took, and how many of those are left to claim.
    private int _claimedMix = -1;
    private int _claimable;

    public VoiceDropReport(int ids)
    {
        _droppedIds = new int[ids];
        Array.Fill(_droppedIds, NoId);
    }

    /// <summary>Mixer side: notes a loop the voice limit dropped.</summary>
    public void MarkDropped(int voice)
    {
        Volatile.Write(ref _droppedIds[AudioIdPool.Slot(voice)], voice);
    }

    /// <summary>Mixer side: after a mix, publishes how many voice slots are free.</summary>
    public void Mixed(int freeVoices)
    {
        Volatile.Write(ref _freeVoices, freeVoices);
        Volatile.Write(ref _mixes, _mixes + 1);
    }

    /// <summary>
    /// Game thread, as it takes the id: a drop noted for an older id in this slot is a different id, but its
    /// generation could wrap round to this one.
    /// </summary>
    public void Forget(int id)
    {
        Volatile.Write(ref _droppedIds[AudioIdPool.Slot(id)], NoId);
    }

    /// <summary>Game thread: true when the mixer side noted this id as dropped.</summary>
    public bool WasDropped(int id)
    {
        return Volatile.Read(ref _droppedIds[AudioIdPool.Slot(id)]) == id;
    }

    /// <summary>
    /// Claims one of the voice slots that were free after the latest mix and returns true, or returns false when none
    /// is left. Each free slot is claimed once until the mixer mixes again, so dropped loops come back as slots free
    /// up rather than all asking every frame.
    /// </summary>
    public bool TryClaimVoice()
    {
        int mixes = Volatile.Read(ref _mixes);
        if (mixes != _claimedMix)
        {
            _claimedMix = mixes;
            _claimable = Volatile.Read(ref _freeVoices);
        }

        if (_claimable <= 0)
        {
            return false;
        }

        _claimable--;
        return true;
    }
}
