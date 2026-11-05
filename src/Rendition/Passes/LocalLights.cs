using System.Numerics;

namespace Rendition.Passes;

/// <summary>One view of a light into the shadow atlas: a spot's cone, or one face of a point light's cube.</summary>
internal readonly record struct ShadowView(Matrix4x4 Matrix, Vector3 Eye, float PixelsAtOneUnit, int X, int Y, int Size);

/// <summary>
/// Gets a frame's point and spot lights ready for the GPU. Lights whose reach misses the
/// view are dropped. Those that cast a shadow are given tiles of the shadow atlas, sized by
/// how big they look, and the nearest and biggest go first: when the atlas is full the rest
/// get smaller tiles, and then none. No Vulkan here, so it can be tested on its own.
/// </summary>
internal sealed class LocalLights
{
    /// <summary>Must match CLUSTER_X, CLUSTER_Y, CLUSTER_Z and CLUSTER_LIGHTS in Shaders/light_data.glsl.</summary>
    public const int ClusterX = 16;
    public const int ClusterY = 9;
    public const int ClusterZ = 24;
    public const int ClusterCount = ClusterX * ClusterY * ClusterZ;
    public const int ClusterLights = 256;

    /// <summary>Bytes of one frame's cluster lists: a count per cluster, then two light indices to a word.</summary>
    public const int ClusterBytes = ClusterCount * sizeof(uint) * (1 + ClusterLights / 2);

    public const int AtlasSize = 4096;
    public const int MinTile = 64;
    public const int MaxTile = 512;
    public const int MaxTiles = AtlasSize / MinTile * (AtlasSize / MinTile);

    /// <summary>How close to a light its shadow starts, in metres.</summary>
    public const float ShadowNear = 0.05f;

    // Texels each tile sees past its edges, so a soft shadow's filter stays inside it.
    private const int Guard = 3;

    // Under perspective the slices start no nearer than this, as they grow from the nearest.
    private const float NearestSlice = 0.05f;

    private static readonly (Vector3 Forward, Vector3 Up)[] CubeFaces =
    [
        (Vector3.UnitX, Vector3.UnitY),
        (-Vector3.UnitX, Vector3.UnitY),
        (Vector3.UnitY, Vector3.UnitZ),
        (-Vector3.UnitY, Vector3.UnitZ),
        (Vector3.UnitZ, Vector3.UnitY),
        (-Vector3.UnitZ, Vector3.UnitY),
    ];

    private readonly int[] _kept = new int[LocalLight.MaxPerFrame];
    private readonly int[] _shadowed = new int[LocalLight.MaxPerFrame];
    private readonly float[] _priority = new float[LocalLight.MaxPerFrame];
    private readonly float[] _pixels = new float[LocalLight.MaxPerFrame];
    private readonly int[] _sizes = new int[LocalLight.MaxPerFrame];
    private readonly int[] _bySize = new int[LocalLight.MaxPerFrame];
    private readonly int[] _sizeKeys = new int[LocalLight.MaxPerFrame];
    private readonly ShadowView[] _tiles = new ShadowView[MaxTiles];

    // Each light's tile size last frame, by key, so a light near the line between two sizes
    // doesn't flip between them.
    private Dictionary<int, int> _lastSizes = [];
    private Dictionary<int, int> _nextSizes = [];

    /// <summary>How many lights reach the view, written to the front of the GPU list.</summary>
    public int LightCount { get; private set; }

    public int TileCount { get; private set; }

    /// <summary>The nearest and furthest depth from the camera any kept light reaches.</summary>
    public float NearDepth { get; private set; }
    public float FarDepth { get; private set; }

    /// <summary>Whether the view is parallel, so the clusters' slices are even.</summary>
    public bool Parallel { get; private set; }

    public ReadOnlySpan<ShadowView> Tiles => _tiles.AsSpan(0, TileCount);

    /// <summary>
    /// Picks the lights the view needs and their shadow tiles, and writes them out for the
    /// shaders. <paramref name="gpuLights"/> needs room for every light and
    /// <paramref name="gpuTiles"/> for <see cref="MaxTiles"/>.
    /// </summary>
    public void Prepare(
        ReadOnlySpan<LocalLight> lights,
        in Matrix4x4 view,
        in Matrix4x4 projection,
        int screenHeight,
        Span<GpuLocalLight> gpuLights,
        Span<GpuShadowTile> gpuTiles)
    {
        if (lights.Length > LocalLight.MaxPerFrame)
        {
            throw new ArgumentException(
                $"A frame can have at most {LocalLight.MaxPerFrame} point and spot lights, not {lights.Length}.", nameof(lights));
        }

        var viewProjection = view * projection;
        var frustum = new DetailView(viewProjection, Vector3.Zero, 0f, 0f, 0f);
        var (pixelsAtOneUnit, pixelsPerUnit) = Projection.PixelScale(projection, screenHeight);
        Parallel = projection.M34 == 0f;

        int kept = 0;
        int shadowed = 0;
        float nearest = float.MaxValue;
        float furthest = float.MinValue;
        for (int i = 0; i < lights.Length; i++)
        {
            ref readonly var light = ref lights[i];
            if (light.Range <= 0f || !frustum.CanSee(light.Position, light.Range))
            {
                continue;
            }

            var seen = Vector3.Transform(light.Position, view);
            float depth = -seen.Z;
            nearest = MathF.Min(nearest, depth - light.Range);
            furthest = MathF.Max(furthest, depth + light.Range);

            // How many pixels the light's reach spans on screen, from its middle to its edge.
            float pixels = light.Range * (pixelsPerUnit + pixelsAtOneUnit / MathF.Max(seen.Length(), light.Range));
            _pixels[kept] = pixels;
            if (light.CastsShadow)
            {
                _shadowed[shadowed] = kept;
                _priority[shadowed] = -pixels; // Biggest first.
                shadowed++;
            }

            _kept[kept++] = i;
        }

        LightCount = kept;
        if (Parallel)
        {
            (NearDepth, FarDepth) = (nearest, MathF.Max(furthest, nearest + 0.01f));
        }
        else
        {
            float near = MathF.Max(MathF.Max(nearest, projection.M43), NearestSlice);
            (NearDepth, FarDepth) = (near, MathF.Max(furthest, near * 1.01f));
        }

        ChooseSizes(lights, shadowed);
        PlaceTiles(lights, shadowed, gpuTiles);

        for (int k = 0; k < kept; k++)
        {
            ref readonly var light = ref lights[_kept[k]];
            float cosOuter = MathF.Cos(light.OuterAngle);
            float cosInner = MathF.Cos(MathF.Min(light.InnerAngle, light.OuterAngle));
            gpuLights[k] = new GpuLocalLight
            {
                PositionRange = new Vector4(light.Position, light.Range),
                RadianceShadow = new Vector4(light.Radiance, _sizes[k] > 0 ? _sizeKeys[k] : -1f),
                DirectionCone = light.IsSpot
                    ? new Vector4(Vector3.Normalize(light.Direction), cosOuter)
                    : new Vector4(0f, 0f, 0f, -2f),
                Fade = new Vector4(1f / MathF.Max(cosInner - cosOuter, 1e-4f), RadiusOf(light), 0f, 0f),
            };
        }
    }

    /// <summary>
    /// Gives each shadowed light the tile size its look on screen asks for, biggest first,
    /// halving what's left over until it fits in the atlas. Those that don't fit get none.
    /// </summary>
    private void ChooseSizes(ReadOnlySpan<LocalLight> lights, int shadowed)
    {
        _sizes.AsSpan(0, LightCount).Clear();
        _priority.AsSpan(0, shadowed).Sort(_shadowed.AsSpan(0, shadowed));
        (_lastSizes, _nextSizes) = (_nextSizes, _lastSizes);
        _nextSizes.Clear();

        long free = (long)AtlasSize * AtlasSize;
        for (int s = 0; s < shadowed; s++)
        {
            int k = _shadowed[s];
            ref readonly var light = ref lights[_kept[k]];
            float pixels = _pixels[k];
            int size = Math.Clamp((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(pixels, 1f)), MinTile, MaxTile);

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
    private void PlaceTiles(ReadOnlySpan<LocalLight> lights, int shadowed, Span<GpuShadowTile> gpuTiles)
    {
        int count = 0;
        for (int s = 0; s < shadowed; s++)
        {
            int k = _shadowed[s];
            if (_sizes[k] > 0)
            {
                _bySize[count] = k;
                _priority[count] = -_sizes[k];
                count++;
            }
        }

        _priority.AsSpan(0, count).Sort(_bySize.AsSpan(0, count));

        long offset = 0;
        int tiles = 0;
        for (int t = 0; t < count; t++)
        {
            int k = _bySize[t];
            ref readonly var light = ref lights[_kept[k]];
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

        TileCount = tiles;
    }

    /// <summary>The radius the shaders use: the light's own, or the default when it's 0 or not a size.</summary>
    internal static float RadiusOf(in LocalLight light) =>
        light.Radius > 0f && float.IsFinite(light.Radius) ? light.Radius : LocalLight.DefaultRadius;

    private static (Vector3 Forward, Vector3 Up) SpotAxes(Vector3 direction)
    {
        var forward = Vector3.Normalize(direction);
        return (forward, MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
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
