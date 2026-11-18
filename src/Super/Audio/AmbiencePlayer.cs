namespace Super;

/// <summary>
/// One playing ambience: its looping layers, which are ordinary voices in the <see cref="VoiceBank"/>, and the timers
/// that start its scattered sounds. The <see cref="AmbienceBank"/> keeps a fixed number of these and reuses them, so
/// updating one allocates nothing.
/// </summary>
internal sealed class AmbiencePlayer
{
    private readonly VoiceBank _voices;
    private readonly RandomStream _random;
    private readonly MixFailures _failures;
    private AmbienceDefinition _definition = null!;
    private int[] _layerVoices = [];
    private long[] _nextAt = [];

    /// <param name="random">Picks where layers start and when, and which and how loud scattered sounds play.</param>
    public AmbiencePlayer(VoiceBank voices, RandomStream random, MixFailures failures)
    {
        _voices = voices;
        _random = random;
        _failures = failures;
    }

    public bool Active { get; private set; }

    public int Id { get; private set; }

    /// <summary>The ambience's own volume, which scales its layers and its scattered sounds.</summary>
    public Ramp Volume;

    /// <summary>
    /// Starts the layers, each at a random frame of its clip, and sets the first time for each scattered entry,
    /// counted from <paramref name="clock"/>. This may allocate: it runs when an ambience starts, never while mixing.
    /// </summary>
    public void Begin(AmbienceDefinition definition, int id, float volume, float fadeSeconds, long clock)
    {
        _definition = definition;
        Id = id;
        // Scattered sounds follow the same fade in as the layers, so the first crow of a fading-in forest is quiet too.
        Volume.Set(0);
        Volume.To(volume, MixRate.ToFrames(fadeSeconds));
        if (_layerVoices.Length < definition.Layers.Length)
        {
            _layerVoices = new int[definition.Layers.Length];
        }

        if (_nextAt.Length < definition.Scattered.Length)
        {
            _nextAt = new long[definition.Scattered.Length];
        }

        StartLayers(volume, fadeSeconds);
        for (int i = 0; i < definition.Scattered.Length; i++)
        {
            _nextAt[i] = clock + Wait(definition.Scattered[i]);
        }

        Active = true;
    }

    /// <summary>
    /// Starts every scattered sound that is due before the end of the block of <paramref name="frames"/> that starts
    /// at <paramref name="clock"/>, and moves the ambience volume on. A sound starts at the start of the block it is due
    /// in, so it starts early, by less than one block.
    /// </summary>
    public void Update(int frames, long clock)
    {
        for (int i = 0; i < _definition.Scattered.Length; i++)
        {
            if (_nextAt[i] >= clock + frames)
            {
                continue;
            }

            // The next time is set first, so a sound that can't start is skipped, not retried every block.
            Scattered entry = _definition.Scattered[i];
            _nextAt[i] = Math.Max(_nextAt[i], clock) + Wait(entry);
            try
            {
                _voices.Start(new VoiceStart
                {
                    Clip = entry.Clips[_random.Between(0, entry.Clips.Length - 1)],
                    Channel = Channel.Ambience,
                    Volume = _random.Between(entry.MinVolume, entry.MaxVolume) * Volume.Value,
                    Pitch = 1 + _random.Between(-Math.Abs(entry.PitchVariation), Math.Abs(entry.PitchVariation)),
                    Pan = _random.Between(-0.8f, 0.8f),
                });
            }
            catch (Exception problem)
            {
                _failures.Tally(problem);
            }
        }

        Volume.Advance(frames);
    }

    /// <summary>Fades the layers to their volume times the new ambience volume. Scattered sounds already playing keep theirs.</summary>
    public void SetVolume(float volume, float fadeSeconds)
    {
        Volume.To(volume, MixRate.ToFrames(fadeSeconds));
        for (int i = 0; i < _definition.Layers.Length; i++)
        {
            _voices.SetVolume(_layerVoices[i], _definition.Layers[i].Volume * volume, fadeSeconds);
        }
    }

    /// <summary>
    /// Fades every layer out and starts no more scattered sounds. A scattered sound already playing plays to its end.
    /// </summary>
    public void Stop(float fadeSeconds)
    {
        for (int i = 0; i < _definition.Layers.Length; i++)
        {
            _voices.Stop(_layerVoices[i], fadeSeconds);
        }

        Active = false;
        _definition = null!;
    }

    /// <summary>Starts every layer. If one throws, the layers already started are stopped, since the ambience never becomes active.</summary>
    private void StartLayers(float volume, float fadeSeconds)
    {
        Layer[] layers = _definition.Layers;
        for (int i = 0; i < layers.Length; i++)
        {
            try
            {
                _layerVoices[i] = StartLayer(layers[i], volume, fadeSeconds);
            }
            catch
            {
                for (int started = 0; started < i; started++)
                {
                    _voices.Stop(_layerVoices[started], 0);
                }

                throw;
            }
        }
    }

    private int StartLayer(Layer layer, float volume, float fadeSeconds)
    {
        long frames = (long)Math.Round(layer.Clip.Seconds * layer.Clip.SampleRate);
        int voice = _voices.Start(new VoiceStart
        {
            Clip = layer.Clip,
            Channel = Channel.Ambience,
            Volume = layer.Volume * volume,
            Loop = true,
            FadeInSeconds = fadeSeconds,
            StartFrame = frames > 1 ? _random.Between(0, (int)Math.Min(frames - 1, int.MaxValue)) : 0,
            IsLayer = true,
        });
        if (voice == VoiceBank.Dropped)
        {
            // Only possible when every voice is music or a layer. The rest of the ambience still plays.
            _failures.Tally($"The ambience layer {layer.Clip.Path} found no free voice: all {VoiceBank.SlotCount} are music or ambience layers.");
        }

        return voice;
    }

    private long Wait(Scattered entry)
    {
        return Math.Max(1, MixRate.ToFrames(_random.Between(entry.MinSeconds, entry.MaxSeconds)));
    }
}

/// <summary>
/// The mixer's 8 ambience slots. Ids work as voice ids do, and calls on unknown or ended ids do nothing.
/// </summary>
internal sealed class AmbienceBank
{
    public const int SlotCount = 8;

    /// <summary>The id <see cref="Start"/> returns when all <see cref="SlotCount"/> are already playing.</summary>
    public const int NoAmbience = -1;

    private readonly AmbiencePlayer[] _players = new AmbiencePlayer[SlotCount];
    private readonly int[] _generations = new int[SlotCount];

    public AmbienceBank(VoiceBank voices, RandomStream random, MixFailures failures)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            _players[i] = new AmbiencePlayer(voices, random, failures);
        }
    }

    /// <summary>See <see cref="Mixer.StartAmbience"/>. Scattered sounds are timed from <paramref name="clock"/>.</summary>
    public int Start(AmbienceDefinition definition, float volume, float fadeSeconds, long clock)
    {
        ArgumentNullException.ThrowIfNull(definition);
        int slot = Array.FindIndex(_players, player => !player.Active);
        if (slot < 0)
        {
            return NoAmbience;
        }

        int generation = MixSlotIds.NextGeneration(_generations[slot]);
        _generations[slot] = generation;
        int id = MixSlotIds.Make(slot, generation);
        _players[slot].Begin(definition, id, volume, fadeSeconds, clock);
        return id;
    }

    public void SetVolume(int ambience, float volume, float fadeSeconds)
    {
        Find(ambience)?.SetVolume(volume, fadeSeconds);
    }

    public void Stop(int ambience, float fadeSeconds)
    {
        Find(ambience)?.Stop(fadeSeconds);
    }

    /// <summary>Lets every playing ambience start its scattered sounds due in the block at <paramref name="clock"/>.</summary>
    public void Update(int frames, long clock)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_players[i].Active)
            {
                _players[i].Update(frames, clock);
            }
        }
    }

    /// <summary>Stops every ambience's layers at once.</summary>
    public void StopAll()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (_players[i].Active)
            {
                _players[i].Stop(0);
            }
        }
    }

    private AmbiencePlayer? Find(int id)
    {
        if (id < 0)
        {
            return null;
        }

        AmbiencePlayer player = _players[MixSlotIds.Slot(id) % SlotCount];
        return player.Active && player.Id == id ? player : null;
    }
}
