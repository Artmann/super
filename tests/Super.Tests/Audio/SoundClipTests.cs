using Super.Testing;
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

    [Fact]
    public void OpenWithReuseRebindsTheSameReaderToTheNewClip()
    {
        SoundClip a = SoundClip.Read("a.wav", TestWav.Make(8000, 1, [0.1f, 0.2f, 0.3f], asFloat: true));
        SoundClip b = SoundClip.Read("b.wav", TestWav.Make(8000, 2, [0.5f, -0.5f, 0.6f, -0.6f], asFloat: true));
        ISampleReader first = a.Open();
        first.Read(new float[2]);

        ISampleReader second = b.Open(first);

        Assert.Same(first, second);
        Assert.Equal(2, second.Frames);
        float[] buffer = new float[4];
        Assert.Equal(2, second.Read(buffer));
        Assert.Equal(new[] { 0.5f, -0.5f, 0.6f, -0.6f }, buffer);
    }

    [Fact]
    public void ReusedOpenAndReadAllocateNothing()
    {
        SoundClip a = SoundClip.Read("a.wav", TestWav.Make(8000, 1, new float[100], asFloat: true));
        SoundClip b = SoundClip.Read("b.wav", TestWav.Make(8000, 1, new float[100], asFloat: true));
        float[] buffer = new float[64];
        ISampleReader reader = a.Open();
        reader = b.Open(reader);
        reader.Read(buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        reader = a.Open(reader);
        reader.Read(buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("tone.ogg", 0.02)]
    [InlineData("tone.mp3", 0.06)] // MP3 encoders pad the start and end with silence
    public void CompressedReaderFramesMatchTheRealLength(string name, double toleranceSeconds)
    {
        using ISampleReader reader = OpenDecoder(name);

        Assert.InRange(reader.Frames, (0.5 - toleranceSeconds) * 44100, (0.5 + toleranceSeconds) * 44100);
    }

    [Theory]
    [InlineData("tone.ogg")]
    [InlineData("tone.mp3")]
    public void CompressedReaderSeekThenReadMatchesTheFullRead(string name)
    {
        using ISampleReader whole = OpenDecoder(name);
        float[] all = new float[whole.Frames + 4096];
        int total = 0;
        int got;
        while ((got = whole.Read(all.AsSpan(total))) > 0)
        {
            total += got;
        }

        long middle = total / 2;

        using ISampleReader reader = OpenDecoder(name);
        reader.Seek(middle);
        float[] slice = new float[100];
        int read = reader.Read(slice);

        Assert.Equal(100, read);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(all[middle + i], slice[i], 0.01f);
        }
    }

    // tone.ogg holds all its audio in one Ogg page, where NVorbis's own rewind is a packet late; rising.ogg has many.
    [Theory]
    [InlineData("tone.ogg")]
    [InlineData("rising.ogg")]
    [InlineData("tone.mp3")]
    [InlineData("rising.mp3")]
    public void CompressedReaderSeekToZeroAfterTheEndReadsTheSameAgain(string name)
    {
        using ISampleReader reader = OpenDecoder(name);
        float[] first = ReadToEnd(reader);

        reader.Seek(0);
        float[] second = ReadToEnd(reader);

        Assert.True(first.Length > 20000);
        Assert.Equal(first, second);
    }

    // Opening a new decoder, as Seek(0) once did, costs about 800 KB. NVorbis's rewind allocates some 25 KB of its own;
    // NLayer's nothing.
    [Theory]
    [InlineData("rising.ogg", 64 * 1024)]
    [InlineData("rising.mp3", 0)]
    public void CompressedReaderSeekToZeroRewindsWithoutOpeningAgain(string name, long mostBytes)
    {
        using ISampleReader reader = OpenDecoder(name);
        ReadToEnd(reader);
        reader.Seek(0);
        ReadToEnd(reader);

        long before = GC.GetAllocatedBytesForCurrentThread();
        reader.Seek(0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, mostBytes);
    }

    // The rising fixtures are a 1 kHz tone at 44 100 Hz whose peak rises steadily from 0 to 0.75 over their 4 s, so how
    // loud a stretch is says where in the clip it is: 0.01 is about 2400 frames. NLayer lands on a granule boundary and
    // starts it without its overlap, so the first two granules (2304 frames) after the seek are skipped before measuring.
    [Theory]
    [InlineData("rising.ogg")]
    [InlineData("rising.mp3")]
    public void CompressedReaderSeekNearLandsCloseToTheFrame(string name)
    {
        using ISampleReader whole = OpenDecoder(name);
        float[] all = ReadToEnd(whole);
        const int Settle = 2304;
        const int Window = 441;

        foreach (long frame in new long[] { 44100, 88200, 132300 })
        {
            using ISampleReader reader = OpenDecoder(name);
            reader.SeekNear(frame);
            float[] settle = new float[Settle];
            Assert.Equal(Settle, reader.Read(settle));
            float[] window = new float[Window];
            Assert.Equal(Window, reader.Read(window));

            float expected = all.AsSpan((int)frame + Settle, Window).ToArray().Max(Math.Abs);
            Assert.InRange(window.Max(Math.Abs), expected - 0.01f, expected + 0.01f);
        }
    }

    [Theory]
    [InlineData("rising.ogg")]
    [InlineData("rising.mp3")]
    public void CompressedReaderSeekNearDecodesNothingOnTheWay(string name)
    {
        using ISampleReader reader = OpenDecoder(name);
        reader.Read(new float[16]);
        long before = ((IDecodeCount)reader).FramesDecoded;

        reader.SeekNear(132300);

        Assert.Equal(before, ((IDecodeCount)reader).FramesDecoded);

        // While an exact seek decodes its way there.
        reader.Seek(132300);
        Assert.Equal(before + 132300, ((IDecodeCount)reader).FramesDecoded);
    }

    [Fact]
    public void OggWithMoreThanTwoChannelsNamesThePath()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", "six.ogg"));

        var e = Assert.Throws<ContentException>(() => SoundClip.Read("six.ogg", bytes));

        Assert.Equal("six.ogg: only mono and stereo are supported, found 6 channels", e.Message);
    }

    private static float[] ReadToEnd(ISampleReader reader)
    {
        int channels = ((IClipFormat)reader).Channels;
        var all = new List<float>();
        float[] buffer = new float[1000 * channels];
        int frames;
        while ((frames = reader.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, frames * channels).ToArray());
        }

        return [.. all];
    }

    private static ISampleReader OpenDecoder(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", name));
        return name.EndsWith(".ogg") ? Decoders.Ogg(bytes) : Decoders.Mp3(bytes);
    }
}
