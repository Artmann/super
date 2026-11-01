namespace Rendition.Vulkan;

/// <summary>
/// Hands out ranges of one block of memory: best fit with alignment, and freed neighbours
/// merge back together. Pure bookkeeping with no Vulkan in it, so it has unit tests.
/// </summary>
internal sealed class MemoryArena(ulong size)
{
    /// <summary>Free ranges sorted by offset, never touching each other.</summary>
    private readonly List<(ulong Offset, ulong Size)> _free = [(0, size)];

    private readonly Dictionary<ulong, ulong> _allocated = [];

    public ulong Size { get; } = size;

    /// <summary>Bytes handed out, not counting alignment padding.</summary>
    public ulong Used { get; private set; }

    public bool IsEmpty => _allocated.Count == 0;

    public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
    {
        int best = -1;
        ulong bestWaste = ulong.MaxValue;
        ulong bestStart = 0;
        for (int i = 0; i < _free.Count; i++)
        {
            var (start, length) = _free[i];
            ulong aligned = Align(start, alignment);
            ulong padding = aligned - start;
            if (padding >= length || length - padding < size)
            {
                continue;
            }

            ulong waste = length - size;
            if (waste < bestWaste)
            {
                best = i;
                bestWaste = waste;
                bestStart = aligned;
            }
        }

        if (best < 0)
        {
            offset = 0;
            return false;
        }

        var (freeStart, freeLength) = _free[best];
        ulong end = freeStart + freeLength;
        ulong allocationEnd = bestStart + size;
        _free.RemoveAt(best);
        if (allocationEnd < end)
        {
            _free.Insert(best, (allocationEnd, end - allocationEnd));
        }

        if (bestStart > freeStart)
        {
            _free.Insert(best, (freeStart, bestStart - freeStart));
        }

        _allocated[bestStart] = size;
        Used += size;
        offset = bestStart;
        return true;
    }

    public void Free(ulong offset)
    {
        if (!_allocated.Remove(offset, out ulong size))
        {
            throw new InvalidOperationException($"Offset {offset} is not allocated.");
        }

        Used -= size;

        int index = 0;
        while (index < _free.Count && _free[index].Offset < offset)
        {
            index++;
        }

        ulong start = offset;
        ulong end = offset + size;
        if (index < _free.Count && _free[index].Offset == end)
        {
            end += _free[index].Size;
            _free.RemoveAt(index);
        }

        if (index > 0 && _free[index - 1].Offset + _free[index - 1].Size == start)
        {
            start = _free[index - 1].Offset;
            _free.RemoveAt(index - 1);
            index--;
        }

        _free.Insert(index, (start, end - start));
    }

    private static ulong Align(ulong value, ulong alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
