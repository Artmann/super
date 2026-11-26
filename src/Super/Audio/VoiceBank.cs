using System.Numerics;

namespace Super;

/// <summary>
/// The mixer's 64 voice slots: starting, finding and stopping voices, mixing them, and choosing which to drop when
/// every slot is taken. Calls on unknown, ended or <see cref="Dropped"/> ids do nothing.
/// </summary>
internal sealed class VoiceBank
{
    public const int SlotCount = 64;

    /// <summary>The id <see cref="Start"/> returns when the voice limit dropped the new sound.</summary>
    public const int Dropped = -1;

    private readonly Voice[] _voices = new Voice[SlotCount];
    private readonly int[] _generations = new int[SlotCount];
    private readonly MixListening _listening;

    public VoiceBank(MixListening listening)
    {
        _listening = listening;
        for (int i = 0; i < SlotCount; i++)
        {
            _voices[i] = new Voice();
        }
    }

    /// <summary>See <see cref="Mixer.OnDropped"/>.</summary>
    public Action<int>? OnDropped { get; set; }

    /// <summary>How many voice slots are free.</summary>
    public int FreeVoices
    {
        get
        {
            int free = 0;
            for (int i = 0; i < SlotCount; i++)
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
    /// The channels with a sound playing that isn't stopping, as bit <c>1 &lt;&lt; channel</c> for each, for
    /// <see cref="Ducking"/>.
    /// </summary>
    public int ChannelsPlaying()
    {
        int playing = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            Voice voice = _voices[i];
            if (voice.Active && !voice.Stopping)
            {
                playing |= 1 << (int)voice.Setup.Channel;
            }
        }

        return playing;
    }

    /// <summary>See <see cref="Mixer.Start"/>.</summary>
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

            cut = _voices[slot].Setup.Tag;
        }

        // A victim keeps playing until the new voice has begun, so a start that throws drops nothing.
        int generation = MixSlotIds.NextGeneration(_generations[slot]);
        int id = MixSlotIds.Make(slot, generation);
        _voices[slot].Begin(start, id, MixRate.ToFrames(start.FadeInSeconds));
        _generations[slot] = generation;
        ReportDropped(cut);
        return id;
    }

    /// <summary>Fades the voice out over the seconds given, then removes it. 0 removes it at once.</summary>
    public void Stop(int voice, float fadeSeconds)
    {
        Voice? found = Find(voice);
        if (found is null)
        {
            return;
        }

        int frames = MixRate.ToFrames(fadeSeconds);
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
        Find(voice)?.Volume.To(volume, MixRate.ToFrames(fadeSeconds));
    }

    /// <summary>Moves a placed voice from the next mixed frame, with its velocity and Doppler amount. A flat voice stays flat.</summary>
    public void SetPosition(int voice, Vector3 position, Vector3 velocity, float doppler)
    {
        Find(voice)?.Reposition(position, velocity, doppler);
    }

    /// <summary>
    /// Adds every playing voice into the block. A voice whose reader failed, such as on a corrupt frame far into a
    /// streamed file, loses its own sound and nothing else: it ends and is counted in <paramref name="failures"/>.
    /// </summary>
    public void MixInto(Span<float> block, MixFailures failures)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            Voice voice = _voices[i];
            if (!voice.Active)
            {
                continue;
            }

            double step = _listening.SourceStep(voice);
            (float left, float right) = _listening.SideGains(voice);
            if (voice.Mix(block, step, left, right))
            {
                continue;
            }

            if (voice.Failure is not null)
            {
                failures.Tally(voice.Failure);
            }

            voice.End();
        }
    }

    /// <summary>Ends every voice at once, closing the readers of streamed clips.</summary>
    public void EndAll()
    {
        for (int i = 0; i < SlotCount; i++)
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
        for (int i = 0; i < SlotCount && count < into.Length; i++)
        {
            Voice voice = _voices[i];
            if (!voice.Active)
            {
                continue;
            }

            ref readonly VoiceStart setup = ref voice.Setup;
            float gain = voice.Volume.Value * voice.Envelope.Value * _listening.ChannelGain(setup.Channel);
            float pan = setup.Pan;
            if (voice.Placed)
            {
                gain *= _listening.Falloff(voice);
                pan = _listening.Pan(voice.Position);
            }

            into[count++] = new PlayingSound(setup.Clip.Path, setup.Channel, setup.Loop, gain, pan, voice.Seconds);
        }

        return count;
    }

    private void ReportDropped(int tag)
    {
        if (tag >= 0)
        {
            OnDropped?.Invoke(tag);
        }
    }

    private Voice? Find(int id)
    {
        if (id < 0)
        {
            return null;
        }

        Voice voice = _voices[MixSlotIds.Slot(id) % SlotCount];
        return voice.Active && voice.Id == id ? voice : null;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < SlotCount; i++)
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
        int quietest = Quietest(out float quietestLoudness);
        if (quietest < 0)
        {
            return -1;
        }

        // Music and ambience layers take the quietest slot whatever their own volume.
        if (!start.IsMusic && !start.IsLayer && Loudness(start) < quietestLoudness)
        {
            return -1;
        }

        return quietest;
    }

    /// <summary>The quietest voice the limit may drop and how loud it is, or -1 when every voice is protected.</summary>
    private int Quietest(out float quietestLoudness)
    {
        int quietest = -1;
        quietestLoudness = float.MaxValue;
        for (int i = 0; i < SlotCount; i++)
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

        return quietest;
    }

    /// <summary>How loud the voice is heading for. A fade-in counts as already up, and a stop as where it has got to.</summary>
    private float Loudness(Voice voice)
    {
        float loudness = voice.Volume.Target * (voice.Stopping ? voice.Envelope.Value : 1) * _listening.ChannelGain(voice.Setup.Channel);
        if (voice.Placed)
        {
            loudness *= _listening.Falloff(voice);
        }

        return loudness;
    }

    /// <summary>How loud a new voice would start.</summary>
    private float Loudness(in VoiceStart start)
    {
        float loudness = start.Volume * _listening.ChannelGain(start.Channel);
        if (start.Position is Vector3 position)
        {
            loudness *= _listening.Falloff(position, start.Near, start.Far);
        }

        return loudness;
    }
}
