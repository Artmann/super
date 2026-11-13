using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// The draws a space's bake is made from: every static draw in the frame, since all of them block its light, and where
/// each chunk that owns some stands. Keeps its arrays from frame to frame so a steady frame allocates nothing. Part of
/// <see cref="BakeResolver"/>.
/// </summary>
internal sealed class SpaceDraws
{
    private readonly List<ulong> _placedChunks = [];
    private ulong[] _ids = [];

    /// <summary>How many of <paramref name="draws"/> are static, with a bake id.</summary>
    public static int StaticCount(ReadOnlySpan<DrawItem> draws)
    {
        int count = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The bake ids of the <paramref name="staticCount"/> static draws among <paramref name="draws"/>: every one blocks
    /// and bounces the space's light, wherever it stands.
    /// </summary>
    public ReadOnlySpan<ulong> Ids(ReadOnlySpan<DrawItem> draws, int staticCount)
    {
        ReusedArrays.Grow(ref _ids, staticCount);
        int count = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0)
            {
                _ids[count++] = draw.BakeId;
            }
        }

        return _ids.AsSpan(0, count);
    }

    /// <summary>
    /// <see cref="BakeInputHash.PlacedChunk"/> of each chunk that owns some draws. A chunk's draws have ids relative to
    /// it, so where it stands goes into the space's inputs too.
    /// </summary>
    public ReadOnlySpan<ulong> PlacedChunks(ReadOnlySpan<BakedChunk> chunks, BakeFrame frame, ModelOwners models)
    {
        _placedChunks.Clear();
        for (int c = 0; c < chunks.Length; c++)
        {
            if (models.ChunkIds(c).Count > 0)
            {
                _placedChunks.Add(BakeInputHash.PlacedChunk(chunks[c], frame.ChunkInputs[c].Draws));
            }
        }

        return CollectionsMarshal.AsSpan(_placedChunks);
    }
}
