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

    private sealed class OggReader : ISampleReader, IClipFormat
    {
        private readonly VorbisReader _reader;

        public OggReader(byte[] bytes)
        {
            _reader = new VorbisReader(new MemoryStream(bytes), closeOnDispose: true);
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
            _reader.SamplePosition = Math.Clamp(frame, 0, Frames);
        }
    }

    private sealed class Mp3Reader : ISampleReader, IClipFormat
    {
        private readonly MpegFile _file;

        public Mp3Reader(byte[] bytes)
        {
            _file = new MpegFile(new MemoryStream(bytes));
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
            _file.Position = Math.Clamp(frame, 0, Frames) * sizeof(float) * Channels;
        }
    }
}
