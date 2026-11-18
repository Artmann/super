using Rendition;

namespace Super;

/// <summary>The parts of a model a <see cref="MeshMask"/> shows, which are all a draw copies and counts.</summary>
internal static class MeshMaskParts
{
    /// <summary>
    /// Copies the parts a mask shows onto the end of <paramref name="draws"/>, from
    /// <paramref name="count"/> on, growing it when needed, and gives the new count. A null mask
    /// shows every part. Throws if the mask was made for a model with another part count.
    /// </summary>
    internal static int AddShown(DrawItem[] parts, MeshMask? mask, ref DrawItem[] draws, int count)
    {
        int kept = KeptCount(parts.Length, mask);
        if (count + kept > draws.Length)
        {
            Array.Resize(ref draws, (count + kept) * 2);
        }

        for (int p = 0; p < parts.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                draws[count++] = parts[p];
            }
        }

        return count;
    }

    /// <summary>The triangles a draw with this mask shows. Throws if the mask was made for a model with another part count.</summary>
    internal static long ShownTriangles(DrawItem[] parts, MeshMask? mask)
    {
        KeptCount(parts.Length, mask);
        long indices = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                indices += parts[p].IndexCount;
            }
        }

        return indices / 3;
    }

    /// <summary>How many of a model's parts a draw shows. A null mask shows them all. Throws if the mask was made for a model with another part count.</summary>
    internal static int KeptCount(int partCount, MeshMask? mask)
    {
        if (mask is null)
        {
            return partCount;
        }

        if (mask.Count != partCount)
        {
            throw new InvalidOperationException(
                $"The mesh mask was made for a model drawn in {mask.Count} parts, but the model being drawn has {partCount}. Make it from the same cooked file.");
        }

        return mask.Kept;
    }
}
