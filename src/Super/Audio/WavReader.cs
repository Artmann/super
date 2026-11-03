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

    private WavReader(byte[] bytes, int dataStart, long frames, int rate, int channels, bool isFloat)
    {
        _bytes = bytes;
        _dataStart = dataStart;
        Frames = frames;
        SampleRate = rate;
        Channels = channels;
        _isFloat = isFloat;
    }

    /// <summary>Walks the RIFF chunks to find the format and the samples. Throws InvalidDataException when it can't.</summary>
    public static WavReader Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        int format = 0;
        int channels = 0;
        int rate = 0;
        int bits = 0;
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            ReadOnlySpan<byte> id = bytes.AsSpan(at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4, 4));
            int body = at + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > bytes.Length)
                {
                    throw new InvalidDataException("the fmt chunk is cut short");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2));
                if (format == 0xFFFE && size >= 26 && body + 26 <= bytes.Length)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 24, 2));
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("the data chunk comes before the fmt chunk");
                }

                bool isFloat = format == 3 && bits == 32;
                if (!isFloat && !(format == 1 && bits == 16))
                {
                    throw new InvalidDataException($"only 16-bit PCM and 32-bit float are supported, found format {format} with {bits} bits");
                }

                if (channels > 2 || rate <= 0)
                {
                    throw new InvalidDataException($"only mono and stereo are supported, found {channels} channels at {rate} Hz");
                }

                long available = Math.Min(size, bytes.Length - body);
                int frameBytes = channels * bits / 8;
                return new WavReader(bytes, body, available / frameBytes, rate, channels, isFloat);
            }

            at = (int)Math.Min(int.MaxValue, body + size + (size & 1));
        }

        throw new InvalidDataException(format == 0 ? "there is no fmt chunk" : "there is no data chunk");
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
