using System.Numerics;

namespace Super;

/// <summary>
/// Mixes every playing voice into 48 kHz stereo floats, with channel volumes, fades, pitch, distance falloff and
/// panning from the listener. It allocates nothing while mixing.
/// </summary>
/// <remarks>
/// A voice id carries its slot in the low bits and the slot's start count above them, so an id kept after its voice
/// ended can't reach the next sound in that slot. Calls on unknown, ended or <see cref="Dropped"/> ids do nothing.
/// </remarks>
internal sealed class Mixer
{
    public const int SampleRate = 48000;
    public const int MaxVoices = 64;

    /// <summary>How many ambiences can play at once.</summary>
    public const int MaxAmbiences = 8;

    /// <summary>The id <see cref="StartAmbience"/> returns when all <see cref="MaxAmbiences"/> are already playing.</summary>
    public const int NoAmbience = -1;

    /// <summary>The id <see cref="Start"/> returns when the voice limit dropped the new sound.</summary>
    public const int Dropped = -1;

    private const int SlotBits = 8;
    private const int SlotMask = (1 << SlotBits) - 1;
    private const int GenerationMask = int.MaxValue >> SlotBits;

    private readonly Voice[] _voices = new Voice[MaxVoices];
    private readonly int[] _generations = new int[MaxVoices];
    private readonly AmbiencePlayer[] _ambiences = new AmbiencePlayer[MaxAmbiences];
    private readonly int[] _ambienceGenerations = new int[MaxAmbiences];
    private readonly RandomStream _random;
    private readonly float[] _channelVolumes = new float[Enum.GetValues<Channel>().Length];
    private Vector3 _listener;
    private Vector3 _forward = -Vector3.UnitZ;
    private float _worldRate = 1;
    private long _clock;

    /// <summary>
    /// Makes a mixer. <paramref name="random"/> picks everything random in ambiences: where layers start and when,
    /// which and how loud scattered sounds play. Pass <c>new GameRandom(seed).Stream("audio")</c>, never the game's own.
    /// </summary>
    public Mixer(RandomStream random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
        for (int i = 0; i < MaxVoices; i++)
        {
            _voices[i] = new Voice();
        }

        for (int i = 0; i < MaxAmbiences; i++)
        {
            _ambiences[i] = new AmbiencePlayer();
        }

        Array.Fill(_channelVolumes, 1f);
    }

    /// <summary>How many voices have ended because their clip's reader threw, or scattered sounds couldn't start.</summary>
    public int FailedVoices { get; private set; }

    /// <summary>The message of the latest of those failures, for a log, or null if there has been none.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Called with the <see cref="VoiceStart.Tag"/> of a tagged voice the voice limit dropped: one refused at its start,
    /// or one cut for a newer voice. Not called for voices that end or fail. Runs on whatever thread mixes.
    /// </summary>
    public Action<int>? OnDropped { get; set; }

    /// <summary>How many voice slots are free.</summary>
    public int FreeVoices
    {
        get
        {
            int free = 0;
            for (int i = 0; i < MaxVoices; i++)
            {
                if (!_voices[i].Active)
                {
                    free++;
                }
            }

            return free;
        }
    }

    /// <summary>
    /// Frames mixed so far: the mixer's sample clock. Unlike the rest of the mixer it may be read from any thread, so
    /// a test can watch an audio thread mix.
    /// </summary>
    public long Clock => Volatile.Read(ref _clock);

    /// <summary>
    /// Starts a voice and returns its id. With every slot taken, the quietest voice that isn't music or an ambience layer is
    /// dropped; if the new one would be quieter than all of them, or every voice is protected, the new one is dropped and
    /// <see cref="Dropped"/> comes back.
    /// </summary>
    public int Start(in VoiceStart start)
    {
        ArgumentNullException.ThrowIfNull(start.Clip, "start.Clip");

        int slot = FreeSlot();
        int cut = -1;
        if (slot < 0)
        {
            slot = Victim(start);
            if (slot < 0)
            {
                ReportDropped(start.Tag);
                return Dropped;
            }

            cut = _voices[slot].Tag;
        }

        int generation = (_generations[slot] + 1) & GenerationMask;
        if (generation == 0)
        {
            generation = 1;
        }

        // A victim keeps playing until the new voice has begun, so a start that throws drops nothing.
        int id = slot | (generation << SlotBits);
        _voices[slot].Begin(start, id, ToFrames(start.FadeInSeconds));
        _generations[slot] = generation;
        ReportDropped(cut);
        return id;
    }

    private void ReportDropped(int tag)
    {
        if (tag >= 0)
        {
            OnDropped?.Invoke(tag);
        }
    }

    /// <summary>Fades the voice out over the seconds given, then removes it. 0 removes it at once.</summary>
    public void Stop(int voice, float fadeSeconds)
    {
        Voice? found = Find(voice);
        if (found is null)
        {
            return;
        }

        int frames = ToFrames(fadeSeconds);
        if (frames == 0)
        {
            found.End();
            return;
        }

        found.Stopping = true;
        found.Envelope.To(0, frames);
    }

    public void SetVolume(int voice, float volume, float fadeSeconds)
    {
        Find(voice)?.Volume.To(volume, ToFrames(fadeSeconds));
    }

    /// <summary>
    /// Starts an ambience: its layers loop on the Ambience channel from a random frame, fading in over the seconds
    /// given, and its scattered sounds play at random times. Returns its id, or <see cref="NoAmbience"/> when
    /// <see cref="MaxAmbiences"/> are already playing. Its voices count against the voice limit like any other.
    /// </summary>
    public int StartAmbience(AmbienceDefinition definition, float volume, float fadeSeconds)
    {
        ArgumentNullException.ThrowIfNull(definition);
        int slot = Array.FindIndex(_ambiences, player => !player.Active);
        if (slot < 0)
        {
            return NoAmbience;
        }

        int generation = (_ambienceGenerations[slot] + 1) & GenerationMask;
        if (generation == 0)
        {
            generation = 1;
        }

        _ambienceGenerations[slot] = generation;
        int id = slot | (generation << SlotBits);
        _ambiences[slot].Begin(this, _random, definition, id, volume, fadeSeconds);
        return id;
    }

    /// <summary>Fades the ambience's layers and the scattered sounds still to start to the new volume.</summary>
    public void SetAmbienceVolume(int ambience, float volume, float fadeSeconds)
    {
        FindAmbience(ambience)?.SetVolume(this, volume, fadeSeconds);
    }

    /// <summary>
    /// Fades every layer out over the seconds given (0 removes them at once) and starts no more scattered sounds.
    /// A scattered sound already playing plays to its end.
    /// </summary>
    public void StopAmbience(int ambience, float fadeSeconds)
    {
        FindAmbience(ambience)?.Stop(this, fadeSeconds);
    }

    /// <summary>Moves a placed voice from the next mixed frame. A flat voice stays flat.</summary>
    public void SetPosition(int voice, Vector3 position)
    {
        Voice? found = Find(voice);
        if (found is not null && found.Placed)
        {
            found.Position = position;
        }
    }

    public void SetChannelVolume(Channel channel, float volume)
    {
        _channelVolumes[(int)channel] = volume;
    }

    /// <summary>Where placed voices are heard from, from the next mixed frame.</summary>
    public void SetListener(Vector3 position, Vector3 forward)
    {
        _listener = position;
        _forward = forward;
    }

    /// <summary>The world's time scale. Voices that follow it play slower and lower below 1, and hold at 0.</summary>
    public void SetWorldRate(float scale)
    {
        _worldRate = Math.Max(0, scale);
    }

    /// <summary>Fills <c>stereo.Length / 2</c> interleaved frames and moves the sample clock on by that many.</summary>
    public void Mix(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        Span<float> block = stereo[..(frames * 2)];
        stereo.Clear();

        for (int i = 0; i < MaxAmbiences; i++)
        {
            if (_ambiences[i].Active)
            {
                _ambiences[i].Update(this, _random, frames);
            }
        }

        for (int i = 0; i < MaxVoices; i++)
        {
            Voice voice = _voices[i];
            if (!voice.Active)
            {
                continue;
            }

            double step = (double)voice.Clip.SampleRate / SampleRate * voice.Pitch;
            if (voice.FollowsScale)
            {
                step *= _worldRate;
            }

            float channel = ChannelGain(voice.Channel);
            float left = channel;
            float right = channel;
            if (voice.Placed)
            {
                float falloff = Falloff(voice);
                (float panLeft, float panRight) = Spatial.EqualPower(Spatial.Pan(_listener, _forward, voice.Position));
                left = panLeft * falloff * channel;
                right = panRight * falloff * channel;
            }
            else if (voice.Pan != 0)
            {
                (float balanceLeft, float balanceRight) = Spatial.Balance(voice.Pan);
                left *= balanceLeft;
                right *= balanceRight;
            }

            if (!voice.Mix(block, step, left, right))
            {
                // A voice whose reader failed, such as on a corrupt frame far into a streamed file, loses its own
                // sound and nothing else.
                if (voice.Failure is not null)
                {
                    RecordFailure(voice.Failure);
                }

                voice.End();
            }
        }

        Volatile.Write(ref _clock, _clock + frames);
    }

    /// <summary>
    /// Ends every ambience and voice at once, closing the readers of streamed clips. For when the mixer is done with,
    /// such as when the game closes.
    /// </summary>
    public void StopAll()
    {
        for (int i = 0; i < MaxAmbiences; i++)
        {
            if (_ambiences[i].Active)
            {
                _ambiences[i].Stop(this, 0);
            }
        }

        for (int i = 0; i < MaxVoices; i++)
        {
            if (_voices[i].Active)
            {
                _voices[i].End();
            }
        }
    }

    /// <summary>Copies what's playing into the span, up to its length, and returns how many it wrote.</summary>
    public int Snapshot(Span<PlayingSound> into)
    {
        int count = 0;
        for (int i = 0; i < MaxVoices && count < into.Length; i++)
        {
            Voice voice = _voices[i];
            if (!voice.Active)
            {
                continue;
            }

            float gain = voice.Volume.Value * voice.Envelope.Value * ChannelGain(voice.Channel);
            float pan = voice.Pan;
            if (voice.Placed)
            {
                gain *= Falloff(voice);
                pan = Spatial.Pan(_listener, _forward, voice.Position);
            }

            into[count++] = new PlayingSound(voice.Clip.Path, voice.Channel, voice.Loop, gain, pan, voice.Seconds);
        }

        return count;
    }

    internal static int ToFrames(float seconds)
    {
        return seconds > 0 ? (int)Math.Round((double)seconds * SampleRate) : 0;
    }

    private float ChannelGain(Channel channel)
    {
        float master = _channelVolumes[(int)Channel.Master];
        return channel == Channel.Master ? master : _channelVolumes[(int)channel] * master;
    }

    private float Falloff(Voice voice)
    {
        return Spatial.Falloff(Vector3.Distance(_listener, voice.Position), voice.Near, voice.Far);
    }

    internal void RecordFailure(Exception problem)
    {
        FailedVoices++;
        LastFailure = problem.Message;
    }

    private AmbiencePlayer? FindAmbience(int id)
    {
        if (id < 0)
        {
            return null;
        }

        AmbiencePlayer player = _ambiences[(id & SlotMask) % MaxAmbiences];
        return player.Active && player.Id == id ? player : null;
    }

    private Voice? Find(int id)
    {
        if (id < 0)
        {
            return null;
        }

        Voice voice = _voices[(id & SlotMask) % MaxVoices];
        return voice.Active && voice.Id == id ? voice : null;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < MaxVoices; i++)
        {
            if (!_voices[i].Active)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The slot to give up for this new voice, or -1 to drop the new voice instead.</summary>
    private int Victim(in VoiceStart start)
    {
        int quietest = -1;
        float quietestLoudness = float.MaxValue;
        for (int i = 0; i < MaxVoices; i++)
        {
            Voice voice = _voices[i];
            if (voice.Protected)
            {
                continue;
            }

            float loudness = Loudness(voice);
            if (loudness < quietestLoudness)
            {
                quietest = i;
                quietestLoudness = loudness;
            }
        }

        if (quietest < 0)
        {
            return -1;
        }

        if (!start.IsMusic && !start.IsLayer)
        {
            float loudness = start.Volume * ChannelGain(start.Channel);
            if (start.Position is Vector3 position)
            {
                loudness *= Spatial.Falloff(Vector3.Distance(_listener, position), start.Near, start.Far);
            }

            if (loudness < quietestLoudness)
            {
                return -1;
            }
        }

        return quietest;
    }

    /// <summary>How loud the voice is heading for. A fade-in counts as already up, and a stop as where it has got to.</summary>
    private float Loudness(Voice voice)
    {
        float loudness = voice.Volume.Target * (voice.Stopping ? voice.Envelope.Value : 1) * ChannelGain(voice.Channel);
        if (voice.Placed)
        {
            loudness *= Falloff(voice);
        }

        return loudness;
    }
}
