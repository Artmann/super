using NLayer;
using NVorbis;

namespace Super;

/// <summary>What a reader knows about the clip it reads.</summary>
internal interface IClipFormat
{
    int SampleRate { get; }

    int Channels { get; }
}

/// <summary>Streaming readers over compressed audio held in memory.</summary>
internal static class Decoders
{
    public static ISampleReader Ogg(byte[] bytes)
    {
        return new OggReader(bytes);
    }

    public static ISampleReader Mp3(byte[] bytes)
    {
        return new Mp3Reader(bytes);
    }

    private static void SkipFrames(ISampleReader reader, long frames)
    {
        Span<float> scratch = stackalloc float[2048];
        int channels = ((IClipFormat)reader).Channels;
        Span<float> whole = scratch[..(scratch.Length / channels * channels)];
        while (frames > 0)
        {
            int read = reader.Read(whole[..(int)Math.Min(whole.Length, frames * channels)]);
            if (read == 0)
            {
                return;
            }

            frames -= read;
        }
    }

    private static InvalidDataException TooManyChannels(int channels)
    {
        return new InvalidDataException($"only mono and stereo are supported, found {channels} channels");
    }

    private sealed class OggReader : ISampleReader, IClipFormat
    {
        private readonly byte[] _bytes;
        private VorbisReader _reader;

        public OggReader(byte[] bytes)
        {
            _bytes = bytes;
            _reader = Open();
        }

        private VorbisReader Open()
        {
            var reader = new VorbisReader(new MemoryStream(_bytes), closeOnDispose: true);
            if (reader.Channels > 2)
            {
                int channels = reader.Channels;
                reader.Dispose();
                throw TooManyChannels(channels);
            }

            return reader;
        }

        public int SampleRate => _reader.SampleRate;

        public int Channels => _reader.Channels;

        public long Frames => _reader.TotalSamples;

        public int Read(Span<float> interleaved)
        {
            int samples = _reader.ReadSamples(interleaved[..(interleaved.Length / Channels * Channels)]);
            return samples / Channels;
        }

        public void Seek(long frame)
        {
            // NVorbis's own seek lands about 30 frames late in this encoder's files, so decode from the start instead.
            _reader.Dispose();
            _reader = Open();
            SkipFrames(this, Math.Clamp(frame, 0, Frames));
        }

        public void Dispose()
        {
            _reader.Dispose();
        }
    }

    private sealed class Mp3Reader : ISampleReader, IClipFormat
    {
        private readonly byte[] _bytes;
        private MpegFile _file;

        public Mp3Reader(byte[] bytes)
        {
            _bytes = bytes;
            _file = Open();
        }

        private MpegFile Open()
        {
            var file = new MpegFile(new MemoryStream(_bytes));
            if (file.Channels > 2)
            {
                int channels = file.Channels;
                file.Dispose();
                throw TooManyChannels(channels);
            }

            return file;
        }

        public int SampleRate => _file.SampleRate;

        public int Channels => _file.Channels;

        // MpegFile measures its length in bytes of float samples.
        public long Frames => _file.Length / (sizeof(float) * Channels);

        public int Read(Span<float> interleaved)
        {
            int samples = _file.ReadSamples(interleaved[..(interleaved.Length / Channels * Channels)]);
            return samples / Channels;
        }

        public void Seek(long frame)
        {
            // NLayer only seeks to whole 1152-frame granules and starts them without their overlap, so decode from the start instead.
            _file.Dispose();
            _file = Open();
            SkipFrames(this, Math.Clamp(frame, 0, Frames));
        }

        public void Dispose()
        {
            _file.Dispose();
        }
    }
}
