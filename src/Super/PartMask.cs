namespace Super;

/// <summary>
/// Which parts of one model a draw shows. A model is cooked in parts, one for each mesh in the
/// source file, and a mask picks some of them, so one file can hold many looks, such as
/// several bodies, heads and weapons, and each draw shows one of each. Make a mask once, with
/// <see cref="Content.Parts"/>, and keep it: a mask never changes and drawing with it allocates
/// nothing. It only fits the model it was made for.
/// </summary>
public sealed class PartMask
{
    private readonly ulong[] _bits;

    internal PartMask(int model, int count, ReadOnlySpan<int> kept)
    {
        _bits = new ulong[(count + 63) / 64];
        Model = model;
        Count = count;

        foreach (int part in kept)
        {
            if ((uint)part >= (uint)count)
            {
                throw new ArgumentOutOfRangeException(nameof(kept), part, $"Part {part} is outside the model's {count} parts.");
            }

            if ((_bits[part >> 6] & (1UL << (part & 63))) == 0)
            {
                _bits[part >> 6] |= 1UL << (part & 63);
                Kept++;
            }
        }
    }

    /// <summary>The index of the <see cref="ModelRef"/> the mask was made for.</summary>
    internal int Model { get; }

    /// <summary>How many parts the model has, shown or not.</summary>
    public int Count { get; }

    /// <summary>How many parts the mask shows.</summary>
    public int Kept { get; private set; }

    /// <summary>Whether the part, counted from 0 in the order the model's meshes were cooked, is shown.</summary>
    public bool Shows(int part) => (uint)part < (uint)Count && (_bits[part >> 6] & (1UL << (part & 63))) != 0;
}
