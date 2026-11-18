using System.Numerics;

namespace Super;

/// <summary>
/// Mixes every playing voice into 48 kHz stereo floats, with channel volumes, fades, pitch, distance falloff and
/// panning from the listener. It allocates nothing while mixing.
/// </summary>
/// <remarks>
/// The voices live in a <see cref="VoiceBank"/> and the ambiences in an <see cref="AmbienceBank"/>; how the mix is
/// heard (channel volumes, listener, world rate) is <see cref="Listening"/>. Ids carry their slot in the low bits and
/// the slot's start count above them (<see cref="MixSlotIds"/>), so an id kept after its sound ended can't reach the
/// next sound in that slot. Calls on unknown, ended or <see cref="Dropped"/> ids do nothing.
/// </remarks>
internal sealed class Mixer
{
    public const int SampleRate = MixRate.SampleRate;
    public const int MaxVoices = VoiceBank.SlotCount;

    /// <summary>How many ambiences can play at once.</summary>
    public const int MaxAmbiences = AmbienceBank.SlotCount;

    /// <summary>The id <see cref="StartAmbience"/> returns when all <see cref="MaxAmbiences"/> are already playing.</summary>
    public const int NoAmbience = AmbienceBank.NoAmbience;

    /// <summary>The id <see cref="Start"/> returns when the voice limit dropped the new sound.</summary>
    public const int Dropped = VoiceBank.Dropped;

    private readonly VoiceBank _voices;
    private readonly AmbienceBank _ambiences;
    private long _clock;

    /// <summary>
    /// Makes a mixer. <paramref name="random"/> picks everything random in ambiences: where layers start and when,
    /// which and how loud scattered sounds play. Pass <c>new GameRandom(seed).Stream("audio")</c>, never the game's own.
    /// </summary>
    public Mixer(RandomStream random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _voices = new VoiceBank(Listening);
        _ambiences = new AmbienceBank(_voices, random, Failures);
    }

    /// <summary>The channel volumes, the listener and the world rate, which take effect from the next mixed frame.</summary>
    public MixListening Listening { get; } = new();

    /// <summary>
    /// Voices that ended because their clip's reader threw, scattered sounds that couldn't start, and ambience layers
    /// that found no free voice.
    /// </summary>
    public MixFailures Failures { get; } = new();

    /// <summary>
    /// Called with the <see cref="VoiceStart.Tag"/> of a tagged voice the voice limit dropped: one refused at its start,
    /// or one cut for a newer voice. Not called for voices that end or fail. Runs on whatever thread mixes.
    /// </summary>
    public Action<int>? OnDropped
    {
        get => _voices.OnDropped;
        set => _voices.OnDropped = value;
    }

    /// <summary>How many voice slots are free.</summary>
    public int FreeVoices => _voices.FreeVoices;

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
    public int Start(in VoiceStart start) => _voices.Start(start);

    /// <summary>Fades the voice out over the seconds given, then removes it. 0 removes it at once.</summary>
    public void Stop(int voice, float fadeSeconds) => _voices.Stop(voice, fadeSeconds);

    public void SetVolume(int voice, float volume, float fadeSeconds) => _voices.SetVolume(voice, volume, fadeSeconds);

    /// <summary>Moves a placed voice from the next mixed frame. A flat voice stays flat.</summary>
    public void SetPosition(int voice, Vector3 position) => _voices.SetPosition(voice, position);

    /// <summary>
    /// Starts an ambience: its layers loop on the Ambience channel from a random frame, fading in over the seconds
    /// given, and its scattered sounds play at random times. Returns its id, or <see cref="NoAmbience"/> when
    /// <see cref="MaxAmbiences"/> are already playing. Its voices count against the voice limit like any other.
    /// </summary>
    public int StartAmbience(AmbienceDefinition definition, float volume, float fadeSeconds)
    {
        return _ambiences.Start(definition, volume, fadeSeconds, Clock);
    }

    /// <summary>Fades the ambience's layers and the scattered sounds still to start to the new volume.</summary>
    public void SetAmbienceVolume(int ambience, float volume, float fadeSeconds)
    {
        _ambiences.SetVolume(ambience, volume, fadeSeconds);
    }

    /// <summary>
    /// Fades every layer out over the seconds given (0 removes them at once) and starts no more scattered sounds.
    /// A scattered sound already playing plays to its end.
    /// </summary>
    public void StopAmbience(int ambience, float fadeSeconds)
    {
        _ambiences.Stop(ambience, fadeSeconds);
    }

    /// <summary>Fills <c>stereo.Length / 2</c> interleaved frames and moves the sample clock on by that many.</summary>
    public void Mix(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        Span<float> block = stereo[..(frames * 2)];
        stereo.Clear();
        _ambiences.Update(frames, _clock);
        _voices.MixInto(block, Failures);
        Volatile.Write(ref _clock, _clock + frames);
    }

    /// <summary>
    /// Ends every ambience and voice at once, closing the readers of streamed clips. For when the mixer is done with,
    /// such as when the game closes.
    /// </summary>
    public void StopAll()
    {
        _ambiences.StopAll();
        _voices.EndAll();
    }

    /// <summary>Copies what's playing into the span, up to its length, and returns how many it wrote.</summary>
    public int Snapshot(Span<PlayingSound> into) => _voices.Snapshot(into);
}
