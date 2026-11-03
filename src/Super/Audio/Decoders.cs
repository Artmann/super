using System.Buffers.Binary;
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
        private readonly bool _rewindsExactly;
        private VorbisReader _reader;

        public OggReader(byte[] bytes)
        {
            _bytes = bytes;
            _reader = Open();
            _rewindsExactly = HasSeveralAudioPages(bytes);
        }

        /// <summary>
        /// True unless all the audio is in one Ogg page. NVorbis rewinds to frame 0 exactly, except in that case, where
        /// it starts a packet late. Only a clip of a second or so fits in one page, and a streamed clip is over 10 s.
        /// </summary>
        private static bool HasSeveralAudioPages(byte[] bytes)
        {
            int at = 0;
            while (at + 27 <= bytes.Length && bytes[at] == 'O' && bytes[at + 1] == 'g' && bytes[at + 2] == 'g' && bytes[at + 3] == 'S')
            {
                byte flags = bytes[at + 5];
                long granule = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 6));
                int segments = bytes[at + 26];
                if (at + 27 + segments > bytes.Length)
                {
                    return false;
                }

                int length = 0;
                for (int i = 0; i < segments; i++)
                {
                    length += bytes[at + 27 + i];
                }

                // The header pages have granule 0, and a page in which no packet ends has -1. The first page past those
                // holds the first audio; it must not be the last page.
                if (granule > 0)
                {
                    return (flags & 4) == 0;
                }

                at += 27 + segments + length;
            }

            return false;
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
            frame = Math.Clamp(frame, 0, Frames);
            if (_rewindsExactly)
            {
                // Rewinding the decoder it has is far cheaper than opening a new one, which builds its codebooks again
                // (about 800 KB), so a loop can go round while mixing.
                _reader.SeekTo(0);
            }
            else
            {
                _reader.Dispose();
                _reader = Open();
            }

            if (frame > 0)
            {
                // NVorbis's seek to anywhere else lands about 30 frames late in this encoder's files, so decode on from
                // the start instead.
                SkipFrames(this, frame);
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
        }
    }

    private sealed class Mp3Reader : ISampleReader, IClipFormat
    {
        private readonly byte[] _bytes;
        private readonly MpegFile _file;

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
            frame = Math.Clamp(frame, 0, Frames);

            // Position 0 is exact, since the first granule has no overlap to lose, and rewinding allocates nothing.
            _file.Position = 0;
            if (frame > 0)
            {
                // Elsewhere NLayer only seeks to whole 1152-frame granules and starts them without their overlap, so
                // decode on from the start instead.
                SkipFrames(this, frame);
            }
        }

        public void Dispose()
        {
            _file.Dispose();
        }
    }
}
