namespace Super;

/// <summary>The rate the mixer runs at, and seconds turned into frames at that rate.</summary>
internal static class MixRate
{
    public const int SampleRate = 48000;

    /// <summary>Whole frames in this many seconds, rounded. 0 for anything not above 0.</summary>
    public static int ToFrames(float seconds)
    {
        return seconds > 0 ? (int)Math.Round((double)seconds * SampleRate) : 0;
    }
}

/// <summary>
/// Ids for the mixer's voice and ambience slots: the slot in the low 8 bits and the slot's start count above them, so
/// an id kept after its sound ended can't reach the next sound in that slot.
/// </summary>
internal static class MixSlotIds
{
    private const int SlotBits = 8;
    private const int SlotMask = (1 << SlotBits) - 1;
    private const int GenerationMask = int.MaxValue >> SlotBits;

    /// <summary>The start count after this one. It skips 0, so no id is ever just its slot.</summary>
    public static int NextGeneration(int generation)
    {
        int next = (generation + 1) & GenerationMask;
        return next == 0 ? 1 : next;
    }

    public static int Make(int slot, int generation) => slot | (generation << SlotBits);

    public static int Slot(int id) => id & SlotMask;
}

/// <summary>
/// Counts sounds that failed as they started or played, and scattered sounds that couldn't start, with the latest
/// message for a log. Only whatever mixes writes it.
/// </summary>
internal sealed class MixFailures
{
    /// <summary>How many have failed so far.</summary>
    public int Failed { get; private set; }

    /// <summary>The message of the latest failure, or null if there has been none.</summary>
    public string? LastMessage { get; private set; }

    public void Tally(Exception problem) => Tally(problem.Message);

    public void Tally(string message)
    {
        Failed++;
        LastMessage = message;
    }
}
