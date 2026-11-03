namespace Super.Tests.Audio;

public class SoundClipTests
{
    private static float[] ReadAll(SoundClip clip)
    {
        ISampleReader reader = clip.Open();
        var all = new List<float>();
        var buffer = new float[256 * clip.Channels];
        int frames;
        while ((frames = reader.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, frames * clip.Channels).ToArray());
        }

        return all.ToArray();
    }

    private static SoundClip ReadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", name);
        return SoundClip.Read(name, File.ReadAllBytes(path));
    }

    [Fact]
    public void Wav16BitMonoReadsSamplesAndRate()
    {
        float[] samples = [0f, 0.5f, -0.5f];
        SoundClip clip = SoundClip.Read("a.wav", TestWav.Make(44100, 1, samples, asFloat: false));

        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(1, clip.Channels);
        float[] read = ReadAll(clip);
        Assert.Equal(3, read.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.InRange(read[i], samples[i] - 1f / 32768f, samples[i] + 1f / 32768f);
        }
    }

    [Fact]
    public void WavFloatStereoKeepsChannelsInterleaved()
    {
        float[] samples = [0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f];
        SoundClip clip = SoundClip.Read("a.wav", TestWav.Make(48000, 2, samples, asFloat: true));

        Assert.Equal(2, clip.Channels);
        Assert.Equal(48000, clip.SampleRate);
        Assert.Equal(samples, ReadAll(clip));
    }

    [Fact]
    public void WavWithExtraChunksBeforeDataStillReads()
    {
        float[] samples = [0.25f, -0.25f, 0.5f];
        SoundClip clip = SoundClip.Read("a.wav", TestWav.Make(22050, 1, samples, asFloat: true, extraChunk: true));

        Assert.Equal(22050, clip.SampleRate);
        Assert.Equal(samples, ReadAll(clip));
    }

    [Fact]
    public void OggToneHasItsLengthAndRate()
    {
        SoundClip clip = ReadFixture("tone.ogg");

        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(1, clip.Channels);
        Assert.InRange(clip.Seconds, 0.48, 0.52);
        Assert.True(ReadAll(clip).Max(Math.Abs) > 0.5f);
    }

    [Fact]
    public void Mp3ToneHasItsLengthAndRate()
    {
        SoundClip clip = ReadFixture("tone.mp3");

        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(1, clip.Channels);
        Assert.InRange(clip.Seconds, 0.44, 0.56);
        Assert.True(ReadAll(clip).Max(Math.Abs) > 0.5f);
    }

    [Fact]
    public void LongClipIsStreamed()
    {
        float[] eleven = new float[11 * 8000];
        float[] one = new float[8000];

        SoundClip longClip = SoundClip.Read("long.wav", TestWav.Make(8000, 1, eleven, asFloat: false));
        SoundClip shortClip = SoundClip.Read("short.wav", TestWav.Make(8000, 1, one, asFloat: false));

        Assert.True(longClip.Streamed);
        Assert.False(shortClip.Streamed);
        Assert.Equal(11.0, longClip.Seconds, 3);
        Assert.Equal(eleven.Length, ReadAll(longClip).Length);
    }

    [Fact]
    public void UnknownExtensionNamesThePath()
    {
        var e = Assert.Throws<ContentException>(() => SoundClip.Read("music/a.flac", [1, 2, 3]));

        Assert.Contains("music/a.flac isn't a sound this can read", e.Message);
        Assert.Contains("Use .wav, .ogg or .mp3.", e.Message);
    }

    [Fact]
    public void BrokenWavNamesThePath()
    {
        var e = Assert.Throws<ContentException>(() => SoundClip.Read("x.wav", [9, 8, 7, 6, 5, 4, 3, 2, 1, 0]));

        Assert.StartsWith("x.wav:", e.Message);
    }

    [Fact]
    public void BrokenOggNamesThePath()
    {
        var e = Assert.Throws<ContentException>(() => SoundClip.Read("x.ogg", [9, 8, 7, 6, 5, 4, 3, 2, 1, 0]));

        Assert.StartsWith("x.ogg:", e.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekThenReadStartsThere(bool streamed)
    {
        int rate = 8000;
        int frames = (streamed ? 11 : 1) * rate;
        float[] samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            samples[i * 2] = i / (float)frames;
            samples[i * 2 + 1] = -i / (float)frames;
        }

        SoundClip clip = SoundClip.Read("a.wav", TestWav.Make(rate, 2, samples, asFloat: true));
        Assert.Equal(streamed, clip.Streamed);
        ISampleReader reader = clip.Open();
        Assert.Equal(frames, reader.Frames);

        reader.Seek(1234);
        float[] buffer = new float[4];
        int read = reader.Read(buffer);

        Assert.Equal(2, read);
        Assert.Equal(new[] { 1234f / frames, -1234f / frames, 1235f / frames, -1235f / frames }, buffer);
    }

    [Fact]
    public void ReadReturnsZeroAtTheEnd()
    {
        SoundClip clip = SoundClip.Read("a.wav", TestWav.Make(8000, 1, [0.1f, 0.2f], asFloat: true));
        ISampleReader reader = clip.Open();
        float[] buffer = new float[8];

        Assert.Equal(2, reader.Read(buffer));
        Assert.Equal(0, reader.Read(buffer));
    }
}
