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

    /// <summary>True when the world's time scale slows and pauses it.</summary>
    public bool FollowsScale;

    /// <summary>True for music, which the voice limit never drops.</summary>
    public bool IsMusic;

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

    // Interleaved source frames; up to two channels.
    private readonly float[] _buffer = new float[BufferFrames * 2];

    // A decoded clip's reader, kept for the next decoded clip played in this slot.
    private ISampleReader? _decoded;
    private ISampleReader _reader = null!;
    private int _channels;

    // Source frame numbers count on across loops, so frame Frames is the clip's frame 0 again.
    private long _bufferStart;
    private int _bufferCount;
    private bool _sourceEnded;
    private double _cursor;

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

    public float Near { get; private set; }

    public float Far { get; private set; }

    public bool FollowsScale { get; private set; }

    public bool IsMusic { get; private set; }

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

    public void Begin(in VoiceStart start, int id, int fadeInFrames)
    {
        Id = id;
        Active = true;
        Clip = start.Clip;
        Channel = start.Channel;
        Loop = start.Loop;
        Pitch = Math.Max(0, start.Pitch);
        Placed = start.Position.HasValue;
        Position = start.Position.GetValueOrDefault();
        Near = start.Near;
        Far = start.Far;
        FollowsScale = start.FollowsScale;
        IsMusic = start.IsMusic;
        Stopping = false;
        Volume.Set(start.Volume);
        Envelope.Set(fadeInFrames > 0 ? 0 : 1);
        Envelope.To(1, fadeInFrames);

        if (Clip.Streamed)
        {
            _reader = Clip.Open();
        }
        else
        {
            _decoded = Clip.Open(_decoded);
            _reader = _decoded;
        }

        _channels = Clip.Channels;
        _bufferStart = 0;
        _bufferCount = 0;
        _sourceEnded = false;
        _cursor = 0;
    }

    public void End()
    {
        if (_reader != _decoded)
        {
            _reader.Dispose();
        }

        _reader = null!;
        Clip = null!;
        Active = false;
    }

    /// <summary>
    /// Adds this voice into the stereo block, stepping through the source by <paramref name="step"/> frames per output
    /// frame. A placed voice is folded to mono and scaled by the two gains; a flat one keeps its sides. A step of 0
    /// holds the voice where it is and sounds nothing. Returns false once the voice has finished.
    /// </summary>
    public bool Mix(Span<float> stereo, double step, float leftGain, float rightGain)
    {
        int frames = stereo.Length / 2;
        for (int frame = 0; frame < frames; frame++)
        {
            if (step > 0)
            {
                long index = (long)_cursor;
                Fetch(index);
                if (index >= _bufferStart + _bufferCount)
                {
                    return false;
                }

                float t = (float)(_cursor - index);
                float left = Interpolate(index, 0, t);
                float right = _channels == 2 ? Interpolate(index, 1, t) : left;
                float gain = Volume.Value * Envelope.Value;
                if (Placed)
                {
                    float mono = (left + right) * 0.5f * gain;
                    stereo[frame * 2] += mono * leftGain;
                    stereo[frame * 2 + 1] += mono * rightGain;
                }
                else
                {
                    stereo[frame * 2] += left * gain * leftGain;
                    stereo[frame * 2 + 1] += right * gain * rightGain;
                }

                _cursor += step;
            }

            Volume.Step();
            Envelope.Step();
            if (Stopping && !Envelope.Moving)
            {
                return false;
            }
        }

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
            int read = _reader.Read(free);
            if (read == 0 && Loop)
            {
                _reader.Seek(0);
                read = _reader.Read(free);
            }

            if (read == 0)
            {
                _sourceEnded = true;
            }

            _bufferCount += read;
        }
    }
}
