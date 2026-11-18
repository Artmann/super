using System.Buffers.Binary;

namespace Super;

/// <summary>Reads 16-bit PCM and 32-bit float WAV bytes, one frame at a time.</summary>
internal sealed class WavReader : ISampleReader, IClipFormat
{
    private readonly byte[] _bytes;
    private readonly int _dataStart;
    private readonly bool _isFloat;
    private long _frame;

    public int SampleRate { get; }

    public int Channels { get; }

    public long Frames { get; }

    private WavReader(byte[] bytes, int dataStart, long frames, WavFormat format)
    {
        _bytes = bytes;
        _dataStart = dataStart;
        Frames = frames;
        SampleRate = format.Rate;
        Channels = format.Channels;
        _isFloat = format.IsFloat;
    }

    /// <summary>Walks the RIFF chunks to find the format and the samples. Throws InvalidDataException when it can't.</summary>
    public static WavReader Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        var format = default(WavFormat);
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            ReadOnlySpan<byte> id = bytes.AsSpan(at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4, 4));
            int body = at + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                format = ReadFormat(bytes, body, size);
            }
            else if (id.SequenceEqual("data"u8))
            {
                return ReadData(bytes, body, size, format);
            }

            at = (int)Math.Min(int.MaxValue, body + size + (size & 1));
        }

        throw new InvalidDataException(format.Format == 0 ? "there is no fmt chunk" : "there is no data chunk");
    }

    private static WavFormat ReadFormat(byte[] bytes, int body, long size)
    {
        if (size < 16 || body + 16 > bytes.Length)
        {
            throw new InvalidDataException("the fmt chunk is cut short");
        }

        int format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2));
        if (format == 0xFFFE && size >= 26 && body + 26 <= bytes.Length)
        {
            format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 24, 2));
        }

        return new WavFormat(
            format,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2, 2)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14, 2)));
    }

    private static WavReader ReadData(byte[] bytes, int body, long size, WavFormat format)
    {
        (_, int channels, int rate, int bits) = format;
        if (channels == 0)
        {
            throw new InvalidDataException("the data chunk comes before the fmt chunk");
        }

        if (!format.IsFloat && !(format.Format == 1 && bits == 16))
        {
            throw new InvalidDataException($"only 16-bit PCM and 32-bit float are supported, found format {format.Format} with {bits} bits");
        }

        if (channels > 2 || rate <= 0)
        {
            throw new InvalidDataException($"only mono and stereo are supported, found {channels} channels at {rate} Hz");
        }

        long available = Math.Min(size, bytes.Length - body);
        int frameBytes = channels * bits / 8;
        return new WavReader(bytes, body, available / frameBytes, format);
    }

    /// <summary>What a fmt chunk says: the sample format code, channel count, sample rate and bits per sample.</summary>
    private readonly record struct WavFormat(int Format, int Channels, int Rate, int Bits)
    {
        /// <summary>Whether the samples are 32-bit floats rather than 16-bit whole numbers.</summary>
        public bool IsFloat => Format == 3 && Bits == 32;
    }

    public int Read(Span<float> interleaved)
    {
        long wanted = interleaved.Length / Channels;
        int frames = (int)Math.Min(wanted, Frames - _frame);
        int count = frames * Channels;
        int bytesPerSample = _isFloat ? 4 : 2;
        int at = _dataStart + (int)(_frame * Channels * bytesPerSample);
        for (int i = 0; i < count; i++)
        {
            if (_isFloat)
            {
                interleaved[i] = BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(at + i * 4, 4));
            }
            else
            {
                interleaved[i] = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(at + i * 2, 2)) / 32768f;
            }
        }

        _frame += frames;
        return frames;
    }

    public void Dispose()
    {
    }

    public void Seek(long frame)
    {
        _frame = Math.Clamp(frame, 0, Frames);
    }
}
