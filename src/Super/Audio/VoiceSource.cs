namespace Super;

/// <summary>
/// The clip a voice plays, read through a <see cref="VoiceBuffer"/> so the voice can interpolate between source
/// frames, and the voice's place in it. It keeps its decoded reader between sounds, so starting a short clip
/// allocates nothing.
/// </summary>
internal sealed class VoiceSource
{
    private readonly VoiceBuffer _buffer = new();

    // A decoded clip's reader, kept for the next decoded clip played in this slot.
    private ISampleReader? _decoded = SoundClip.NewDecodedReader();
    private ISampleReader _reader = null!;
    private bool _loop;
    private bool _sourceEnded;
    private double _cursor;

    /// <summary>What went wrong if the clip's reader threw while reading.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// Opens the start's clip at about its <see cref="VoiceStart.StartFrame"/>, then lets go of the reader it had if
    /// <paramref name="wasPlaying"/>. The clip is opened first, so one that fails to open throws with this source as it was.
    /// </summary>
    public void Open(in VoiceStart start, bool wasPlaying)
    {
        ISampleReader? old = wasPlaying ? _reader : null;
        ISampleReader? decoded = _decoded;
        ISampleReader reader = VoiceReaders.Open(start.Clip, ref decoded);
        long from = VoiceReaders.SeekNear(reader, decoded, start.StartFrame);

        // Nothing above changed this source, so a start that threw leaves whatever it was playing alone.
        if (old is not null && old != reader && old != decoded)
        {
            VoiceReaders.DisposeQuietly(old);
        }

        _decoded = decoded;
        _reader = reader;
        _loop = start.Loop;
        _buffer.StartAt(from, start.Clip.Channels);
        _sourceEnded = false;
        _cursor = from;
        Failure = null;
    }

    /// <summary>Lets go of the reader, disposing it unless it's the decoded reader kept for the next clip.</summary>
    public void Close()
    {
        ISampleReader reader = _reader;
        _reader = null!;
        if (reader != _decoded)
        {
            VoiceReaders.DisposeQuietly(reader);
        }
    }

    /// <summary>Seconds into the clip of the next frame to be read, at the clip's own rate.</summary>
    public double Seconds(int sampleRate)
    {
        double frame = _cursor;
        long frames = _reader.Frames;
        if (_loop && frames > 0)
        {
            frame %= frames;
        }

        return frame / sampleRate;
    }

    /// <summary>
    /// The two sides at the cursor, read between source frames. A mono clip gives the same on both. Returns false once
    /// the clip has run out or failed.
    /// </summary>
    public bool TryReadFrame(out float left, out float right)
    {
        long index = (long)_cursor;
        Fetch(index);
        if (Failure is not null || index >= _buffer.End)
        {
            left = 0;
            right = 0;
            return false;
        }

        float t = (float)(_cursor - index);
        left = _buffer.Interpolate(index, 0, t);
        right = _buffer.Stereo ? _buffer.Interpolate(index, 1, t) : left;
        return true;
    }

    /// <summary>Moves the cursor on by this many source frames.</summary>
    public void MoveOn(double frames)
    {
        _cursor += frames;
    }

    /// <summary>Reads on until the buffer holds this frame and the one after it, or the clip has run out.</summary>
    private void Fetch(long frame)
    {
        while (frame + 1 >= _buffer.End && !_sourceEnded)
        {
            _buffer.KeepFrom(frame);
            ReadMore();
        }
    }

    /// <summary>Fills the rest of the buffer from the reader, going back to the start of a looping clip at its end.</summary>
    private void ReadMore()
    {
        Span<float> free = _buffer.FreeSpace;
        int read;
        try
        {
            read = _reader.Read(free);
            if (read == 0 && _loop)
            {
                _reader.Seek(0);
                read = _reader.Read(free);
            }
        }
        catch (Exception e)
        {
            // Only the clip's reader is caught, such as a corrupt frame far into a streamed file.
            Failure = e;
            _sourceEnded = true;
            return;
        }

        if (read == 0)
        {
            _sourceEnded = true;
        }

        _buffer.Added(read);
    }
}

/// <summary>
/// Source frames a voice has read and not yet played past, interleaved, up to two channels. Frame numbers count on
/// across loops, so frame <c>Frames</c> of a clip is its frame 0 again.
/// </summary>
internal sealed class VoiceBuffer
{
    private const int FrameCapacity = 1024;

    private readonly float[] _samples = new float[FrameCapacity * 2];
    private int _channels;
    private long _first;
    private int _held;

    /// <summary>The source frame number just past the last one held.</summary>
    public long End => _first + _held;

    public bool Stereo => _channels == 2;

    /// <summary>The room after the frames held, as whole frames.</summary>
    public Span<float> FreeSpace => _samples.AsSpan(_held * _channels, (FrameCapacity - _held) * _channels);

    /// <summary>Empties the buffer for a new clip, whose first read will be source frame <paramref name="from"/>.</summary>
    public void StartAt(long from, int channels)
    {
        _channels = channels;
        _first = from;
        _held = 0;
    }

    /// <summary>Counts in frames just read into <see cref="FreeSpace"/>.</summary>
    public void Added(int frames)
    {
        _held += frames;
    }

    /// <summary>Moves the frames from this one on to the front, or empties it if none of them are held.</summary>
    public void KeepFrom(long frame)
    {
        long end = End;
        if (frame >= end)
        {
            _first = end;
            _held = 0;
            return;
        }

        int keep = (int)(end - frame);
        int from = (int)(frame - _first) * _channels;
        _samples.AsSpan(from, keep * _channels).CopyTo(_samples);
        _first = frame;
        _held = keep;
    }

    /// <summary>The channel's sample at <paramref name="t"/> of the way from this frame to the next.</summary>
    public float Interpolate(long index, int channel, float t)
    {
        float a = Sample(index, channel);
        float b = Sample(index + 1, channel);
        return a + (b - a) * t;
    }

    private float Sample(long frame, int channel)
    {
        long at = frame - _first;
        if (at < 0 || at >= _held)
        {
            return 0;
        }

        return _samples[at * _channels + channel];
    }
}

/// <summary>Opening, seeking and closing the clip readers a <see cref="VoiceSource"/> plays from.</summary>
internal static class VoiceReaders
{
    /// <summary>
    /// A reader at the start of the clip. A decoded clip reuses <paramref name="decoded"/> and sets it to the reader
    /// it returns; a streamed clip opens a new decoder and leaves it alone.
    /// </summary>
    public static ISampleReader Open(SoundClip clip, ref ISampleReader? decoded)
    {
        if (clip.Streamed)
        {
            return clip.Open();
        }

        decoded = clip.Open(decoded);
        return decoded;
    }

    /// <summary>
    /// Seeks a new reader to about the frame, kept inside the clip, and returns the frame it starts from. A reader that
    /// fails to seek is disposed, unless it's the kept decoded one, before the error goes on.
    /// </summary>
    public static long SeekNear(ISampleReader reader, ISampleReader? decoded, long startFrame)
    {
        long from = startFrame > 0 ? Math.Min(startFrame, Math.Max(0, reader.Frames - 1)) : 0;
        if (from <= 0)
        {
            return 0;
        }

        try
        {
            reader.SeekNear(from);
        }
        catch
        {
            if (reader != decoded)
            {
                reader.Dispose();
            }

            throw;
        }

        return from;
    }

    /// <summary>Disposes a reader whose sound is over. One that won't dispose has nothing more to tell and mustn't stop the next sound.</summary>
    public static void DisposeQuietly(ISampleReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch (Exception)
        {
            // Nothing to do: the sound is over either way.
        }
    }
}
