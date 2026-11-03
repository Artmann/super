using System.Numerics;

namespace Super.Tests.Audio;

public class MixerTests
{
    private const float Centre = 0.70710678f;

    private static SoundClip Constant(string path, int rate, int frames, float value = 1f)
    {
        var samples = new float[frames];
        Array.Fill(samples, value);
        return SoundClip.Read(path, TestWav.Make(rate, 1, samples, asFloat: true));
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

    private static VoiceStart Flat(SoundClip clip, Channel channel = Channel.Interface, float volume = 1, bool loop = false)
    {
        return new VoiceStart { Clip = clip, Channel = channel, Volume = volume, Loop = loop };
    }

    [Fact]
    public void FlatConstantClipMixesAtChannelTimesMaster()
    {
        var mixer = new Mixer();
        mixer.SetChannelVolume(Channel.Interface, 0.5f);
        mixer.SetChannelVolume(Channel.Master, 0.8f);
        mixer.Start(Flat(Constant("click.wav", 48000, 4800)));

        float[] stereo = Mix(mixer, 1000);

        foreach (float sample in stereo)
        {
            Assert.Equal(0.4f, sample, 1e-6f);
        }
    }

    [Fact]
    public void ClipAt44100PlaysForItsLengthAt48000()
    {
        var mixer = new Mixer();
        mixer.Start(Flat(Constant("a.wav", 44100, 4410)));

        float[] stereo = Mix(mixer, 6000);

        int sounding = 0;
        while (sounding < 6000 && stereo[sounding * 2] != 0)
        {
            sounding++;
        }

        Assert.InRange(sounding, 4799, 4801);
        for (int i = sounding * 2; i < stereo.Length; i++)
        {
            Assert.Equal(0f, stereo[i]);
        }

        Assert.Empty(Snapshot(mixer));
    }

    [Fact]
    public void LoopingClipNeverRunsOut()
    {
        var mixer = new Mixer();
        mixer.Start(Flat(Constant("loop.wav", 44100, 4410), loop: true));

        for (int block = 0; block < 3 * 48000 / 512; block++)
        {
            float[] stereo = Mix(mixer, 512);
            foreach (float sample in stereo)
            {
                Assert.Equal(1f, sample, 1e-5f);
            }
        }

        Assert.Single(Snapshot(mixer));
    }

    [Fact]
    public void StreamedClipLoopsBackToItsStart()
    {
        var mixer = new Mixer();
        SoundClip clip = Constant("song.wav", 8000, 84000); // 10.5 s, past the decode limit
        Assert.True(clip.Streamed);
        mixer.Start(Flat(clip, Channel.Music, loop: true));

        // 11 s at 48 kHz, in blocks, so the clip runs out and starts again.
        for (int block = 0; block < 11 * 48000 / 480; block++)
        {
            float[] stereo = Mix(mixer, 480);
            foreach (float sample in stereo)
            {
                Assert.Equal(1f, sample, 1e-5f);
            }
        }

        Assert.Equal(0.5, Assert.Single(Snapshot(mixer)).Seconds, 1e-6);
    }

    [Fact]
    public void FadeInRisesOverItsSeconds()
    {
        var mixer = new Mixer();
        VoiceStart start = Flat(Constant("rise.wav", 48000, 48000));
        start.FadeInSeconds = 0.1f;
        mixer.Start(start);

        float[] stereo = Mix(mixer, 9600);

        Assert.Equal(0f, stereo[0], 1e-6f);
        Assert.Equal(0.5f, stereo[2400 * 2], 1e-4f);
        Assert.Equal(0.5f, stereo[2400 * 2 + 1], 1e-4f);
        for (int frame = 4800; frame < 9600; frame++)
        {
            Assert.Equal(1f, stereo[frame * 2]);
            Assert.Equal(1f, stereo[frame * 2 + 1]);
        }
    }

    [Fact]
    public void StopFadesThenRemoves()
    {
        var mixer = new Mixer();
        int voice = mixer.Start(Flat(Constant("hum.wav", 48000, 4800), loop: true));
        Mix(mixer, 512);

        mixer.Stop(voice, 0.1f);
        Mix(mixer, 2400);
        PlayingSound halfway = Assert.Single(Snapshot(mixer));
        Assert.Equal(0.5f, halfway.Gain, 1e-4f);

        Mix(mixer, 2400);
        Assert.Empty(Snapshot(mixer));
    }

    [Fact]
    public void PlacedSoundLeftOfListenerIsOnlyLeft()
    {
        var mixer = new Mixer();
        mixer.SetListener(Vector3.Zero, -Vector3.UnitZ);
        mixer.Start(new VoiceStart
        {
            Clip = Constant("bang.wav", 48000, 4800),
            Channel = Channel.Effects,
            Position = new Vector3(-10, 0, 0),
            Near = 2,
            Far = 50,
        });

        float[] stereo = Mix(mixer, 100);

        for (int frame = 0; frame < 100; frame++)
        {
            Assert.Equal(0.16667f, stereo[frame * 2], 1e-4f);
            Assert.Equal(0f, stereo[frame * 2 + 1], 1e-4f);
        }
    }

    [Fact]
    public void StereoClipPlacedInWorldIsFoldedToMono()
    {
        var mixer = new Mixer();
        var samples = new float[200];
        for (int frame = 0; frame < 100; frame++)
        {
            samples[frame * 2] = 1;
        }

        SoundClip clip = SoundClip.Read("lr.wav", TestWav.Make(48000, 2, samples, asFloat: true));
        mixer.Start(new VoiceStart { Clip = clip, Channel = Channel.Effects, Position = Vector3.Zero });

        float[] stereo = Mix(mixer, 50);

        foreach (float sample in stereo)
        {
            Assert.Equal(0.5f * Centre, sample, 1e-5f);
        }
    }

    [Fact]
    public void FlatStereoClipKeepsItsChannels()
    {
        var mixer = new Mixer();
        var samples = new float[200];
        for (int frame = 0; frame < 100; frame++)
        {
            samples[frame * 2] = 1;
        }

        SoundClip clip = SoundClip.Read("lr.wav", TestWav.Make(48000, 2, samples, asFloat: true));
        mixer.Start(Flat(clip));

        float[] stereo = Mix(mixer, 50);

        for (int frame = 0; frame < 50; frame++)
        {
            Assert.Equal(1f, stereo[frame * 2]);
            Assert.Equal(0f, stereo[frame * 2 + 1]);
        }
    }

    [Fact]
    public void WorldRateZeroHoldsPlacedVoicePosition()
    {
        var mixer = new Mixer();
        mixer.SetChannelVolume(Channel.Music, 0);
        mixer.Start(new VoiceStart
        {
            Clip = Ramp("step.wav", 48000),
            Channel = Channel.Effects,
            Position = Vector3.Zero,
            FollowsScale = true,
        });
        mixer.Start(new VoiceStart
        {
            Clip = Constant("song.wav", 48000, 3 * 48000),
            Channel = Channel.Music,
            IsMusic = true,
        });
        Mix(mixer, 1000);

        mixer.SetWorldRate(0);
        float[] paused = Mix(mixer, 48000);
        mixer.SetWorldRate(1);

        Assert.Equal(-1, Array.FindIndex(paused, sample => sample != 0));
        PlayingSound[] playing = Snapshot(mixer);
        PlayingSound step = Assert.Single(playing, p => p.Clip == "step.wav");
        PlayingSound song = Assert.Single(playing, p => p.Clip == "song.wav");
        Assert.Equal(1000.0 / 48000, step.Seconds, 1e-9);
        Assert.Equal(49000.0 / 48000, song.Seconds, 1e-9);

        float[] resumed = Mix(mixer, 1);
        Assert.Equal(Centre * 1000f / 48000, resumed[0], 1e-6f);
        Assert.Equal(Centre * 1000f / 48000, resumed[1], 1e-6f);
    }

    [Fact]
    public void HalfWorldRatePlaysPlacedVoiceAtHalfSpeed()
    {
        var mixer = new Mixer();
        mixer.SetWorldRate(0.5f);
        mixer.Start(new VoiceStart
        {
            Clip = Constant("slow.wav", 48000, 48000),
            Channel = Channel.Effects,
            FollowsScale = true,
        });

        Mix(mixer, 4800);

        Assert.Equal(0.05, Assert.Single(Snapshot(mixer)).Seconds, 1e-9);
    }

    [Fact]
    public void PitchTwoPlaysTwiceAsFast()
    {
        var mixer = new Mixer();
        VoiceStart start = Flat(Constant("high.wav", 48000, 48000));
        start.Pitch = 2;
        mixer.Start(start);

        Mix(mixer, 4800);

        Assert.Equal(0.2, Assert.Single(Snapshot(mixer)).Seconds, 1e-9);
    }

    [Fact]
    public void RampClipIsInterpolatedBetweenFrames()
    {
        var mixer = new Mixer();
        VoiceStart start = Flat(Ramp("ramp.wav", 1000));
        start.Pitch = 0.5f;
        mixer.Start(start);

        float[] stereo = Mix(mixer, 4);

        // Half speed reads frames 0, 0.5, 1 and 1.5; the halves land between the ramp's steps of 0.001.
        Assert.Equal(0f, stereo[0], 1e-7f);
        Assert.Equal(0.0005f, stereo[2], 1e-7f);
        Assert.Equal(0.001f, stereo[4], 1e-7f);
        Assert.Equal(0.0015f, stereo[6], 1e-7f);
    }

    [Fact]
    public void PastTheLimitTheQuietestGoes()
    {
        var mixer = new Mixer();
        SoundClip loud = Constant("loud.wav", 48000, 4800);
        SoundClip quiet = Constant("quiet.wav", 48000, 4800);
        for (int i = 0; i < Mixer.MaxVoices - 1; i++)
        {
            mixer.Start(Flat(loud, Channel.Effects, 0.5f, loop: true));
        }

        mixer.Start(Flat(quiet, Channel.Effects, 0.1f, loop: true));
        int newest = mixer.Start(Flat(loud, Channel.Effects, 0.5f, loop: true));

        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(Mixer.MaxVoices, playing.Length);
        Assert.DoesNotContain(playing, p => p.Clip == "quiet.wav");
        Assert.NotEqual(-1, newest);
    }

    [Fact]
    public void PastTheLimitMusicIsNeverDropped()
    {
        var mixer = new Mixer();
        SoundClip song = Constant("song.wav", 48000, 4800);
        SoundClip shot = Constant("shot.wav", 48000, 4800);
        for (int i = 0; i < Mixer.MaxVoices - 1; i++)
        {
            mixer.Start(new VoiceStart { Clip = song, Channel = Channel.Music, Volume = 0.01f, Loop = true, IsMusic = true });
        }

        mixer.Start(Flat(shot, Channel.Effects, 1f, loop: true));
        mixer.Start(Flat(shot, Channel.Effects, 1f, loop: true));

        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(Mixer.MaxVoices, playing.Length);
        Assert.Equal(Mixer.MaxVoices - 1, playing.Count(p => p.Clip == "song.wav"));
        Assert.Equal(1, playing.Count(p => p.Clip == "shot.wav"));
    }

    [Fact]
    public void PastTheLimitANewQuietestVoiceIsTheOneDropped()
    {
        var mixer = new Mixer();
        SoundClip loud = Constant("loud.wav", 48000, 4800);
        SoundClip quiet = Constant("quiet.wav", 48000, 4800);
        for (int i = 0; i < Mixer.MaxVoices; i++)
        {
            mixer.Start(Flat(loud, Channel.Effects, 0.5f, loop: true));
        }

        int dropped = mixer.Start(Flat(quiet, Channel.Effects, 0.1f, loop: true));

        Assert.Equal(-1, dropped);
        PlayingSound[] playing = Snapshot(mixer);
        Assert.Equal(Mixer.MaxVoices, playing.Length);
        Assert.All(playing, p => Assert.Equal("loud.wav", p.Clip));

        // Calls on the dropped id do nothing.
        mixer.Stop(dropped, 0);
        mixer.SetVolume(dropped, 0, 0);
        mixer.SetPosition(dropped, Vector3.One);
        Assert.Equal(Mixer.MaxVoices, Snapshot(mixer).Length);
        Assert.All(Snapshot(mixer), p => Assert.Equal(0.5f, p.Gain));
    }

    [Fact]
    public void StaleIdDoesNotTouchTheVoiceThatReusedItsSlot()
    {
        var mixer = new Mixer();
        int old = mixer.Start(Flat(Constant("short.wav", 48000, 100)));
        Mix(mixer, 200);
        Assert.Empty(Snapshot(mixer));

        int next = mixer.Start(Flat(Constant("long.wav", 48000, 48000)));
        mixer.Stop(old, 0);
        mixer.SetVolume(old, 0, 0);
        Mix(mixer, 100);

        Assert.NotEqual(old, next);
        PlayingSound playing = Assert.Single(Snapshot(mixer));
        Assert.Equal("long.wav", playing.Clip);
        Assert.Equal(1f, playing.Gain);
    }

    [Fact]
    public void SetVolumeFadesToTheNewVolume()
    {
        var mixer = new Mixer();
        int voice = mixer.Start(Flat(Constant("hum.wav", 48000, 48000)));

        mixer.SetVolume(voice, 0.5f, 0.1f);
        float[] stereo = Mix(mixer, 9600);

        Assert.Equal(1f, stereo[0], 1e-6f);
        Assert.Equal(0.75f, stereo[2400 * 2], 1e-4f);
        Assert.Equal(0.5f, stereo[4800 * 2], 1e-6f);
        Assert.Equal(0.5f, Assert.Single(Snapshot(mixer)).Gain);
    }

    [Fact]
    public void SetPositionMovesThePan()
    {
        var mixer = new Mixer();
        int voice = mixer.Start(new VoiceStart
        {
            Clip = Constant("buzz.wav", 48000, 48000),
            Channel = Channel.Effects,
            Position = new Vector3(-1, 0, 0),
        });
        Mix(mixer, 10);

        mixer.SetPosition(voice, new Vector3(1, 0, 0));
        float[] stereo = Mix(mixer, 10);

        Assert.Equal(0f, stereo[0], 1e-6f);
        Assert.Equal(1f, stereo[1], 1e-6f);
        Assert.Equal(1f, Assert.Single(Snapshot(mixer)).Pan, 1e-6f);
    }

    [Fact]
    public void SnapshotReportsGainAndPan()
    {
        var mixer = new Mixer();
        mixer.SetChannelVolume(Channel.Effects, 0.5f);
        mixer.SetChannelVolume(Channel.Master, 0.8f);
        mixer.Start(new VoiceStart
        {
            Clip = Constant("bang.wav", 48000, 48000),
            Channel = Channel.Effects,
            Volume = 0.5f,
            Position = new Vector3(10, 0, 0),
            Near = 2,
            Far = 50,
        });
        mixer.Start(Flat(Constant("click.wav", 48000, 4800), Channel.Interface, 0.25f, loop: true));
        Mix(mixer, 4800);

        PlayingSound[] playing = Snapshot(mixer);

        PlayingSound bang = Assert.Single(playing, p => p.Clip == "bang.wav");
        Assert.Equal(Channel.Effects, bang.Channel);
        Assert.False(bang.Looping);
        Assert.Equal(0.5f * 0.16667f * 0.5f * 0.8f, bang.Gain, 1e-5f); // volume, falloff(10), channel, master
        Assert.Equal(1f, bang.Pan, 1e-6f);
        Assert.Equal(0.1, bang.Seconds, 1e-9);

        // The looped clip has gone round once and is back at its start.
        PlayingSound click = Assert.Single(playing, p => p.Clip == "click.wav");
        Assert.Equal(Channel.Interface, click.Channel);
        Assert.True(click.Looping);
        Assert.Equal(0.25f * 0.8f, click.Gain, 1e-6f);
        Assert.Equal(0f, click.Pan);
        Assert.Equal(0.0, click.Seconds, 1e-9);
    }

    [Fact]
    public void SnapshotStopsAtTheSpanLength()
    {
        var mixer = new Mixer();
        SoundClip clip = Constant("a.wav", 48000, 4800);
        mixer.Start(Flat(clip));
        mixer.Start(Flat(clip));
        mixer.Start(Flat(clip));

        Assert.Equal(2, mixer.Snapshot(new PlayingSound[2]));
    }

    [Fact]
    public void MixAllocatesNothingOnceWarm()
    {
        var mixer = new Mixer();
        SoundClip clip = Constant("a.wav", 44100, 4410);
        for (int i = 0; i < 10; i++)
        {
            VoiceStart start = Flat(clip, Channel.Effects, 0.5f, loop: true);
            start.FadeInSeconds = 0.5f;
            start.FollowsScale = true;
            if (i % 2 == 0)
            {
                start.Position = new Vector3(i, 0, -3);
            }

            mixer.Start(start);
        }

        var stereo = new float[512 * 2];
        mixer.Mix(stereo);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            mixer.Mix(stereo);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Fact]
    public void StartingADecodedClipAllocatesNothingOnceTheSlotsAreWarm()
    {
        var mixer = new Mixer();
        SoundClip clip = Constant("a.wav", 48000, 100);
        VoiceStart start = Flat(clip, Channel.Effects);
        var stereo = new float[512 * 2];
        for (int i = 0; i < 10; i++)
        {
            mixer.Start(start);
        }

        mixer.Mix(stereo);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 10; i++)
            {
                mixer.Start(start);
            }

            mixer.Mix(stereo);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }
}
