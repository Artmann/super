namespace Rendition;

/// <summary>
/// Arrays and lists kept from one frame to the next, so a frame with fewer items than an earlier one reuses what is
/// there and a steady frame allocates nothing.
/// </summary>
internal static class ReusedArrays
{
    // Arrays only grow, doubling, so a frame with fewer lights or draws than an earlier one reuses what is there.
    public static void Grow<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[Math.Max(length, array.Length * 2)];
        }
    }

    // Grows as Grow does, and gives each new slot an empty list.
    public static void GrowLists(ref List<ulong>[] lists, int length)
    {
        if (lists.Length >= length)
        {
            return;
        }

        int old = lists.Length;
        Array.Resize(ref lists, Math.Max(length, old * 2));
        for (int i = old; i < lists.Length; i++)
        {
            lists[i] = [];
        }
    }

    // Grows the lists to length and empties the first length of them.
    public static void ClearLists(ref List<ulong>[] lists, int length)
    {
        GrowLists(ref lists, length);
        for (int i = 0; i < length; i++)
        {
            lists[i].Clear();
        }
    }
}
