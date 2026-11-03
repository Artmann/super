namespace Super;

/// <summary>Reads interleaved float samples from a clip. A frame is one sample per channel.</summary>
internal interface ISampleReader
{
    /// <summary>Total frames in the clip.</summary>
    long Frames { get; }

    /// <summary>Fills the span with whole frames and returns how many frames it read, or 0 at the end.</summary>
    int Read(Span<float> interleaved);

    /// <summary>Moves the next read to this frame.</summary>
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

    /// <summary>A reader at the start of the clip. A decoded clip reads from its shared array.</summary>
    public ISampleReader Open()
    {
        if (_samples is not null)
        {
            return new ArrayReader(_samples, Channels);
        }

        return _decoder!();
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
            ISampleReader first = open();
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

    private sealed class ArrayReader(float[] samples, int channels) : ISampleReader
    {
        private long _frame;

        public long Frames { get; } = samples.Length / channels;

        public int Read(Span<float> interleaved)
        {
            int frames = (int)Math.Min(interleaved.Length / channels, Frames - _frame);
            samples.AsSpan((int)(_frame * channels), frames * channels).CopyTo(interleaved);
            _frame += frames;
            return frames;
        }

        public void Seek(long frame)
        {
            _frame = Math.Clamp(frame, 0, Frames);
        }
    }
}
