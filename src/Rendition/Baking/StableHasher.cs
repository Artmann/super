using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Text;

namespace Rendition;

/// <summary>
/// Hashes values to a <see cref="ulong"/> that is the same across runs, machines, cultures and processes, unlike
/// <see cref="HashCode"/> and <see cref="string.GetHashCode()"/>, which are seeded per process. Built on XxHash64 with a
/// seed of zero, fed little-endian bytes. Floats are rounded to <see cref="Step"/> first, so values a hair apart, such
/// as a transform read back from a file, hash the same. Nothing marks a value's type or where it ends, so callers
/// must feed a fixed sequence of values for each thing they hash. See "Bake ids" in docs/design.md.
/// </summary>
public sealed class StableHasher
{
    /// <summary>The size floats are rounded to before hashing, a tenth of a millimetre. Matrix elements use it too.</summary>
    public const float Step = 1e-4f;

    private readonly XxHash64 _hash = new(0);

    public StableHasher Add(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _hash.Append(bytes);
        return this;
    }

    public StableHasher Add(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _hash.Append(bytes);
        return this;
    }

    /// <summary>
    /// Rounds the value to the nearest <see cref="Step"/> and hashes that count of steps, so negative zero hashes as zero.
    /// Two values a hair apart on either side of a step's midpoint still differ. Values past about ±9e14 saturate,
    /// and every NaN hashes alike.
    /// </summary>
    public StableHasher Add(float value)
    {
        long steps = (long)Math.Round((double)value / Step, MidpointRounding.ToEven);
        return Add(unchecked((ulong)steps));
    }

    public StableHasher Add(Vector3 value) => Add(value.X).Add(value.Y).Add(value.Z);

    public StableHasher Add(Matrix4x4 value)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                Add(value[row, column]);
            }
        }

        return this;
    }

    /// <summary>Hashes the string's length and then its UTF-8 bytes, so "ab" then "c" differs from "a" then "bc".</summary>
    public StableHasher Add(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Add(bytes.Length);
        _hash.Append(bytes);
        return this;
    }

    /// <summary>Hashes the length and then the bytes, for data such as pixels that has no floats to round.</summary>
    public StableHasher Add(ReadOnlySpan<byte> bytes)
    {
        Add(bytes.Length);
        _hash.Append(bytes);
        return this;
    }

    public ulong Finish() => _hash.GetCurrentHashAsUInt64();

    /// <summary>Starts again from nothing, so one hasher can serve every draw of a frame without garbage.</summary>
    public void Reset() => _hash.Reset();
}
