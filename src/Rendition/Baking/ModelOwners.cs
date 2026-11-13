using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// Files each static draw of a frame under the chunk that owns its model, keeping its arrays from frame to frame so a
/// steady frame allocates nothing. Part of <see cref="BakeResolver"/>.
/// </summary>
internal sealed class ModelOwners
{
    private readonly ModelOrder _modelOrder = new();
    private readonly Comparison<int> _compareModels;
    private readonly List<BakeSphere> _modelParts = [];
    private int[] _owners = [];
    private int _ownerCount;
    private List<ulong>[] _chunkIds = [];
    private int[] _drawIndex = [];
    private (ulong Instance, Matrix4x4 Transform)[] _keys = [];
    private int[] _order = [];

    public ModelOwners() => _compareModels = _modelOrder.Compare;

    /// <summary>For each draw, the index of the chunk that owns its model, or <see cref="BakeAreas.None"/>.</summary>
    public ReadOnlySpan<int> Owners => _owners.AsSpan(0, _ownerCount);

    /// <summary>The bake ids of the static draws chunk <paramref name="chunk"/> owns, in the order its inputs name them.</summary>
    public List<ulong> ChunkIds(int chunk) => _chunkIds[chunk];

    /// <summary>Forgets every owner, as a frame that names no area does.</summary>
    public void Clear() => _ownerCount = 0;

    /// <summary>Starts a frame of <paramref name="drawCount"/> draws, none owned yet, and <paramref name="chunkCount"/> empty chunks.</summary>
    public void Reset(int drawCount, int chunkCount)
    {
        ReusedArrays.Grow(ref _owners, drawCount);
        _ownerCount = drawCount;
        _owners.AsSpan(0, drawCount).Fill(BakeAreas.None);
        ReusedArrays.ClearLists(ref _chunkIds, chunkCount);
    }

    // A model's parts are filed together, under the area that owns the whole model, the same call Super made when it
    // gave their ids, so a model straddling two areas is never split between them. The parts need not come one after
    // another: they are sorted by (instance, transform, draw order) first.
    public void File(
        ReadOnlySpan<DrawItem> draws, int staticCount, Func<DetailHandle, BakeSphere> bounds, BakedSpace? space,
        ReadOnlySpan<BakedChunk> chunks)
    {
        SortStaticDraws(draws, staticCount);
        for (int first = 0; first < staticCount;)
        {
            int end = first + 1;
            while (end < staticCount && _keys[_order[end]] == _keys[_order[first]])
            {
                end++;
            }

            FileModel(draws, _order.AsSpan(first, end - first), bounds, space, chunks);
            first = end;
        }
    }

    // Puts the static draws' indices in _order, sorted so each model's parts come in one run.
    private void SortStaticDraws(ReadOnlySpan<DrawItem> draws, int staticCount)
    {
        ReusedArrays.Grow(ref _drawIndex, staticCount);
        ReusedArrays.Grow(ref _keys, staticCount);
        ReusedArrays.Grow(ref _order, staticCount);
        for (int i = 0, n = 0; i < draws.Length; i++)
        {
            if (draws[i].BakeId != 0)
            {
                _drawIndex[n] = i;
                _keys[n] = (BakeIds.Instance(draws[i].BakeId), draws[i].Transform);
                _order[n] = n;
                n++;
            }
        }

        // Sorting with an IComparer makes a Comparison delegate on every call, so the delegate is made once instead.
        _modelOrder.Keys = _keys;
        _order.AsSpan(0, staticCount).Sort(_compareModels);
    }

    // Files the model whose parts are at these positions among the static draws under the chunk that owns it, if one
    // does.
    private void FileModel(
        ReadOnlySpan<DrawItem> draws, ReadOnlySpan<int> parts, Func<DetailHandle, BakeSphere> bounds, BakedSpace? space,
        ReadOnlySpan<BakedChunk> chunks)
    {
        _modelParts.Clear();
        foreach (int p in parts)
        {
            ref readonly var part = ref draws[_drawIndex[p]];
            _modelParts.Add(BakeAreas.Transform(bounds(part.Detail), part.Transform));
        }

        int owner = BakeAreas.OwnerOfModel(CollectionsMarshal.AsSpan(_modelParts), space, chunks);
        if (owner < 0)
        {
            return;
        }

        var ids = _chunkIds[owner];
        foreach (int p in parts)
        {
            ids.Add(draws[_drawIndex[p]].BakeId);
            _owners[_drawIndex[p]] = owner;
        }
    }

    // Orders static draws, by their position among the static draws, by (instance, the 16 transform elements, position).
    // Every part of a model has the model's instance (BakeIds.Instance) and transform, so a model's parts end up in a
    // run in draw order wherever other models' draws came between them. Two copies of a chunk can give a model the same
    // instance, but they stand in different places, so the transform keeps them apart. One instance is kept and given
    // the frame's keys each time, so ordering makes no garbage.
    private sealed class ModelOrder
    {
        public (ulong Instance, Matrix4x4 Transform)[] Keys { get; set; } = [];

        public int Compare(int a, int b)
        {
            var (instanceA, x) = Keys[a];
            var (instanceB, y) = Keys[b];
            int result = instanceA.CompareTo(instanceB);
            // The elements in row order: M11, M12, ... M44.
            for (int i = 0; result == 0 && i < 16; i++)
            {
                result = x[i / 4, i % 4].CompareTo(y[i / 4, i % 4]);
            }

            return result != 0 ? result : a.CompareTo(b);
        }
    }
}
