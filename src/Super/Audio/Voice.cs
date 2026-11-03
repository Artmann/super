using System.Numerics;

namespace Super;

/// <summary>What <see cref="Mixer.Start"/> needs to play a clip. Volume and pitch default to 1, near to 2 m and far to 50 m.</summary>
internal struct VoiceStart
{
    public SoundClip Clip;
    public Channel Channel;
    public float Volume = 1;
    public bool Loop;
    public float Pitch = 1;

    /// <summary>Where the sound is in the world, or null for a flat sound that keeps its own stereo.</summary>
    public Vector3? Position;
    public float Near = 2;
    public float Far = 50;
    public float FadeInSeconds;

    /// <summary>
    /// About where in the clip to start, for an ambience layer's random start. A decoded clip starts exactly there. A
    /// streamed clip uses its decoder's own quick seek once, when the voice starts, which may land a little off (up to
    /// 1152 frames early for an MP3), rather than decoding up to it, which can take longer than the speakers' buffer.
    /// </summary>
    public long StartFrame;

    /// <summary>-1 (left) to 1 (right) for a flat voice. 0 leaves it as it is. Placed voices pan by position instead.</summary>
    public float Pan;

    /// <summary>True when the world's time scale slows and pauses it.</summary>
    public bool FollowsScale;

    /// <summary>True for music, which the voice limit never drops.</summary>
    public bool IsMusic;

    /// <summary>True for an ambience layer, which the voice limit never drops either.</summary>
    public bool IsLayer;

    public VoiceStart()
    {
        Clip = null!;
    }
}

/// <summary>A value that moves in a straight line to a target over a number of frames.</summary>
internal struct Ramp
{
    private float _from;
    private float _to;
    private int _length;
    private int _elapsed;

    public float Value { get; private set; }

    public readonly float Target => _to;

    public readonly bool Moving => _elapsed < _length;

    public void Set(float value)
    {
        _from = value;
        _to = value;
        Value = value;
        _length = 0;
        _elapsed = 0;
    }

    /// <summary>Moves on this many frames at once, to where <see cref="Step"/> would have got after as many calls.</summary>
    public void Advance(int frames)
    {
        if (_elapsed < _length && frames > 0)
        {
            _elapsed = (int)Math.Min((long)_elapsed + frames, _length);
            Value = _elapsed == _length ? _to : _from + (_to - _from) * _elapsed / _length;
        }
    }

    public void To(float target, int frames)
    {
        if (frames <= 0)
        {
            Set(target);
            return;
        }

        _from = Value;
        _to = target;
        _length = frames;
        _elapsed = 0;
    }

    /// <summary>Moves on one frame.</summary>
    public void Step()
    {
        if (_elapsed < _length)
        {
            _elapsed++;
            Value = _elapsed == _length ? _to : _from + (_to - _from) * _elapsed / _length;
        }
    }
}

/// <summary>
/// One slot of the mixer. It reads its clip through a small buffer so it can interpolate between source frames,
/// and keeps its decoded reader between sounds so starting a short clip allocates nothing.
/// </summary>
internal sealed class Voice
{
    private const int BufferFrames = 1024;

    // A voice held at world rate 0 fades out over this many frames (5 ms), and back in when it resumes.
    private const int HoldRampFrames = 240;

    // Interleaved source frames; up to two channels.
    private readonly float[] _buffer = new float[BufferFrames * 2];

    // A decoded clip's reader, kept for the next decoded clip played in this slot.
    private ISampleReader? _decoded = SoundClip.NewDecodedReader();
    private ISampleReader _reader = null!;
    private int _channels;

    // Source frame numbers count on across loops, so frame Frames is the clip's frame 0 again.
    private long _bufferStart;
    private int _bufferCount;
    private bool _sourceEnded;
    private double _cursor;

    // The gains the last block ended on, which this block ramps from. Not known until the first block.
    private bool _started;
    private float _lastLeft;
    private float _lastRight;

    // How far into the 5 ms ramp a hold or resume is, 0 (held) to HoldRampFrames (running), and the step to keep
    // playing at while it fades out.
    private int _running;
    private double _lastStep;

    public Ramp Volume;
    public Ramp Envelope;

    public int Id { get; private set; }

    public bool Active { get; private set; }

    public SoundClip Clip { get; private set; } = null!;

    public Channel Channel { get; private set; }

    public bool Loop { get; private set; }

    public float Pitch { get; private set; }

    public bool Placed { get; private set; }

    public Vector3 Position { get; set; }

    /// <summary>The pan of a flat voice.</summary>
    public float Pan { get; private set; }

    public float Near { get; private set; }

    public float Far { get; private set; }

    public bool FollowsScale { get; private set; }

    public bool IsMusic { get; private set; }

    public bool IsLayer { get; private set; }

    /// <summary>Music and ambience layers, which the voice limit never drops.</summary>
    public bool Protected => IsMusic || IsLayer;

    /// <summary>What went wrong if the clip's reader threw while mixing. The voice ends at once.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>True once <see cref="Mixer.Stop"/> has started its fade; the voice ends when the fade reaches 0.</summary>
    public bool Stopping { get; set; }

    /// <summary>Seconds into the clip of the next frame to be mixed.</summary>
    public double Seconds
    {
        get
        {
            double frame = _cursor;
            long frames = _reader.Frames;
            if (Loop && frames > 0)
            {
                frame %= frames;
            }

            return frame / Clip.SampleRate;
        }
    }

    /// <summary>
    /// Starts the voice. The clip is opened first, so a clip that fails to open throws with the slot still free.
    /// </summary>
    public void Begin(in VoiceStart start, int id, int fadeInFrames)
    {
        ISampleReader? old = Active ? _reader : null;
        ISampleReader reader;
        ISampleReader? decoded = _decoded;
        if (start.Clip.Streamed)
        {
            reader = start.Clip.Open();
        }
        else
        {
            decoded = start.Clip.Open(_decoded);
            reader = decoded;
        }

        long from = 0;
        if (start.StartFrame > 0)
        {
            from = Math.Min(start.StartFrame, Math.Max(0, reader.Frames - 1));
            if (from > 0)
            {
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
            }
        }

        // Nothing above changed this voice, so a start that threw leaves whatever the slot was playing alone.
        if (old is not null && old != reader && old != decoded)
        {
            old.Dispose();
        }

        _decoded = decoded;
        _reader = reader;
        Id = id;
        Clip = start.Clip;
        Channel = start.Channel;
        Loop = start.Loop;
        Pitch = Math.Max(0, start.Pitch);
        Placed = start.Position.HasValue;
        Position = start.Position.GetValueOrDefault();
        Pan = Math.Clamp(start.Pan, -1, 1);
        Near = start.Near;
        Far = start.Far;
        FollowsScale = start.FollowsScale;
        IsMusic = start.IsMusic;
        IsLayer = start.IsLayer;
        Failure = null;
        Stopping = false;
        Volume.Set(start.Volume);
        Envelope.Set(fadeInFrames > 0 ? 0 : 1);
        Envelope.To(1, fadeInFrames);

        _channels = Clip.Channels;
        _bufferStart = from;
        _bufferCount = 0;
        _sourceEnded = false;
        _cursor = from;
        _started = false;
        _running = 0;
        _lastStep = 0;
        Active = true;
    }

    public void End()
    {
        Active = false;
        ISampleReader reader = _reader;
        _reader = null!;
        Clip = null!;
        if (reader != _decoded)
        {
            try
            {
                reader.Dispose();
            }
            catch (Exception)
            {
                // The voice is over; a decoder that won't dispose has nothing more to tell us.
            }
        }
    }

    /// <summary>
    /// Adds this voice into the stereo block, stepping through the source by <paramref name="step"/> frames per output
    /// frame. A placed voice is folded to mono and scaled by the two gains; a flat one keeps its sides. The gains
    /// ramp in a straight line from where the last block left them to the values given, so they never jump; a
    /// voice's first block starts at the values given. A step of 0 holds the voice where it is: it keeps playing
    /// for 5 ms while it fades out, then sounds nothing, and fades back in when it resumes. Returns false once the
    /// voice has finished.
    /// </summary>
    public bool Mix(Span<float> stereo, double step, float leftGain, float rightGain)
    {
        int frames = stereo.Length / 2;
        if (!_started)
        {
            _started = true;
            _lastLeft = leftGain;
            _lastRight = rightGain;
            _running = step > 0 ? HoldRampFrames : 0;
        }

        float leftSlope = (leftGain - _lastLeft) / frames;
        float rightSlope = (rightGain - _lastRight) / frames;
        for (int frame = 0; frame < frames; frame++)
        {
            double move = step;
            if (step > 0)
            {
                _lastStep = step;
                if (_running < HoldRampFrames)
                {
                    _running++;
                }
            }
            else if (_running > 0)
            {
                _running--;
                move = _lastStep;
            }

            if (_running > 0 && move > 0)
            {
                long index = (long)_cursor;
                Fetch(index);
                if (Failure is not null)
                {
                    return false;
                }

                if (index >= _bufferStart + _bufferCount)
                {
                    return false;
                }

                float t = (float)(_cursor - index);
                float left = Interpolate(index, 0, t);
                float right = _channels == 2 ? Interpolate(index, 1, t) : left;
                float gain = Volume.Value * Envelope.Value;
                if (_running < HoldRampFrames)
                {
                    gain *= (float)_running / HoldRampFrames;
                }

                float leftNow = _lastLeft + leftSlope * (frame + 1);
                float rightNow = _lastRight + rightSlope * (frame + 1);
                if (Placed)
                {
                    float mono = (left + right) * 0.5f * gain;
                    stereo[frame * 2] += mono * leftNow;
                    stereo[frame * 2 + 1] += mono * rightNow;
                }
                else
                {
                    stereo[frame * 2] += left * gain * leftNow;
                    stereo[frame * 2 + 1] += right * gain * rightNow;
                }

                _cursor += move;
            }

            Volume.Step();
            Envelope.Step();
            if (Stopping && !Envelope.Moving)
            {
                return false;
            }
        }

        _lastLeft = leftGain;
        _lastRight = rightGain;
        return true;
    }

    private float Interpolate(long index, int channel, float t)
    {
        float a = Sample(index, channel);
        float b = Sample(index + 1, channel);
        return a + (b - a) * t;
    }

    private float Sample(long frame, int channel)
    {
        long at = frame - _bufferStart;
        if (at < 0 || at >= _bufferCount)
        {
            return 0;
        }

        return _buffer[at * _channels + channel];
    }

    /// <summary>Reads on until the buffer holds this frame and the one after it, or the clip has run out.</summary>
    private void Fetch(long frame)
    {
        while (frame + 1 >= _bufferStart + _bufferCount && !_sourceEnded)
        {
            long end = _bufferStart + _bufferCount;
            if (frame >= end)
            {
                _bufferStart = end;
                _bufferCount = 0;
            }
            else
            {
                int keep = (int)(end - frame);
                int from = (int)(frame - _bufferStart) * _channels;
                _buffer.AsSpan(from, keep * _channels).CopyTo(_buffer);
                _bufferStart = frame;
                _bufferCount = keep;
            }

            Span<float> free = _buffer.AsSpan(_bufferCount * _channels, (BufferFrames - _bufferCount) * _channels);
            int read;
            try
            {
                read = _reader.Read(free);
                if (read == 0 && Loop)
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

            _bufferCount += read;
        }
    }
}
