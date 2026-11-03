using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Super;

/// <summary>
/// Writes mixed sound to a WAV file: 48 kHz, stereo, 32-bit float. The sizes in the header aren't known until
/// the end, so <see cref="Finish"/> goes back and fixes them.
/// </summary>
internal sealed class AudioRecording : IDisposable
{
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private const int HeaderBytes = 44;

    private readonly Stream _file;
    private long _frames;
    private bool _finished;

    public AudioRecording(string wavPath)
        : this(OpenNew(wavPath))
    {
    }

    /// <summary>Writes to this stream instead of a file, which must be seekable. For tests.</summary>
    internal AudioRecording(Stream stream)
    {
        _file = stream;
        _file.Write(Header(0));
    }

    /// <summary>Frames written so far.</summary>
    public long Frames => _frames;

    /// <summary>Adds interleaved left/right samples. The length must be even.</summary>
    public void Write(ReadOnlySpan<float> stereo)
    {
        if (stereo.Length % Channels != 0)
        {
            throw new ArgumentException("Stereo samples come in left/right pairs, so the length must be even.", nameof(stereo));
        }

        ObjectDisposedException.ThrowIf(_finished, this);
        _file.Write(MemoryMarshal.AsBytes(stereo));
        _frames += stereo.Length / Channels;
    }

    /// <summary>Fixes the header and closes the file. Calling it again does nothing.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            _file.Flush();
            _file.Position = 0;
            _file.Write(Header(_frames));
        }
        finally
        {
            _file.Dispose();
        }
    }

    public void Dispose() => Finish();

    private static FileStream OpenNew(string wavPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(wavPath);
        return new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
    }

    private static byte[] Header(long frames)
    {
        long dataBytes = Math.Min(frames * BytesPerFrame, uint.MaxValue - 36);
        var header = new byte[HeaderBytes];
        var span = header.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 3); // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], Mixer.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(Mixer.SampleRate * BytesPerFrame));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 32);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        return header;
    }
}
