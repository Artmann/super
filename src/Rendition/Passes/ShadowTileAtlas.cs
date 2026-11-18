using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// The lights a view keeps, as <see cref="LocalLights"/> finds them: each kept light's index among the frame's lights
/// and how many pixels its reach spans, and those of them that cast a shadow, with their priority, which the tiles
/// sort in place.
/// </summary>
internal readonly ref struct SeenLights(ReadOnlySpan<int> kept, ReadOnlySpan<float> pixels, Span<int> shadowed, Span<float> priority)
{
    public ReadOnlySpan<int> Kept { get; } = kept;

    public ReadOnlySpan<float> Pixels { get; } = pixels;

    public Span<int> Shadowed { get; } = shadowed;

    public Span<float> Priority { get; } = priority;
}

/// <summary>
/// Gives the shadowed point and spot lights tiles of the shadow atlas, sized by how big they look, the nearest and
/// biggest first: when the atlas is full the rest get smaller tiles, and then none.
/// </summary>
internal sealed class ShadowTileAtlas
{
    public const int AtlasSize = 4096;
    public const int MinTile = 64;
    public const int MaxTile = 512;
    public const int MaxTiles = AtlasSize / MinTile * (AtlasSize / MinTile);

    /// <summary>How close to a light its shadow starts, in metres.</summary>
    public const float ShadowNear = 0.05f;

    // Texels each tile sees past its edges, so a soft shadow's filter stays inside it.
    private const int Guard = 3;

    private static readonly (Vector3 Forward, Vector3 Up)[] CubeFaces =
    [
        (Vector3.UnitX, Vector3.UnitY),
        (-Vector3.UnitX, Vector3.UnitY),
        (Vector3.UnitY, Vector3.UnitZ),
        (-Vector3.UnitY, Vector3.UnitZ),
        (Vector3.UnitZ, Vector3.UnitY),
        (-Vector3.UnitZ, Vector3.UnitY),
    ];

    private readonly int[] _sizes = new int[LocalLight.MaxPerFrame];
    private readonly int[] _bySize = new int[LocalLight.MaxPerFrame];
    private readonly float[] _sizeOrder = new float[LocalLight.MaxPerFrame];
    private readonly int[] _sizeKeys = new int[LocalLight.MaxPerFrame];
    private readonly ShadowView[] _tiles = new ShadowView[MaxTiles];

    // Each light's tile size last frame, by key, so a light near the line between two sizes
    // doesn't flip between them.
    private Dictionary<int, int> _lastSizes = [];
    private Dictionary<int, int> _nextSizes = [];

    public int Count { get; private set; }

    public ReadOnlySpan<ShadowView> Views => _tiles.AsSpan(0, Count);

    /// <summary>The first tile of the kept light <paramref name="kept"/>, as the shaders read it, or -1 for none.</summary>
    public float TileOf(int kept) => _sizes[kept] > 0 ? _sizeKeys[kept] : -1f;

    /// <summary>Gives the seen lights that cast a shadow their tiles, and writes the tiles for the shaders.</summary>
    public void Place(ReadOnlySpan<LocalLight> lights, in SeenLights seen, Span<GpuShadowTile> gpuTiles)
    {
        ChooseSizes(lights, seen);
        PlaceTiles(lights, seen, gpuTiles);
    }

    /// <summary>
    /// A light's view of one tile: reversed-Z perspective between <see cref="ShadowNear"/>
    /// and its range, with y flipped like the camera's so the same pipelines' winding applies.
    /// </summary>
    internal static Matrix4x4 TileMatrix(Vector3 eye, Vector3 forward, Vector3 up, float scale, float range)
    {
        float n = ShadowNear;
        float f = MathF.Max(range, n * 2f);
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, up);
        var projection = new Matrix4x4(
            scale, 0f, 0f, 0f,
            0f, -scale, 0f, 0f,
            0f, 0f, n / (f - n), -1f,
            0f, 0f, n * f / (f - n), 0f);
        return view * projection;
    }

    /// <summary>
    /// Gives each shadowed light the tile size its look on screen asks for, biggest first,
    /// halving what's left over until it fits in the atlas. Those that don't fit get none.
    /// </summary>
    private void ChooseSizes(ReadOnlySpan<LocalLight> lights, in SeenLights seen)
    {
        _sizes.AsSpan(0, seen.Kept.Length).Clear();
        seen.Priority.Sort(seen.Shadowed);
        (_lastSizes, _nextSizes) = (_nextSizes, _lastSizes);
        _nextSizes.Clear();

        long free = (long)AtlasSize * AtlasSize;
        foreach (int k in seen.Shadowed)
        {
            ref readonly var light = ref lights[seen.Kept[k]];
            float pixels = seen.Pixels[k];
            int size = Math.Clamp(
                (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(pixels, 1f)), MinTile, MaxTile);

            // Keeping last frame's size while the light is near the line saves a visible jump.
            if (_lastSizes.TryGetValue(light.Key, out int last) && pixels >= last * 0.4f && pixels <= last * 1.2f)
            {
                size = last;
            }

            int faces = light.IsSpot ? 1 : 6;
            while (size >= MinTile && (long)faces * size * size > free)
            {
                size /= 2;
            }

            if (size < MinTile)
            {
                continue;
            }

            free -= (long)faces * size * size;
            _sizes[k] = size;
            _nextSizes[light.Key] = size;
        }
    }

    /// <summary>
    /// Lays the tiles out, biggest first, along a Z-shaped curve through the atlas. Every
    /// size is a power of two and none comes after a smaller one, so each lands on a spot
    /// that's a whole number of its own size along, and they fill the atlas without gaps.
    /// </summary>
    private void PlaceTiles(ReadOnlySpan<LocalLight> lights, in SeenLights seen, Span<GpuShadowTile> gpuTiles)
    {
        int count = SortBySize(seen.Shadowed);
        long offset = 0;
        int tiles = 0;
        for (int t = 0; t < count; t++)
        {
            int k = _bySize[t];
            ref readonly var light = ref lights[seen.Kept[k]];
            int size = _sizes[k];
            _sizeKeys[k] = tiles;
            int faces = light.IsSpot ? 1 : 6;
            for (int face = 0; face < faces; face++)
            {
                uint along = (uint)(offset / ((long)size * size));
                int x = (int)Compact(along) * size;
                int y = (int)Compact(along >> 1) * size;
                offset += (long)size * size;

                var (forward, up) = light.IsSpot ? SpotAxes(light.Direction) : CubeFaces[face];
                float halfAngle = light.IsSpot ? MathF.Min(light.OuterAngle, 1.5f) : MathF.PI / 4f;
                float scale = (size - 2f * Guard) / size / MathF.Tan(halfAngle);
                var matrix = TileMatrix(light.Position, forward, up, scale, light.Range);
                _tiles[tiles] = new ShadowView(matrix, light.Position, scale * size / 2f, x, y, size);
                gpuTiles[tiles] = new GpuShadowTile
                {
                    Matrix = matrix,
                    Rect = new Vector4(x, y, size, size) / AtlasSize,
                    Depth = new Vector4(ShadowNear, MathF.Max(light.Range, ShadowNear * 2f), scale, 0f),
                };
                tiles++;
            }
        }

        Count = tiles;
    }

    // Puts the shadowed lights that got a tile size in _bySize, biggest first, and returns how many.
    private int SortBySize(ReadOnlySpan<int> shadowed)
    {
        int count = 0;
        foreach (int k in shadowed)
        {
            if (_sizes[k] > 0)
            {
                _bySize[count] = k;
                _sizeOrder[count] = -_sizes[k];
                count++;
            }
        }

        _sizeOrder.AsSpan(0, count).Sort(_bySize.AsSpan(0, count));
        return count;
    }

    private static (Vector3 Forward, Vector3 Up) SpotAxes(Vector3 direction)
    {
        var forward = Vector3.Normalize(direction);
        return (forward, MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
    }

    /// <summary>Every other bit of a number, packed together: one axis of a Z-order index.</summary>
    private static uint Compact(uint bits)
    {
        bits &= 0x55555555;
        bits = (bits | (bits >> 1)) & 0x33333333;
        bits = (bits | (bits >> 2)) & 0x0F0F0F0F;
        bits = (bits | (bits >> 4)) & 0x00FF00FF;
        bits = (bits | (bits >> 8)) & 0x0000FFFF;
        return bits;
    }
}
