using Super.Testing;

namespace Super.Tests.Audio;

/// <summary>
/// Ducking, heard in the mixed output. Music is a constant 1 that loops, so a sample is its gain; the sounds that set
/// ducks off are silent, so they add nothing. Blocks are 480 frames, a hundredth of a second. At -10 dB a gain is
/// 10^(-10/20) = 0.31623, at -5 dB 0.56234, at -6 dB 0.50119 and at -3 dB 0.70795.
/// </summary>
public class DuckingTests
{
    private const int Block = 480;

    private static SoundClip Constant(string path, float value)
    {
        var samples = new float[Mixer.SampleRate];
        Array.Fill(samples, value);
        return SoundClip.Read(path, TestWav.Make(Mixer.SampleRate, 1, samples, asFloat: true));
    }

    private static VoiceStart Looping(string path, Channel channel, float value)
    {
        return new VoiceStart { Clip = Constant(path, value), Channel = channel, Volume = 1, Loop = true };
    }

    /// <summary>Mixes this many seconds in blocks and returns the last block's last left sample: the music's gain.</summary>
    private static float MixFor(Mixer mixer, double seconds)
    {
        var stereo = new float[Block * 2];
        int blocks = (int)Math.Round(seconds * Mixer.SampleRate / Block);
        for (int i = 0; i < blocks; i++)
        {
            mixer.Mix(stereo);
        }

        return stereo[^2];
    }

    private static Mixer WithMusic()
    {
        var mixer = TestMixer.Make();
        mixer.Start(Looping("music.wav", Channel.Music, 1));
        return mixer;
    }

    [Fact]
    public void MusicDipsTenDecibelsUnderDialogueOverTheAttack()
    {
        Mixer mixer = WithMusic();
        Assert.Equal(1f, MixFor(mixer, 0.05), 1e-5f);

        mixer.Start(Looping("line.wav", Channel.Dialogue, 0));

        Assert.Equal(0.56234f, MixFor(mixer, 0.05), 1e-4f);
        Assert.Equal(0.31623f, MixFor(mixer, 0.05), 1e-4f);
        Assert.Equal(0.31623f, MixFor(mixer, 0.5), 1e-4f);
    }

    [Fact]
    public void MusicComesBackOverTheReleaseOnceDialogueStops()
    {
        Mixer mixer = WithMusic();
        int line = mixer.Start(Looping("line.wav", Channel.Dialogue, 0));
        MixFor(mixer, 0.2);

        mixer.Stop(line, 0);

        Assert.Equal(0.56234f, MixFor(mixer, 0.4), 1e-4f);
        Assert.Equal(1f, MixFor(mixer, 0.4), 1e-5f);
    }

    [Fact]
    public void DialogueFadingOutLetsTheMusicComeBack()
    {
        Mixer mixer = WithMusic();
        int line = mixer.Start(Looping("line.wav", Channel.Dialogue, 0));
        MixFor(mixer, 0.2);

        // Stopping counts from when it was asked, not when the fade ends.
        mixer.Stop(line, 1);

        Assert.Equal(0.56234f, MixFor(mixer, 0.4), 1e-4f);
    }

    [Fact]
    public void AmbienceDipsSixDecibelsUnderDialogue()
    {
        var mixer = TestMixer.Make();
        mixer.Start(Looping("wind.wav", Channel.Ambience, 1));
        mixer.Start(Looping("line.wav", Channel.Dialogue, 0));

        Assert.Equal(0.50119f, MixFor(mixer, 0.2), 1e-4f);
    }

    [Fact]
    public void EffectsDoNotDuckMusicWithoutARule()
    {
        Mixer mixer = WithMusic();
        mixer.Start(Looping("bang.wav", Channel.Effects, 0));

        Assert.Equal(1f, MixFor(mixer, 0.2), 1e-5f);
    }

    [Fact]
    public void ZeroDecibelsRemovesARule()
    {
        Mixer mixer = WithMusic();
        mixer.Listening.Ducks.SetRule(Channel.Music, Channel.Dialogue, 0, 0.1f, 0.8f);
        mixer.Start(Looping("line.wav", Channel.Dialogue, 0));

        Assert.Equal(1f, MixFor(mixer, 0.2), 1e-5f);
    }

    [Fact]
    public void TheDeepestDuckWins()
    {
        Mixer mixer = WithMusic();
        mixer.Listening.Ducks.SetRule(Channel.Music, Channel.Effects, -6, 0, 0);
        mixer.Start(Looping("bang.wav", Channel.Effects, 0));
        Assert.Equal(0.50119f, MixFor(mixer, 0.01), 1e-4f);

        mixer.Start(Looping("line.wav", Channel.Dialogue, 0));

        Assert.Equal(0.31623f, MixFor(mixer, 0.2), 1e-4f);
    }

    [Fact]
    public void DuckForHoldsThenComesBack()
    {
        Mixer mixer = WithMusic();
        mixer.Listening.Ducks.DuckFor(Channel.Music, -6, holdSeconds: 0.2f, attackSeconds: 0.1f, releaseSeconds: 0.4f);

        Assert.Equal(0.50119f, MixFor(mixer, 0.1), 1e-4f);
        Assert.Equal(0.50119f, MixFor(mixer, 0.1), 1e-4f);
        Assert.Equal(0.70795f, MixFor(mixer, 0.2), 1e-4f);
        Assert.Equal(1f, MixFor(mixer, 0.2), 1e-5f);
    }

    [Fact]
    public void DuckForAgainKeepsTheDeeperDipAndTheLaterEnd()
    {
        Mixer mixer = WithMusic();
        mixer.Listening.Ducks.DuckFor(Channel.Music, -10, holdSeconds: 0.5f, attackSeconds: 0, releaseSeconds: 0);
        MixFor(mixer, 0.1);

        // Shallower and ending sooner, so neither wins.
        mixer.Listening.Ducks.DuckFor(Channel.Music, -3, holdSeconds: 0.1f, attackSeconds: 0, releaseSeconds: 0);
        Assert.Equal(0.31623f, MixFor(mixer, 0.39), 1e-4f);

        // Now past the first one's end, half a second in.
        Assert.Equal(1f, MixFor(mixer, 0.02), 1e-5f);
    }

    [Fact]
    public void DuckForAfterOneEndedStartsAfresh()
    {
        Mixer mixer = WithMusic();
        mixer.Listening.Ducks.DuckFor(Channel.Music, -10, holdSeconds: 0.1f, attackSeconds: 0, releaseSeconds: 0);
        MixFor(mixer, 0.2);

        mixer.Listening.Ducks.DuckFor(Channel.Music, -6, holdSeconds: 0.1f, attackSeconds: 0, releaseSeconds: 0);

        Assert.Equal(0.50119f, MixFor(mixer, 0.05), 1e-4f);
    }

    [Fact]
    public void SnapshotShowsTheDuckedGain()
    {
        Mixer mixer = WithMusic();
        mixer.Start(Looping("line.wav", Channel.Dialogue, 0));
        MixFor(mixer, 0.2);

        var into = new PlayingSound[Mixer.MaxVoices];
        int count = mixer.Snapshot(into);

        PlayingSound music = Assert.Single(into[..count], sound => sound.Channel == Channel.Music);
        Assert.Equal(0.31623f, music.Gain, 1e-4f);
    }
}
