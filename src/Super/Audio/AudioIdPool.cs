using System.Numerics;

namespace Super;

/// <summary>
/// Ids handed out on the game thread: a slot in the low 16 bits and the slot's use count above, so an id kept after
/// it was released can't reach the next user of the slot. Preallocated, so taking and releasing never allocate.
/// </summary>
internal sealed class AudioIdPool
{
    /// <summary>What <see cref="Take"/> returns when every slot is in use, and the id calls ignore.</summary>
    public const int NoId = -1;

    private const int SlotBits = 16;
    private const int GenerationMask = int.MaxValue >> SlotBits;

    private readonly int[] _free;
    private readonly int[] _generations;
    private readonly bool[] _held;
    private int _freeCount;

    public AudioIdPool(int capacity)
    {
        _free = new int[capacity];
        _generations = new int[capacity];
        _held = new bool[capacity];
        for (int i = 0; i < capacity; i++)
        {
            // Lowest slot first, which keeps ids small and easy to read in a debugger.
            _free[i] = capacity - 1 - i;
        }

        _freeCount = capacity;
    }

    public static int Slot(int id) => id & ((1 << SlotBits) - 1);

    /// <summary>A fresh id, or -1 when every slot is in use.</summary>
    public int Take()
    {
        if (_freeCount == 0)
        {
            return NoId;
        }

        int slot = _free[--_freeCount];
        int generation = (_generations[slot] + 1) & GenerationMask;
        if (generation == 0)
        {
            generation = 1;
        }

        _generations[slot] = generation;
        _held[slot] = true;
        return slot | (generation << SlotBits);
    }

    public bool Holds(int id)
    {
        if (id < 0)
        {
            return false;
        }

        int slot = Slot(id);
        return slot < _held.Length && _held[slot] && _generations[slot] == id >> SlotBits;
    }

    /// <summary>Frees the id and returns true, or returns false for an id that isn't held.</summary>
    public bool Release(int id)
    {
        if (!Holds(id))
        {
            return false;
        }

        int slot = Slot(id);
        _held[slot] = false;
        _free[_freeCount++] = slot;
        return true;
    }
}

/// <summary>The checks <see cref="Audio"/> makes on the game thread before it queues anything.</summary>
internal static class AudioChecks
{
    public static void CheckVolume(float volume, string name)
    {
        if (!(volume >= 0) || !float.IsFinite(volume))
        {
            throw new ArgumentOutOfRangeException(name, volume, $"{name} must be a number of at least zero.");
        }
    }

    public static void CheckDoppler(float doppler, string name)
    {
        if (!(doppler >= 0) || !float.IsFinite(doppler))
        {
            throw new ArgumentOutOfRangeException(name, doppler, $"{name} must be a number of at least zero.");
        }
    }

    public static void CheckChannel(Channel channel, string name)
    {
        if (!IsChannel(channel))
        {
            throw new ArgumentOutOfRangeException(name, channel, $"{name} must be one of the Channel values.");
        }
    }

    public static void CheckSeconds(float seconds, string name)
    {
        if (!(seconds >= 0) || !float.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(name, seconds, $"{name} must be a number of at least zero.");
        }
    }

    /// <summary>Checks a channel a duck names: any channel but Master, which carries the rest.</summary>
    public static void CheckDuckChannel(Channel channel, string name)
    {
        CheckChannel(channel, name);
        if (channel == Channel.Master)
        {
            throw new ArgumentOutOfRangeException(name, channel, $"{name} can't be Master, which carries every other channel. Duck the channels under it instead.");
        }
    }

    public static void CheckDuck(float decibels, float attack, float release)
    {
        if (!(decibels <= 0) || !float.IsFinite(decibels))
        {
            throw new ArgumentOutOfRangeException(nameof(decibels), decibels, "decibels must be a number of 0 or below: a duck only turns a channel down.");
        }

        CheckSeconds(attack, nameof(attack));
        CheckSeconds(release, nameof(release));
    }

    /// <summary>Whether the value is one of the <see cref="Channel"/> values.</summary>
    public static bool IsChannel(Channel channel) => (uint)channel <= (uint)Channel.Dialogue;

    public static void CheckPlace(Vector3? at, float near, float far)
    {
        if (at is Vector3 position && !IsFinite(position))
        {
            throw new ArgumentOutOfRangeException(nameof(at), position, "at must be a finite position.");
        }

        if (!(near >= 0) || !float.IsFinite(near))
        {
            throw new ArgumentOutOfRangeException(nameof(near), near, "near must be a distance of at least zero.");
        }

        if (!(far > near) || !float.IsFinite(far))
        {
            throw new ArgumentOutOfRangeException(nameof(far), far, "far must be further than near.");
        }
    }

    public static bool IsFinite(Vector3 position)
    {
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }
}
