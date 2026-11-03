namespace Super;

/// <summary>Reads interleaved float samples from a clip. A frame is one sample per channel.</summary>
internal interface ISampleReader : IDisposable
{
    /// <summary>Total frames in the clip.</summary>
    long Frames { get; }

    /// <summary>Fills the span with whole frames and returns how many frames it read, or 0 at the end.</summary>
    int Read(Span<float> interleaved);

    /// <summary>
    /// Moves the next read to exactly this frame. Seek(0) rewinds the reader it is rather than opening a new decoder, so
    /// a loop can go round while mixing (NVorbis still allocates some 25 KB to rewind; the others nothing).
    /// </summary>
    void Seek(long frame);
}

/// <summary>A sound file read from disk. Short clips are decoded once; long ones are decoded as they play.</summary>
internal sealed class SoundClip
{
    /// <summary>Clips up to this long are decoded once and kept as floats.</summary>
    public const double MaxDecodedSeconds = 10;

    private readonly float[]? _samples;
    private readonly Func<ISampleReader>? _decoder;

    private SoundClip(string path, int sampleRate, int channels, double seconds, float[]? samples, Func<ISampleReader>? decoder)
    {
        Path = path;
        SampleRate = sampleRate;
        Channels = channels;
        Seconds = seconds;
        _samples = samples;
        _decoder = decoder;
    }

    public string Path { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public double Seconds { get; }

    /// <summary>True when the clip is long and each Open makes a new decoder.</summary>
    public bool Streamed => _samples is null;

    /// <summary>
    /// A reader at the start of the clip. A decoded clip reads from its shared array; pass a reader from an
    /// earlier decoded Open to reuse it without allocating. A streamed clip always makes a new decoder.
    /// </summary>
    public ISampleReader Open(ISampleReader? reuse = null)
    {
        if (_samples is not null)
        {
            if (reuse is ArrayReader reader)
            {
                reader.Rebind(_samples, Channels);
                return reader;
            }

            return new ArrayReader(_samples, Channels);
        }

        return _decoder!();
    }

    /// <summary>
    /// A streamed clip over any reader factory, for tests that need a decoder which fails when opened or part way through.
    /// </summary>
    internal static SoundClip Streaming(string path, int sampleRate, int channels, double seconds, Func<ISampleReader> open)
    {
        return new SoundClip(path, sampleRate, channels, seconds, null, open);
    }

    /// <summary>An empty decoded-clip reader, for a voice to hold so that rebinding it to a clip later allocates nothing.</summary>
    internal static ISampleReader NewDecodedReader()
    {
        return new ArrayReader([], 1);
    }

    /// <summary>Reads a clip from its file bytes. The path picks the format and names the file in errors.</summary>
    public static SoundClip Read(string path, byte[] bytes)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        Func<ISampleReader> open = extension switch
        {
            ".wav" => () => WavReader.Parse(bytes),
            ".ogg" => () => Decoders.Ogg(bytes),
            ".mp3" => () => Decoders.Mp3(bytes),
            _ => throw new ContentException([$"{path} isn't a sound this can read. Use .wav, .ogg or .mp3."]),
        };

        try
        {
            using ISampleReader first = open();
            var format = (IClipFormat)first;
            double seconds = (double)first.Frames / format.SampleRate;
            if (seconds > MaxDecodedSeconds)
            {
                return new SoundClip(path, format.SampleRate, format.Channels, seconds, null, open);
            }

            float[] samples = DecodeAll(first, format.Channels);
            seconds = (double)(samples.Length / format.Channels) / format.SampleRate;
            return new SoundClip(path, format.SampleRate, format.Channels, seconds, samples, null);
        }
        catch (Exception e) when (e is not ContentException)
        {
            throw new ContentException([$"{path}: {e.Message}"]);
        }
    }

    private static float[] DecodeAll(ISampleReader reader, int channels)
    {
        var samples = new float[reader.Frames * channels];
        int filled = 0;
        while (true)
        {
            if (filled == samples.Length)
            {
                // The header undercounted. Grow and keep reading.
                Array.Resize(ref samples, Math.Max(samples.Length * 2, 4096 * channels));
            }

            int frames = reader.Read(samples.AsSpan(filled));
            if (frames == 0)
            {
                break;
            }

            filled += frames * channels;
        }

        return filled == samples.Length ? samples : samples[..filled];
    }

    private sealed class ArrayReader : ISampleReader
    {
        private float[] _samples;
        private int _channels;
        private long _frame;

        public ArrayReader(float[] samples, int channels)
        {
            _samples = samples;
            _channels = channels;
            Frames = samples.Length / channels;
        }

        public long Frames { get; private set; }

        public void Rebind(float[] samples, int channels)
        {
            _samples = samples;
            _channels = channels;
            Frames = samples.Length / channels;
            _frame = 0;
        }

        public void Dispose()
        {
        }

        public int Read(Span<float> interleaved)
        {
            int frames = (int)Math.Min(interleaved.Length / _channels, Frames - _frame);
            _samples.AsSpan((int)(_frame * _channels), frames * _channels).CopyTo(interleaved);
            _frame += frames;
            return frames;
        }

        public void Seek(long frame)
        {
            _frame = Math.Clamp(frame, 0, Frames);
        }
    }
}
