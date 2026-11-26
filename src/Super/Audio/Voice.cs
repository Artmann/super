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
    /// How much a placed voice's pitch follows the Doppler effect: 0 not at all, 1 as in air. It scales both its own
    /// velocity and the listener's. Flat voices never have it.
    /// </summary>
    public float Doppler;

    /// <summary>How fast a placed voice moves, in metres a second, for the Doppler effect. It follows <see cref="Voice.Reposition"/>.</summary>
    public Vector3 Velocity;

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

    /// <summary>
    /// The game's own id for the voice, or -1 for none. If the voice limit drops a tagged voice, at its start or later
    /// for a louder one, <see cref="Mixer.OnDropped"/> is called with it.
    /// </summary>
    public int Tag = -1;

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
/// How a voice's sound ramps across blocks: its two side gains move in a straight line from where the last block
/// left them, and a hold at world rate 0 fades it out over 5 ms while it keeps playing, and back in when it resumes.
/// </summary>
internal struct VoiceRamps
{
    // A voice held at world rate 0 fades out over this many frames (5 ms), and back in when it resumes.
    private const int HoldFrames = 240;

    // The gains the last block ended on, which this block ramps from. Not known until the first block.
    private bool _started;
    private float _lastLeft;
    private float _lastRight;
    private float _leftSlope;
    private float _rightSlope;

    // How far into the 5 ms ramp a hold or resume is, 0 (held) to HoldFrames (running), and the step to keep
    // playing at while it fades out.
    private int _running;
    private double _lastStep;

    /// <summary>True while the voice sounds: running, or still fading out of a hold.</summary>
    public readonly bool Sounding => _running > 0;

    /// <summary>For a new sound: its first block starts at its gains, and a voice started held starts silent.</summary>
    public void ForNewSound()
    {
        _started = false;
        _running = 0;
        _lastStep = 0;
    }

    /// <summary>Sets the ramps for a block of this many frames that ends on these gains.</summary>
    public void StartBlock(float leftGain, float rightGain, double step, int frames)
    {
        if (!_started)
        {
            _started = true;
            _lastLeft = leftGain;
            _lastRight = rightGain;
            _running = step > 0 ? HoldFrames : 0;
        }

        _leftSlope = (leftGain - _lastLeft) / frames;
        _rightSlope = (rightGain - _lastRight) / frames;
    }

    public void EndBlock(float leftGain, float rightGain)
    {
        _lastLeft = leftGain;
        _lastRight = rightGain;
    }

    /// <summary>The left gain the ramp has reached by this frame of the block.</summary>
    public readonly float LeftAt(int frame) => _lastLeft + _leftSlope * (frame + 1);

    public readonly float RightAt(int frame) => _lastRight + _rightSlope * (frame + 1);

    /// <summary>The gain scaled by how far the hold's fade has got.</summary>
    public readonly float HoldGain(float gain)
    {
        if (_running < HoldFrames)
        {
            gain *= (float)_running / HoldFrames;
        }

        return gain;
    }

    /// <summary>
    /// How far the source moves this frame. While held (a step of 0) the voice keeps moving at its last step until
    /// its fade-out runs down; while playing, its fade-in runs up.
    /// </summary>
    public double Advance(double step)
    {
        if (step > 0)
        {
            _lastStep = step;
            _running = Math.Min(_running + 1, HoldFrames);
            return step;
        }

        if (_running > 0)
        {
            _running--;
            return _lastStep;
        }

        return step;
    }
}

/// <summary>
/// One slot of the mixer: what it was asked to play, its volume and fade, and the <see cref="VoiceSource"/> it reads
/// its clip through.
/// </summary>
internal sealed class Voice
{
    private readonly VoiceSource _source = new();
    private VoiceStart _setup;
    private VoiceRamps _ramps;

    public Ramp Volume;
    public Ramp Envelope;

    public int Id { get; private set; }

    public bool Active { get; private set; }

    /// <summary>
    /// What the voice was started with, its pitch kept at 0 or above and its pan within -1..1. A placed voice's
    /// position follows <see cref="Reposition"/>.
    /// </summary>
    public ref readonly VoiceStart Setup => ref _setup;

    public bool Placed => _setup.Position.HasValue;

    public Vector3 Position => _setup.Position.GetValueOrDefault();

    /// <summary>Music and ambience layers, which the voice limit never drops.</summary>
    public bool Protected => _setup.IsMusic || _setup.IsLayer;

    /// <summary>What went wrong if the clip's reader threw while mixing. The voice ends at once.</summary>
    public Exception? Failure => _source.Failure;

    /// <summary>True once <see cref="Mixer.Stop"/> has started its fade; the voice ends when the fade reaches 0.</summary>
    public bool Stopping { get; set; }

    /// <summary>Seconds into the clip of the next frame to be mixed.</summary>
    public double Seconds => _source.Seconds(_setup.Clip.SampleRate);

    /// <summary>
    /// Starts the voice. The clip is opened first, so a clip that fails to open throws with the slot still free.
    /// </summary>
    public void Begin(in VoiceStart start, int id, int fadeInFrames)
    {
        _source.Open(start, Active);
        Id = id;
        _setup = start;
        _setup.Pitch = Math.Max(0, start.Pitch);
        _setup.Pan = Math.Clamp(start.Pan, -1, 1);
        Stopping = false;
        Volume.Set(start.Volume);
        Envelope.Set(fadeInFrames > 0 ? 0 : 1);
        Envelope.To(1, fadeInFrames);
        _ramps.ForNewSound();
        Active = true;
    }

    /// <summary>Moves a placed voice, giving its velocity and Doppler amount too. A flat voice stays flat.</summary>
    public void Reposition(Vector3 position, Vector3 velocity, float doppler)
    {
        if (Placed)
        {
            _setup.Position = position;
            _setup.Velocity = velocity;
            _setup.Doppler = doppler;
        }
    }

    public void End()
    {
        Active = false;
        _setup.Clip = null!;
        _source.Close();
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
        _ramps.StartBlock(leftGain, rightGain, step, frames);
        for (int frame = 0; frame < frames; frame++)
        {
            if (!MixFrame(stereo.Slice(frame * 2, 2), frame, step))
            {
                return false;
            }

            Volume.Step();
            Envelope.Step();
            if (Stopping && !Envelope.Moving)
            {
                return false;
            }
        }

        _ramps.EndBlock(leftGain, rightGain);
        return true;
    }

    /// <summary>
    /// Adds the sample at the cursor into one stereo frame, with the side gains the ramps have reached, and moves the
    /// cursor on. Returns false once the clip has run out or failed.
    /// </summary>
    private bool MixFrame(Span<float> into, int frame, double step)
    {
        double move = _ramps.Advance(step);
        if (!_ramps.Sounding || !(move > 0))
        {
            return true;
        }

        if (!_source.TryReadFrame(out float left, out float right))
        {
            return false;
        }

        float gain = _ramps.HoldGain(Volume.Value * Envelope.Value);
        float leftGain = _ramps.LeftAt(frame);
        float rightGain = _ramps.RightAt(frame);
        if (Placed)
        {
            float mono = (left + right) * 0.5f * gain;
            into[0] += mono * leftGain;
            into[1] += mono * rightGain;
        }
        else
        {
            into[0] += left * gain * leftGain;
            into[1] += right * gain * rightGain;
        }

        _source.MoveOn(move);
        return true;
    }
}
