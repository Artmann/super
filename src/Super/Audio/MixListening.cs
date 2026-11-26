using System.Numerics;

namespace Super;

/// <summary>
/// How the mixer hears its voices: the channel volumes, where the listener is and faces, and the world's time scale.
/// It works out each voice's gains and speed from them.
/// </summary>
internal sealed class MixListening
{
    private readonly float[] _channelVolumes = new float[Enum.GetValues<Channel>().Length];
    private Vector3 _listener;
    private Vector3 _forward = -Vector3.UnitZ;
    private Vector3 _listenerVelocity;
    private float _worldRate = 1;
    private float _dopplerScale = 1;

    public MixListening()
    {
        Array.Fill(_channelVolumes, 1f);
    }

    public void SetChannelVolume(Channel channel, float volume)
    {
        _channelVolumes[(int)channel] = volume;
    }

    /// <summary>Where placed voices are heard from, from the next mixed frame, and how fast the listener moves.</summary>
    public void SetListener(Vector3 position, Vector3 forward, Vector3 velocity = default)
    {
        _listener = position;
        _forward = forward;
        _listenerVelocity = velocity;
    }

    /// <summary>Scales every placed voice's Doppler amount: 0 turns the effect off, 1 leaves each voice's own.</summary>
    public void SetDopplerScale(float scale)
    {
        _dopplerScale = Math.Max(0, scale);
    }

    /// <summary>
    /// How much the Doppler effect raises the voice's pitch: 1 for a flat voice or one with no Doppler amount,
    /// otherwise from both velocities scaled by the voice's amount and the global scale.
    /// </summary>
    public float Doppler(Voice voice)
    {
        ref readonly VoiceStart setup = ref voice.Setup;
        float amount = setup.Doppler * _dopplerScale;
        if (!voice.Placed || !(amount > 0))
        {
            return 1;
        }

        return Spatial.Doppler(_listener, _listenerVelocity * amount, voice.Position, setup.Velocity * amount);
    }

    /// <summary>The world's time scale. Voices that follow it play slower and lower below 1, and hold at 0.</summary>
    public void SetWorldRate(float scale)
    {
        _worldRate = Math.Max(0, scale);
    }

    /// <summary>Which channels dip under which, and by how much this block.</summary>
    public Ducking Ducks { get; } = new();

    /// <summary>The channel's volume times its ducking this block, times Master. A voice on Master is scaled once.</summary>
    public float ChannelGain(Channel channel)
    {
        float master = _channelVolumes[(int)Channel.Master];
        return channel == Channel.Master ? master : _channelVolumes[(int)channel] * Ducks.Gain(channel) * master;
    }

    public float Falloff(Vector3 position, float near, float far)
    {
        return Spatial.Falloff(Vector3.Distance(_listener, position), near, far);
    }

    public float Falloff(Voice voice)
    {
        return Falloff(voice.Position, voice.Setup.Near, voice.Setup.Far);
    }

    public float Pan(Vector3 position)
    {
        return Spatial.Pan(_listener, _forward, position);
    }

    /// <summary>Source frames the voice moves on per output frame: its pitch, the Doppler effect and the world rate.</summary>
    public double SourceStep(Voice voice)
    {
        ref readonly VoiceStart setup = ref voice.Setup;
        double step = (double)setup.Clip.SampleRate / MixRate.SampleRate * setup.Pitch * Doppler(voice);
        if (setup.FollowsScale)
        {
            step *= _worldRate;
        }

        return step;
    }

    /// <summary>
    /// The voice's left and right gains this block: the channel, then for a placed voice the falloff and an
    /// equal-power pan, or for a flat one its own balance.
    /// </summary>
    public (float Left, float Right) SideGains(Voice voice)
    {
        float channel = ChannelGain(voice.Setup.Channel);
        if (voice.Placed)
        {
            float falloff = Falloff(voice);
            (float panLeft, float panRight) = Spatial.EqualPower(Pan(voice.Position));
            return (panLeft * falloff * channel, panRight * falloff * channel);
        }

        float pan = voice.Setup.Pan;
        if (pan != 0)
        {
            (float balanceLeft, float balanceRight) = Spatial.Balance(pan);
            return (channel * balanceLeft, channel * balanceRight);
        }

        return (channel, channel);
    }
}
