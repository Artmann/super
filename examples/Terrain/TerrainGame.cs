using System.Numerics;
using Rendition;
using Super;
using Super.Terrain;

namespace Terrain;

/// <summary>What a player can do: fly around.</summary>
public enum Control
{
    /// <summary>Move along the ground plane, forward being positive y.</summary>
    Move,

    /// <summary>Turn the camera, in radians.</summary>
    Look,

    /// <summary>Climb or sink.</summary>
    Rise,
}

/// <summary>
/// Endless hills made from noise, textured by height and slope. Chunks appear around the
/// camera as it flies and are unloaded behind it.
/// </summary>
public sealed class TerrainGame(GameRandom random, Actions<Control> actions, Input input, Content content) : IGame
{
    internal const int ChunkQuads = 32;
    internal const float Spacing = 2f;
    internal const float ChunkSize = ChunkQuads * Spacing;
    internal const int LoadRadius = 4;
    internal const int UnloadRadius = LoadRadius + 1;
    private const float Speed = 24f;
    private const float TurnPerPixel = 0.0025f;
    private const float TurnPerSecond = 2.5f;
    private const float UvPerMetre = 0.125f;
    private const int SplatSize = 32;
    private const int LayerSize = 64;
    private const float SnowHeight = 26f;
    private const float SnowFade = 6f;
    private const float RockSteepness = 0.18f;
    private const float RockFade = 0.12f;

    private readonly Dictionary<(int X, int Z), Chunk> _chunks = [];
    private readonly List<(int X, int Z)> _leaving = [];
    private Heightfield _field = null!;
    private Noise _ground = null!;
    private Noise _moisture = null!;
    private ShaderRef _shader;
    private TextureArrayRef _colors;
    private TextureArrayRef _normals;
    private Vector3 _position;
    private Vector3 _previousPosition;
    private float _yaw;
    private float _pitch = -0.25f;
    private float _previousYaw;
    private float _previousPitch;

    /// <summary>The camera's place in the world.</summary>
    internal Vector3 Position => _position;

    /// <summary>Which chunks are loaded right now, by chunk coordinates.</summary>
    internal IReadOnlyCollection<(int X, int Z)> LoadedChunks => _chunks.Keys;

    /// <summary>How high the ground is anywhere.</summary>
    internal float GroundHeight(float x, float z) => _field.Height(x, z);

    public void Load()
    {
        actions.Bind(Control.Move, Keys.WASD, Keys.Arrows, GamepadStick.Left);
        actions.Bind(Control.Look, Mouse.Delta.Scaled(TurnPerPixel), ((Binding)GamepadStick.Right).Scaled(TurnPerSecond / 30f));
        actions.Bind(Control.Rise, Binding.Axis((Binding)Key.LeftShift, (Binding)Key.Space), Binding.Axis((Binding)GamepadTrigger.Left, (Binding)GamepadTrigger.Right));

        _ground = new Noise(random.Stream("ground"));
        _moisture = new Noise(random.Stream("moisture"));
        _field = new Heightfield(Height, Spacing);
        _shader = content.Shader("terrain");

        var looks = random.Stream("looks");
        TextureRef[] colorLayers =
        [
            content.Texture(ColorLayer(looks, new Vector3(0.66f, 0.58f, 0.40f), 0.05f)),
            content.Texture(ColorLayer(looks, new Vector3(0.28f, 0.46f, 0.17f), 0.08f)),
            content.Texture(ColorLayer(looks, new Vector3(0.44f, 0.42f, 0.40f), 0.14f)),
            content.Texture(ColorLayer(looks, new Vector3(0.93f, 0.95f, 0.98f), 0.02f)),
        ];
        TextureRef[] bumpLayers =
        [
            content.Texture(BumpLayer(looks, 0.015f, 3), TextureKind.Linear, normalMap: true),
            content.Texture(BumpLayer(looks, 0.02f, 7), TextureKind.Linear, normalMap: true),
            content.Texture(BumpLayer(looks, 0.12f, 2), TextureKind.Linear, normalMap: true),
            content.Texture(BumpLayer(looks, 0.015f, 4), TextureKind.Linear, normalMap: true),
        ];
        _colors = content.TextureArray(colorLayers);
        _normals = content.TextureArray(bumpLayers);

        // The arrays keep their own copy of each layer, so the single textures need not
        // take texture slots as well.
        foreach (var layer in colorLayers)
        {
            content.Unload(layer);
        }

        foreach (var layer in bumpLayers)
        {
            content.Unload(layer);
        }

        _position = new Vector3(0f, Height(0f, 0f) + 14f, 0f);
        _previousPosition = _position;
        _previousYaw = _yaw;
        _previousPitch = _pitch;
        UpdateChunks();
    }

    public void Update(GameTime time)
    {
        input.CaptureMouse = true;
        _previousPosition = _position;
        _previousYaw = _yaw;
        _previousPitch = _pitch;

        var look = actions.Vector(Control.Look);
        _yaw -= look.X;
        _pitch = Math.Clamp(_pitch - look.Y, -1.5f, 1.5f);

        var move = actions.Vector(Control.Move);
        var forward = Forward(_yaw, 0f);
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var step = (forward * move.Y + right * move.X) * (Speed * time.TickSeconds);
        step.Y += actions.Value(Control.Rise) * Speed * 0.6f * time.TickSeconds;
        _position += step;

        // Stay a little above the ground even when diving.
        float floor = Height(_position.X, _position.Z) + 2f;
        if (_position.Y < floor)
        {
            _position.Y = floor;
        }

        UpdateChunks();
    }

    public void Draw(View view)
    {
        var position = Vector3.Lerp(_previousPosition, _position, view.Blend);
        float yaw = _previousYaw + (_yaw - _previousYaw) * view.Blend;
        float pitch = _previousPitch + (_pitch - _previousPitch) * view.Blend;
        view.Camera(position, position + Forward(yaw, pitch), 60f * MathF.PI / 180f);
        view.Sun(new Vector3(0.5f, 0.8f, 0.3f), new Vector3(3.6f, 3.4f, 3.1f));
        view.Shadows(40f, 3);
        view.Finish(new PostSettings { Contrast = 0.08f, Saturation = 0.08f, Vignette = 0.25f });

        // A chunk still being prepared on the loading thread is skipped rather than waited for.
        foreach (var (key, chunk) in _chunks)
        {
            if (content.IsReady(chunk.Model))
            {
                view.Model(DrawKey(key), chunk.Model, Matrix4x4.Identity);
            }
        }
    }

    private static int DrawKey((int X, int Z) chunk) => ((chunk.X & 0xFFFF) << 16) | (chunk.Z & 0xFFFF);

    private static Vector3 Forward(float yaw, float pitch) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));

    private float Height(float x, float z)
    {
        float swell = 10f * MathF.Sin(x * 0.011f) * MathF.Cos(z * 0.009f);
        float hills = (_ground.Layered(x * 0.0035f, z * 0.0035f, 5) - 0.45f) * 120f;
        return swell + hills;
    }

    /// <summary>Loads chunks that came near the camera and unloads those it left well behind.</summary>
    private void UpdateChunks()
    {
        int centerX = (int)MathF.Floor(_position.X / ChunkSize);
        int centerZ = (int)MathF.Floor(_position.Z / ChunkSize);

        foreach (var key in _chunks.Keys)
        {
            if (Math.Max(Math.Abs(key.X - centerX), Math.Abs(key.Z - centerZ)) > UnloadRadius)
            {
                _leaving.Add(key);
            }
        }

        foreach (var key in _leaving)
        {
            var chunk = _chunks[key];
            _chunks.Remove(key);
            content.Unload(chunk.Model);
            content.Unload(chunk.Splat);
        }

        _leaving.Clear();

        for (int z = centerZ - LoadRadius; z <= centerZ + LoadRadius; z++)
        {
            for (int x = centerX - LoadRadius; x <= centerX + LoadRadius; x++)
            {
                if (!_chunks.ContainsKey((x, z)))
                {
                    _chunks.Add((x, z), LoadChunk(x, z));
                }
            }
        }
    }

    private Chunk LoadChunk(int chunkX, int chunkZ)
    {
        var splat = content.Texture(Splat(chunkX, chunkZ), TextureKind.Linear);
        var surface = new Surface
        {
            Color = Vector3.One,
            Roughness = 0.9f,
            Shader = _shader,
            Parameters =
            [
                new Vector4(SnowHeight, SnowFade, RockSteepness, RockFade),
                new Vector4(chunkX * ChunkSize, chunkZ * ChunkSize, 1f / ChunkSize, SplatSize),
            ],
            ShaderTextures = [splat],
            ShaderArrays = [_colors, _normals],
        };
        var model = content.Mesh(_field.Chunk(chunkX, chunkZ, ChunkQuads, surface, UvPerMetre));
        return new Chunk(model, splat);
    }

    /// <summary>How grassy each spot of a chunk is: damp lowlands grow grass and dry ground stays sand.</summary>
    private GeneratedTexture Splat(int chunkX, int chunkZ)
    {
        var pixels = new byte[SplatSize * SplatSize * 4];
        for (int row = 0; row < SplatSize; row++)
        {
            for (int column = 0; column < SplatSize; column++)
            {
                // The edge texels sit on the chunk's edges, so neighbours agree there.
                float x = (chunkX + column / (SplatSize - 1f)) * ChunkSize;
                float z = (chunkZ + row / (SplatSize - 1f)) * ChunkSize;
                float damp = _moisture.Layered(x * 0.01f, z * 0.01f, 3);
                float low = 1f - Math.Clamp((Height(x, z) - 4f) / 14f, 0f, 1f);
                float grassy = Math.Clamp((damp * 0.7f + low * 0.5f - 0.35f) * 3f, 0f, 1f);
                int offset = (row * SplatSize + column) * 4;
                pixels[offset] = (byte)(grassy * 255f);
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 255;
            }
        }

        return new GeneratedTexture(SplatSize, pixels);
    }

    private static GeneratedTexture ColorLayer(RandomStream stream, Vector3 color, float grain)
    {
        var mottle = Tileable(stream, 4);
        var pixels = new byte[LayerSize * LayerSize * 4];
        for (int i = 0; i < LayerSize * LayerSize; i++)
        {
            float shade = (0.92f + mottle[i] * 0.16f) * (1f + stream.Between(-grain, grain));
            pixels[i * 4] = Byte(color.X * shade);
            pixels[i * 4 + 1] = Byte(color.Y * shade);
            pixels[i * 4 + 2] = Byte(color.Z * shade);
            pixels[i * 4 + 3] = 255;
        }

        return new GeneratedTexture(LayerSize, pixels);
    }

    /// <summary>A normal map of gentle random bumps, so each layer catches light its own way.</summary>
    private static GeneratedTexture BumpLayer(RandomStream stream, float strength, int bumpsAcross)
    {
        var heights = Tileable(stream, bumpsAcross);
        var pixels = new byte[LayerSize * LayerSize * 4];
        for (int row = 0; row < LayerSize; row++)
        {
            for (int column = 0; column < LayerSize; column++)
            {
                float slopeX = heights[row * LayerSize + (column + 1) % LayerSize] - heights[row * LayerSize + (column + LayerSize - 1) % LayerSize];
                float slopeY = heights[((row + 1) % LayerSize) * LayerSize + column] - heights[((row + LayerSize - 1) % LayerSize) * LayerSize + column];
                var normal = Vector3.Normalize(new Vector3(-slopeX * strength * LayerSize, -slopeY * strength * LayerSize, 1f));
                int offset = (row * LayerSize + column) * 4;
                pixels[offset] = Byte(normal.X * 0.5f + 0.5f);
                pixels[offset + 1] = Byte(normal.Y * 0.5f + 0.5f);
                pixels[offset + 2] = Byte(normal.Z * 0.5f + 0.5f);
                pixels[offset + 3] = 255;
            }
        }

        return new GeneratedTexture(LayerSize, pixels);
    }

    /// <summary>
    /// Smooth random values over the texture that wrap at its edges, so the tiles meet
    /// without a seam. There are this many bumps across the texture.
    /// </summary>
    private static float[] Tileable(RandomStream stream, int cells)
    {
        var lattice = new float[cells * cells];
        for (int i = 0; i < lattice.Length; i++)
        {
            lattice[i] = stream.Float();
        }

        var values = new float[LayerSize * LayerSize];
        for (int row = 0; row < LayerSize; row++)
        {
            for (int column = 0; column < LayerSize; column++)
            {
                float x = column * cells / (float)LayerSize;
                float y = row * cells / (float)LayerSize;
                int cellX = (int)x;
                int cellY = (int)y;
                float fx = Smooth(x - cellX);
                float fy = Smooth(y - cellY);
                int nextX = (cellX + 1) % cells;
                int nextY = (cellY + 1) % cells;
                float top = Lerp(lattice[cellY * cells + cellX], lattice[cellY * cells + nextX], fx);
                float bottom = Lerp(lattice[nextY * cells + cellX], lattice[nextY * cells + nextX], fx);
                values[row * LayerSize + column] = Lerp(top, bottom, fy);
            }
        }

        return values;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte Byte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private readonly record struct Chunk(ModelRef Model, TextureRef Splat);
}
