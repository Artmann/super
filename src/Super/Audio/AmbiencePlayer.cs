namespace Super;

/// <summary>
/// One playing ambience: its looping layers, which are ordinary mixer voices, and the timers that start its
/// scattered sounds. The mixer keeps a fixed number of these and reuses them, so updating one allocates nothing.
/// </summary>
internal sealed class AmbiencePlayer
{
    private AmbienceDefinition _definition = null!;
    private int[] _layerVoices = [];
    private long[] _nextAt = [];

    public bool Active { get; private set; }

    public int Id { get; private set; }

    /// <summary>The ambience's own volume, which scales its layers and its scattered sounds.</summary>
    public Ramp Volume;

    /// <summary>
    /// Starts the layers, each at a random frame of its clip, and sets the first time for each scattered entry.
    /// This may allocate: it runs when an ambience starts, never while mixing.
    /// </summary>
    public void Begin(Mixer mixer, RandomStream random, AmbienceDefinition definition, int id, float volume, float fadeSeconds)
    {
        _definition = definition;
        Id = id;
        // Scattered sounds follow the same fade in as the layers, so the first crow of a fading-in forest is quiet too.
        Volume.Set(0);
        Volume.To(volume, Mixer.ToFrames(fadeSeconds));
        if (_layerVoices.Length < definition.Layers.Length)
        {
            _layerVoices = new int[definition.Layers.Length];
        }

        if (_nextAt.Length < definition.Scattered.Length)
        {
            _nextAt = new long[definition.Scattered.Length];
        }

        for (int i = 0; i < definition.Layers.Length; i++)
        {
            Layer layer = definition.Layers[i];
            long frames = (long)Math.Round(layer.Clip.Seconds * layer.Clip.SampleRate);
            try
            {
                _layerVoices[i] = mixer.Start(new VoiceStart
                {
                    Clip = layer.Clip,
                    Channel = Channel.Ambience,
                    Volume = layer.Volume * volume,
                    Loop = true,
                    FadeInSeconds = fadeSeconds,
                    StartFrame = frames > 1 ? random.Between(0, (int)Math.Min(frames - 1, int.MaxValue)) : 0,
                    IsLayer = true,
                });
                if (_layerVoices[i] == Mixer.Dropped)
                {
                    // Only possible when every voice is music or a layer. The rest of the ambience still plays.
                    mixer.RecordFailure($"The ambience layer {layer.Clip.Path} found no free voice: all {Mixer.MaxVoices} are music or ambience layers.");
                }
            }
            catch
            {
                // The ambience never becomes active, so nothing else would stop the layers already started.
                for (int started = 0; started < i; started++)
                {
                    mixer.Stop(_layerVoices[started], 0);
                }

                throw;
            }
        }

        for (int i = 0; i < definition.Scattered.Length; i++)
        {
            _nextAt[i] = mixer.Clock + Wait(random, definition.Scattered[i]);
        }

        Active = true;
    }

    /// <summary>
    /// Starts every scattered sound that is due before the end of the block of <paramref name="frames"/> that starts
    /// at the mixer's clock, and moves the ambience volume on. A sound starts at the start of the block it is due in, so
    /// it starts early, by less than one block.
    /// </summary>
    public void Update(Mixer mixer, RandomStream random, int frames)
    {
        long clock = mixer.Clock;
        for (int i = 0; i < _definition.Scattered.Length; i++)
        {
            if (_nextAt[i] >= clock + frames)
            {
                continue;
            }

            // The next time is set first, so a sound that can't start is skipped, not retried every block.
            Scattered entry = _definition.Scattered[i];
            _nextAt[i] = Math.Max(_nextAt[i], clock) + Wait(random, entry);
            try
            {
                mixer.Start(new VoiceStart
                {
                    Clip = entry.Clips[random.Between(0, entry.Clips.Length - 1)],
                    Channel = Channel.Ambience,
                    Volume = random.Between(entry.MinVolume, entry.MaxVolume) * Volume.Value,
                    Pitch = 1 + random.Between(-Math.Abs(entry.PitchVariation), Math.Abs(entry.PitchVariation)),
                    Pan = random.Between(-0.8f, 0.8f),
                });
            }
            catch (Exception problem)
            {
                mixer.RecordFailure(problem);
            }
        }

        Volume.Advance(frames);
    }

    /// <summary>Fades the layers to their volume times the new ambience volume. Scattered sounds already playing keep theirs.</summary>
    public void SetVolume(Mixer mixer, float volume, float fadeSeconds)
    {
        Volume.To(volume, Mixer.ToFrames(fadeSeconds));
        for (int i = 0; i < _definition.Layers.Length; i++)
        {
            mixer.SetVolume(_layerVoices[i], _definition.Layers[i].Volume * volume, fadeSeconds);
        }
    }

    /// <summary>
    /// Fades every layer out and starts no more scattered sounds. A scattered sound already playing plays to its end.
    /// </summary>
    public void Stop(Mixer mixer, float fadeSeconds)
    {
        for (int i = 0; i < _definition.Layers.Length; i++)
        {
            mixer.Stop(_layerVoices[i], fadeSeconds);
        }

        Active = false;
        _definition = null!;
    }

    private static long Wait(RandomStream random, Scattered entry)
    {
        return Math.Max(1, Mixer.ToFrames(random.Between(entry.MinSeconds, entry.MaxSeconds)));
    }
}
