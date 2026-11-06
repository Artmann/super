namespace Super;

/// <summary>
/// Which meshes of one model a draw shows, so one file can hold many looks, such as several
/// bodies, heads and weapons, and each draw shows one of each. Make a mask once, with
/// <see cref="Content.Meshes"/>, and keep it: a mask never changes and drawing with it allocates
/// nothing. It only fits the model it was made for. Underneath it picks the model's parts, the
/// pieces it is drawn in, which a mesh with several materials has one of for each.
/// </summary>
public sealed class MeshMask
{
    private readonly ulong[] _bits;

    internal MeshMask(int model, int count, ReadOnlySpan<int> kept)
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

    /// <summary>How many parts the model is drawn in, shown or not.</summary>
    public int Count { get; }

    /// <summary>How many of those parts the mask shows.</summary>
    public int Kept { get; private set; }

    /// <summary>Whether the part, counted from 0 in the order the model was cooked, is shown.</summary>
    public bool Shows(int part) => (uint)part < (uint)Count && (_bits[part >> 6] & (1UL << (part & 63))) != 0;
}
