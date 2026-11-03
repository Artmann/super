namespace Super.Tests.Audio;

public class AmbiencePlayerTests
{
    private const int Block = 512;

    private static SoundClip Constant(string path, int frames, float value = 1f)
    {
        var samples = new float[frames];
        Array.Fill(samples, value);
        return SoundClip.Read(path, TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: true));
    }

    /// <summary>A mono clip whose frame k holds k / frames.</summary>
    private static SoundClip Ramp(string path, int frames)
    {
        var samples = new float[frames];
        for (int k = 0; k < frames; k++)
        {
            samples[k] = (float)k / frames;
        }

        return SoundClip.Read(path, TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: true));
    }

    private static AmbienceDefinition Layers(params (SoundClip Clip, float Volume)[] layers)
    {
        return new AmbienceDefinition([.. layers.Select(l => new Layer(l.Clip, l.Volume))], []);
    }

    private static AmbienceDefinition Every(float min, float max, SoundClip clip, float minVolume = 1, float maxVolume = 1, float pitch = 0)
    {
        return new AmbienceDefinition([], [new Scattered([clip], min, max, minVolume, maxVolume, pitch)]);
    }

    private static float[] Mix(Mixer mixer, int frames)
    {
        var stereo = new float[frames * 2];
        mixer.Mix(stereo);
        return stereo;
    }

    private static PlayingSound[] Snapshot(Mixer mixer)
    {
        var into = new PlayingSound[Mixer.MaxVoices];
        int count = mixer.Snapshot(into);
        return into[..count];
    }

    /// <summary>Mixes blocks until one sounds, and returns it. The scattered clip must be shorter than a block.</summary>
    private static float[] FirstSoundingBlock(Mixer mixer)
    {
        float[] stereo;
        do
        {
            stereo = Mix(mixer, Block);
        }
        while (stereo.All(sample => sample == 0));

        return stereo;
    }

    /// <summary>
    /// The frames at which scattered sounds started, found by mixing blocks and noting each one that sounds. The clip
    /// must be shorter than a block, so a start can't spill into the next one.
    /// </summary>
    private static List<long> StartFrames(Mixer mixer, float seconds)
    {
        var starts = new List<long>();
        var stereo = new float[Block * 2];
        for (long frame = 0; frame < seconds * Mixer.SampleRate; frame += Block)
        {
            mixer.Mix(stereo);
            if (stereo.Any(sample => sample != 0))
            {
                starts.Add(frame);
            }
        }

        return starts;
    }

    [Fact]
    public void LayersPlayTogetherAtTheirVolumes()
    {
        var mixer = TestMixer.Make();

        mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.6f), (Constant("wind.wav", 4800), 0.3f)), 1, 0);
        float[] stereo = Mix(mixer, 100);

        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(2, playing.Length);
        PlayingSound rain = Assert.Single(playing, p => p.Clip == "rain.wav");
        PlayingSound wind = Assert.Single(playing, p => p.Clip == "wind.wav");
        Assert.Equal(0.6f, rain.Gain, 1e-6f);
        Assert.Equal(0.3f, wind.Gain, 1e-6f);
        Assert.All(playing, p => Assert.Equal(Channel.Ambience, p.Channel));
        Assert.All(playing, p => Assert.True(p.Looping));
        Assert.Equal(0.9f, stereo[0], 1e-6f);
    }

    [Fact]
    public void AmbienceVolumeAndChannelScaleTheLayers()
    {
        var mixer = TestMixer.Make();
        mixer.SetChannelVolume(Channel.Ambience, 0.5f);

        mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.6f)), 0.5f, 0);
        float[] stereo = Mix(mixer, 10);

        Assert.Equal(0.15f, stereo[0], 1e-6f);
    }

    [Fact]
    public void LayersStartAtDifferentPoints()
    {
        SoundClip clip = Ramp("ramp.wav", 48000);
        var firstSamples = new List<float>();
        for (int seed = 1; seed <= 20; seed++)
        {
            Mixer mixer = TestMixer.Make(seed);
            mixer.StartAmbience(Layers((clip, 1)), 1, 0);
            firstSamples.Add(Mix(mixer, 4)[0]);
        }

        Assert.True(firstSamples.Distinct().Count() > 10, string.Join(", ", firstSamples));
        Assert.All(firstSamples, sample => Assert.InRange(sample, 0f, 1f));
    }

    [Fact]
    public void ALayerStartedPartWayStillLoopsWholeClips()
    {
        var mixer = TestMixer.Make();
        mixer.StartAmbience(Layers((Ramp("ramp.wav", 4800), 1)), 1, 0);
        float start = Mix(mixer, 1)[0];

        // A whole clip later the layer is back at the same sample, whichever frame it started from.
        Mix(mixer, 4799);
        float again = Mix(mixer, 1)[0];

        Assert.Equal(start, again, 1e-5f);
    }

    [Fact]
    public void ALayerOnAStreamedClipStartsPartWayToo()
    {
        SoundClip clip = Ramp("long.wav", 12 * Mixer.SampleRate); // past the decode limit
        Assert.True(clip.Streamed);
        var firstSamples = new List<float>();
        for (int seed = 1; seed <= 10; seed++)
        {
            Mixer mixer = TestMixer.Make(seed);
            mixer.StartAmbience(Layers((clip, 1)), 1, 0);
            firstSamples.Add(Mix(mixer, 4)[0]);
        }

        Assert.True(firstSamples.Distinct().Count() > 5, string.Join(", ", firstSamples));
    }

    /// <summary>A silent streamed reader that counts what the voice asks of it.</summary>
    private sealed class CountingReader(long frames) : ISampleReader
    {
        public long Frames => frames;

        public long FramesRead { get; private set; }

        public int ExactSeeks { get; private set; }

        public long NearFrame { get; private set; } = -1;

        public int Read(Span<float> interleaved)
        {
            int read = (int)Math.Min(interleaved.Length, frames - FramesRead);
            interleaved[..read].Clear();
            FramesRead += read;
            return read;
        }

        public void Seek(long frame)
        {
            ExactSeeks++;
            FramesRead = frame;
        }

        public void SeekNear(long frame)
        {
            NearFrame = frame;
            FramesRead = frame;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void AStreamedLayerStartsNearItsRandomFrameWithoutDecodingUpToIt()
    {
        const long frames = 60L * Mixer.SampleRate;
        var readers = new List<CountingReader>();
        SoundClip clip = SoundClip.Streaming("long.ogg", Mixer.SampleRate, 1, 60, () =>
        {
            var reader = new CountingReader(frames);
            readers.Add(reader);
            return reader;
        });
        var mixer = TestMixer.Make(3);

        mixer.StartAmbience(Layers((clip, 1)), 1, 0);

        CountingReader started = Assert.Single(readers);
        Assert.Equal(0, started.ExactSeeks);
        Assert.InRange(started.NearFrame, 1, frames - 1);
        Assert.Equal((double)started.NearFrame / Mixer.SampleRate, Assert.Single(Snapshot(mixer)).Seconds, 1e-9);
    }

    [Fact]
    public void ScatteredFiresWithinItsRange()
    {
        var mixer = TestMixer.Make();
        mixer.StartAmbience(Every(4, 12, Constant("twig.wav", 100)), 1, 0);

        List<long> starts = StartFrames(mixer, 120);

        Assert.NotEmpty(starts);
        var times = new List<double> { 0 };
        times.AddRange(starts.Select(frame => (double)frame / Mixer.SampleRate));
        double[] gaps = [.. times.Zip(times.Skip(1), (a, b) => b - a)];
        double slack = (double)Block / Mixer.SampleRate;
        Assert.All(gaps, gap => Assert.InRange(gap, 4 - slack, 12 + slack));

        // And the gaps are spread over the range, not all pinned to one end of it.
        Assert.True(gaps.Min() < 9, string.Join(", ", gaps));
        Assert.True(gaps.Max() > 7, string.Join(", ", gaps));
    }

    [Fact]
    public void SameSeedSameScatteredTimes()
    {
        List<long> Run(int seed)
        {
            Mixer mixer = TestMixer.Make(seed);
            mixer.StartAmbience(Every(1, 5, Constant("twig.wav", 100)), 1, 0);
            return StartFrames(mixer, 60);
        }

        List<long> first = Run(7);

        Assert.True(first.Count > 5);
        Assert.Equal(first, Run(7));
        Assert.NotEqual(first, Run(8));
    }

    [Fact]
    public void ScatteredSoundsShowInTheSnapshotWithTheirOwnVolumePitchAndPan()
    {
        SoundClip clip = Constant("owl.wav", 5 * Mixer.SampleRate);
        var pans = new List<float>();
        var gains = new List<float>();
        var pitches = new List<double>();
        for (int seed = 1; seed <= 12; seed++)
        {
            Mixer mixer = TestMixer.Make(seed);
            mixer.StartAmbience(Every(1, 1, clip, 0.2f, 0.4f, 0.5f), 0.5f, 0);
            while (Snapshot(mixer).Length == 0)
            {
                Mix(mixer, Block);
            }

            PlayingSound owl = Assert.Single(Snapshot(mixer));
            Assert.Equal(Channel.Ambience, owl.Channel);
            Assert.False(owl.Looping);
            pans.Add(owl.Pan);
            gains.Add(owl.Gain);

            // The voice started at the start of the block just mixed, so it has played that block at its pitch.
            pitches.Add(owl.Seconds * Mixer.SampleRate / Block);
        }

        Assert.All(pans, pan => Assert.InRange(pan, -0.8f, 0.8f));
        Assert.All(gains, gain => Assert.InRange(gain, 0.1f - 1e-6f, 0.2f + 1e-6f)); // volume 0.2..0.4 times ambience 0.5
        Assert.All(pitches, pitch => Assert.InRange(pitch, 0.5 - 1e-6, 1.5 + 1e-6));
        Assert.True(pans.Distinct().Count() > 6);
        Assert.True(gains.Distinct().Count() > 6);
        Assert.True(pitches.Distinct().Count() > 6);
    }

    [Fact]
    public void ScatteredWithNoPitchVariationPlaysAtItsOwnPitch()
    {
        var mixer = TestMixer.Make();
        mixer.StartAmbience(Every(1, 1, Constant("owl.wav", 5 * Mixer.SampleRate)), 1, 0);
        while (Snapshot(mixer).Length == 0)
        {
            Mix(mixer, Block);
        }

        Assert.Equal((double)Block / Mixer.SampleRate, Assert.Single(Snapshot(mixer)).Seconds, 1e-9);
    }

    [Fact]
    public void ScatteredPanTurnsDownOneEarInTheMix()
    {
        // A pan to one side turns the other ear down; the near ear is never turned down.
        var sides = new List<(float Left, float Right)>();
        for (int seed = 1; seed <= 12; seed++)
        {
            Mixer mixer = TestMixer.Make(seed);
            mixer.StartAmbience(Every(1, 1, Constant("owl.wav", 100)), 1, 0);
            float[] stereo = FirstSoundingBlock(mixer);
            sides.Add((stereo[0], stereo[1]));
        }

        Assert.All(sides, side => Assert.Equal(1f, Math.Max(side.Left, side.Right)));
        Assert.Contains(sides, side => side.Left < 1);
        Assert.Contains(sides, side => side.Right < 1);
    }

    [Fact]
    public void AmbienceVolumeScalesScatteredSounds()
    {
        var mixer = TestMixer.Make();
        mixer.StartAmbience(Every(1, 1, Constant("twig.wav", 100)), 0.5f, 0);

        float[] stereo = FirstSoundingBlock(mixer);

        Assert.Equal(0.5f, Math.Max(stereo[0], stereo[1]), 1e-6f);
    }

    [Fact]
    public void SetAmbienceVolumeFadesTheLayers()
    {
        var mixer = TestMixer.Make();
        int ambience = mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.6f)), 1, 0);
        Mix(mixer, 100);

        mixer.SetAmbienceVolume(ambience, 0.5f, 0.1f);
        Mix(mixer, 2400);
        Assert.Equal(0.45f, Assert.Single(Snapshot(mixer)).Gain, 1e-4f);

        Mix(mixer, 2400);
        Assert.Equal(0.3f, Assert.Single(Snapshot(mixer)).Gain, 1e-6f);
    }

    [Fact]
    public void ScatteredSoundsTakeTheNewAmbienceVolume()
    {
        var mixer = TestMixer.Make();
        int ambience = mixer.StartAmbience(Every(1, 1, Constant("twig.wav", 100)), 1, 0);
        mixer.SetAmbienceVolume(ambience, 0.25f, 0);

        float[] stereo = FirstSoundingBlock(mixer);

        Assert.Equal(0.25f, Math.Max(stereo[0], stereo[1]), 1e-6f);
    }

    [Fact]
    public void StopAmbienceFadesEveryLayer()
    {
        var mixer = TestMixer.Make();
        int ambience = mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.6f), (Constant("wind.wav", 4800), 0.3f)), 1, 0);
        Mix(mixer, 512);

        mixer.StopAmbience(ambience, 0.1f);
        Mix(mixer, 2400);
        PlayingSound[] halfway = Snapshot(mixer);
        Assert.Equal(0.3f, Assert.Single(halfway, p => p.Clip == "rain.wav").Gain, 1e-4f);
        Assert.Equal(0.15f, Assert.Single(halfway, p => p.Clip == "wind.wav").Gain, 1e-4f);

        Mix(mixer, 2400);
        Assert.Empty(Snapshot(mixer));
    }

    [Fact]
    public void StopAmbienceStartsNoMoreScatteredSounds()
    {
        var mixer = TestMixer.Make();
        int ambience = mixer.StartAmbience(Every(1, 1, Constant("twig.wav", 100)), 1, 0);
        Assert.NotEmpty(StartFrames(mixer, 3));

        mixer.StopAmbience(ambience, 0);

        Assert.Empty(StartFrames(mixer, 10));
        Assert.Empty(Snapshot(mixer));
    }

    [Fact]
    public void AScatteredSoundAlreadyPlayingFinishesAfterStop()
    {
        var mixer = TestMixer.Make();
        int ambience = mixer.StartAmbience(Every(1, 1, Constant("owl.wav", 5 * Mixer.SampleRate)), 1, 0);
        while (Snapshot(mixer).Length == 0)
        {
            Mix(mixer, Block);
        }

        mixer.StopAmbience(ambience, 0.1f);
        Mix(mixer, 4800);

        Assert.Equal("owl.wav", Assert.Single(Snapshot(mixer)).Clip);
    }

    [Fact]
    public void AStoppedAmbiencesIdDoesNothingAndItsSlotIsReused()
    {
        var mixer = TestMixer.Make();
        int old = mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.6f)), 1, 0);
        mixer.StopAmbience(old, 0);

        int next = mixer.StartAmbience(Layers((Constant("wind.wav", 4800), 0.3f)), 1, 0);
        mixer.StopAmbience(old, 0);
        mixer.SetAmbienceVolume(old, 0, 0);
        Mix(mixer, 10);

        Assert.NotEqual(old, next);
        PlayingSound playing = Assert.Single(Snapshot(mixer));
        Assert.Equal("wind.wav", playing.Clip);
        Assert.Equal(0.3f, playing.Gain, 1e-6f);
    }

    [Fact]
    public void OnlyEightAmbiencesPlayAtOnce()
    {
        var mixer = TestMixer.Make();
        AmbienceDefinition rain = Layers((Constant("rain.wav", 4800), 0.1f));
        var ids = new List<int>();
        for (int i = 0; i < Mixer.MaxAmbiences; i++)
        {
            ids.Add(mixer.StartAmbience(rain, 1, 0));
        }

        Assert.DoesNotContain(Mixer.NoAmbience, ids);
        Assert.Equal(Mixer.NoAmbience, mixer.StartAmbience(rain, 1, 0));

        mixer.StopAmbience(ids[3], 0);
        Assert.NotEqual(Mixer.NoAmbience, mixer.StartAmbience(rain, 1, 0));
    }

    [Fact]
    public void ALayerStartingPastTheLimitDropsAnEffectEvenAtLowerVolume()
    {
        var mixer = TestMixer.Make();
        SoundClip effect = Constant("effect.wav", 4800);
        for (int i = 0; i < Mixer.MaxVoices; i++)
        {
            mixer.Start(new VoiceStart { Clip = effect, Channel = Channel.Effects, Volume = 0.1f, Loop = true });
        }

        mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 1f)), 1, 0);

        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(Mixer.MaxVoices, playing.Length);
        Assert.Single(playing, p => p.Clip == "rain.wav");
        Assert.Equal(Mixer.MaxVoices - 1, playing.Count(p => p.Clip == "effect.wav"));
    }

    [Fact]
    public void ALayerQuieterThanEveryEffectIsStillKeptPastTheLimit()
    {
        var mixer = TestMixer.Make();
        SoundClip effect = Constant("effect.wav", 4800);
        for (int i = 0; i < Mixer.MaxVoices; i++)
        {
            mixer.Start(new VoiceStart { Clip = effect, Channel = Channel.Effects, Volume = 1f, Loop = true });
        }

        mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.001f)), 1, 0);

        Assert.Single(Snapshot(mixer), p => p.Clip == "rain.wav");
    }

    [Fact]
    public void ABurstOfEffectsNeverDropsAPlayingLayer()
    {
        var mixer = TestMixer.Make();
        mixer.StartAmbience(Layers((Constant("rain.wav", 4800), 0.01f), (Constant("wind.wav", 4800), 0.01f)), 1, 0);
        SoundClip shot = Constant("shot.wav", 4800);

        for (int i = 0; i < 3 * Mixer.MaxVoices; i++)
        {
            mixer.Start(new VoiceStart { Clip = shot, Channel = Channel.Effects, Volume = 1f, Loop = true });
        }

        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(Mixer.MaxVoices, playing.Length);
        Assert.Single(playing, p => p.Clip == "rain.wav");
        Assert.Single(playing, p => p.Clip == "wind.wav");
    }

    [Fact]
    public void AScatteredSoundThatCantStartIsSkippedAndTheLayerKeepsSounding()
    {
        var mixer = TestMixer.Make();
        SoundClip broken = SoundClip.Streaming("broken.ogg", 48000, 1, 20, () => throw new InvalidDataException("can't open"));
        var ambience = new AmbienceDefinition(
            [new Layer(Constant("rain.wav", 4800), 0.5f)],
            [new Scattered([broken], 0.5f, 0.5f, 1, 1, 0)]);
        mixer.StartAmbience(ambience, 1, 0);

        // Four seconds is about eight fires.
        for (int i = 0; i < 4 * Mixer.SampleRate / Block; i++)
        {
            float[] stereo = Mix(mixer, Block);
            Assert.Equal(0.5f, stereo[^2], 1e-6f);
        }

        Assert.InRange(mixer.FailedVoices, 6, 9);
        Assert.Equal("can't open", mixer.LastFailure);
        Assert.Equal(4L * Mixer.SampleRate / Block * Block, mixer.Clock);
        Assert.Equal("rain.wav", Assert.Single(Snapshot(mixer)).Clip);
    }

    [Fact]
    public void ALayerThatCantStartStopsTheLayersAlreadyStarted()
    {
        var mixer = TestMixer.Make();
        SoundClip broken = SoundClip.Streaming("broken.ogg", 48000, 1, 20, () => throw new InvalidDataException("can't open"));
        var ambience = new AmbienceDefinition(
            [new Layer(Constant("rain.wav", 4800), 0.5f), new Layer(broken, 0.5f)],
            []);

        var e = Assert.Throws<InvalidDataException>(() => mixer.StartAmbience(ambience, 1, 0));

        Assert.Equal("can't open", e.Message);
        Assert.Empty(Snapshot(mixer));
        Mix(mixer, Block);
        Assert.NotEqual(Mixer.NoAmbience, mixer.StartAmbience(Layers((Constant("wind.wav", 4800), 0.3f)), 1, 0));
    }

    [Fact]
    public void AStartThatFailsDropsNothingEvenWithEveryVoiceTaken()
    {
        var mixer = TestMixer.Make();
        SoundClip effect = Constant("effect.wav", 4800);
        for (int i = 0; i < Mixer.MaxVoices; i++)
        {
            mixer.Start(new VoiceStart { Clip = effect, Channel = Channel.Effects, Volume = 0.1f, Loop = true });
        }

        SoundClip broken = SoundClip.Streaming("broken.ogg", 48000, 1, 20, () => throw new InvalidDataException("can't open"));
        Assert.Throws<InvalidDataException>(() => mixer.Start(new VoiceStart { Clip = broken, Channel = Channel.Effects, Volume = 1f }));

        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(Mixer.MaxVoices, playing.Length);
        Assert.All(playing, p => Assert.Equal("effect.wav", p.Clip));
    }

    [Fact]
    public void MixAllocatesNothingWithAnAmbienceScatteringSounds()
    {
        var mixer = TestMixer.Make();
        var ambience = new AmbienceDefinition(
            [new Layer(Constant("rain.wav", 44100), 0.5f), new Layer(Constant("wind.wav", 4800), 0.3f)],
            [
                new Scattered([Constant("twig.wav", 2000), Constant("owl.wav", 9000)], 0.1f, 0.3f, 0.2f, 0.9f, 0.3f),
                new Scattered([Constant("drip.wav", 500)], 0.05f, 0.2f, 0.5f, 1f, 0f),
            ]);
        var stereo = new float[Block * 2];
        int id = mixer.StartAmbience(ambience, 1, 0.5f);

        // No warm-up: the first scattered sound in each slot must not build a reader while mixing.
        var playing = new PlayingSound[Mixer.MaxVoices];
        int most = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int blocks = 60 * Mixer.SampleRate / Block;
        for (int i = 0; i < blocks; i++)
        {
            mixer.Mix(stereo);
            most = Math.Max(most, mixer.Snapshot(playing));
            if (i % 500 == 0)
            {
                mixer.SetAmbienceVolume(id, i % 1000 == 0 ? 1f : 0.5f, 0.1f);
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(most > 3, $"only {most} voices at most, so scattered sounds barely overlapped");
        Assert.Equal(before, after);
    }
}
