using System.Numerics;

namespace Super;

internal enum AudioCommandKind : byte
{
    /// <summary>Starts <see cref="AudioCommand.Start"/> as the voice <see cref="AudioCommand.Id"/> (-1 for a one-shot nothing refers to again).</summary>
    StartVoice,

    /// <summary>Fades the voice out over <see cref="AudioCommand.FadeSeconds"/>.</summary>
    StopVoice,

    /// <summary>Fades the voice to <see cref="AudioCommand.Value"/> over <see cref="AudioCommand.FadeSeconds"/>.</summary>
    VoiceVolume,

    /// <summary>Moves a placed voice to <see cref="AudioCommand.Position"/>.</summary>
    MoveVoice,

    /// <summary>Starts <see cref="AudioCommand.Ambience"/> at <see cref="AudioCommand.Value"/> as the ambience <see cref="AudioCommand.Id"/>.</summary>
    StartAmbience,

    AmbienceVolume,

    StopAmbience,

    /// <summary>Sets <see cref="AudioCommand.Channel"/> to <see cref="AudioCommand.Value"/>.</summary>
    ChannelVolume,

    /// <summary>Hears placed voices from <see cref="AudioCommand.Position"/>, facing <see cref="AudioCommand.Forward"/>.</summary>
    Listener,

    /// <summary>Sets the world's time scale to <see cref="AudioCommand.Value"/>.</summary>
    WorldRate,
}

/// <summary>One change the game thread asks of the mixer. Which fields count depends on <see cref="Kind"/>.</summary>
internal struct AudioCommand
{
    public AudioCommandKind Kind;

    /// <summary>The game thread's id for the voice or ambience.</summary>
    public int Id;

    public VoiceStart Start;
    public AmbienceDefinition? Ambience;
    public Channel Channel;
    public float Value;
    public float FadeSeconds;
    public Vector3 Position;
    public Vector3 Forward;
}

/// <summary>
/// A fixed-size queue of <see cref="AudioCommand"/>s from one producer (the game thread) to one consumer (whatever
/// mixes). Pushed commands stay hidden from the consumer until <see cref="Publish"/>, so a frame's changes arrive
/// together. Neither side ever waits: a push into a full ring drops the command and counts it in
/// <see cref="Dropped"/>. Nothing allocates after the ring is made.
/// </summary>
internal sealed class AudioCommands
{
    public const int DefaultCapacity = 4096;

    private readonly AudioCommand[] _items;

    // Each counts commands since the start and only grows, so full and empty never look alike.
    private long _written;
    private long _published;
    private long _read;
    private int _dropped;

    public AudioCommands(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new AudioCommand[capacity];
    }

    public int Capacity => _items.Length;

    /// <summary>How many commands were queued since the start, dropped ones not counted.</summary>
    public long Written => _written;

    /// <summary>How many commands were dropped because the ring was full.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>How many commands the consumer has taken since the start. Any thread may read it.</summary>
    public long Taken => Volatile.Read(ref _read);

    /// <summary>Producer only. Queues the command, or drops and counts it when the ring is full.</summary>
    public bool Push(in AudioCommand command)
    {
        if (_written - Volatile.Read(ref _read) >= _items.Length)
        {
            Volatile.Write(ref _dropped, _dropped + 1);
            return false;
        }

        _items[(int)(_written % _items.Length)] = command;
        _written++;
        return true;
    }

    /// <summary>Producer only. Lets the consumer see every command pushed so far.</summary>
    public void Publish()
    {
        Volatile.Write(ref _published, _written);
    }

    /// <summary>Consumer only. Takes the oldest published command, or returns false when there is none.</summary>
    public bool TryPop(out AudioCommand command)
    {
        long read = _read;
        if (read == Volatile.Read(ref _published))
        {
            command = default;
            return false;
        }

        int index = (int)(read % _items.Length);
        command = _items[index];

        // Clear the slot so it doesn't keep an ambience definition alive until the ring wraps round to it.
        _items[index] = default;

        // Only after the copy and the clear, so the producer can't reuse the slot while it's being read.
        Volatile.Write(ref _read, read + 1);
        return true;
    }
}
