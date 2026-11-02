using System.Numerics;
using Rendition;
using Super;
using Super.Terrain;

namespace Archer;

/// <summary>
/// The forest clearing: grass, ferns and shrubs, a ring of firs, a boulder for Erika to
/// leap onto, and a fallen log. It never changes, so it's drawn once and replayed.
/// </summary>
public sealed class Clearing
{
    public static readonly Vector3 Middle = new(-1f, 0f, 10f);
    public static readonly Vector3 Rock = new(-5f, 0f, 7.5f);
    public const float RockScale = 1.3f;
    private const int FirstKey = 1000;
    private const int ChunkQuads = 32;
    private const int GroundChunks = 3; // Each way from zero, so the ground is 192 metres wide.

    /// <summary>
    /// Flat across the clearing, where everything happens, then rising into low hills
    /// behind the trees so the world has no edge in sight.
    /// </summary>
    public static readonly Heightfield Field = new(Height, spacing: 1f);

    private readonly List<(ModelRef Model, Matrix4x4 Transform, Vector3? Tint)> _things = [];

    public Clearing(Content content)
    {
        var random = new Random(11);
        var tallGrass = content.Model("props/grass-tall.rmodel");
        var largeGrass = content.Model("props/grass-large.rmodel");
        var fern = content.Model("props/fern.rmodel");
        var shrub = content.Model("props/shrub.rmodel");
        var firs = new[] { content.Model("props/fir-a.rmodel"), content.Model("props/fir-b.rmodel") };

        var ground = new Surface
        {
            Color = new Vector3(0.62f, 0.85f, 0.42f),
            Roughness = 0.95f,
            ColorTexture = content.Texture("textures/grass-ground.jpg"),
            NormalTexture = content.Texture("textures/grass-ground-normal.jpg", TextureKind.Linear, normalMap: true),
        };
        for (int z = -GroundChunks; z < GroundChunks; z++)
        {
            for (int x = -GroundChunks; x < GroundChunks; x++)
            {
                _things.Add((content.Mesh(Field.Chunk(x, z, ChunkQuads, ground, uvPerMetre: 0.3f)), Matrix4x4.Identity, null));
            }
        }

        _things.Add((content.Model("props/boulder.rmodel"), Matrix4x4.CreateScale(RockScale) * Matrix4x4.CreateRotationY(0.6f) * Matrix4x4.CreateTranslation(Rock), null));
        _things.Add((content.Model("props/log.rmodel"), Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(3.6f, 0f, 5.5f), null));
        _things.Add((content.Model("props/stump.rmodel"), Matrix4x4.CreateRotationY(1.2f) * Matrix4x4.CreateTranslation(-9.5f, 0f, 12f), null));

        Scatter(random, [tallGrass], 3500, 0.5f, 24f, 1.6f, 2.8f, avoidPaths: true);
        Scatter(random, [largeGrass], 250, 3f, 20f, 0.9f, 1.5f, avoidPaths: true);
        Scatter(random, [fern], 70, 10f, 18f, 0.9f, 1.6f, avoidPaths: true);
        Scatter(random, [shrub], 45, 14f, 21f, 0.8f, 1.4f, avoidPaths: false);
        Scatter(random, firs, 72, 18f, 40f, 0.9f, 1.7f, avoidPaths: false);
    }

    public void Draw(View view) => view.Cached(FirstKey, 1, _things, static (things, view) =>
    {
        for (int i = 0; i < things.Count; i++)
        {
            var (model, transform, tint) = things[i];
            view.Model(FirstKey + i, model, transform, tint);
        }
    });

    private static float Height(float x, float z)
    {
        float distance = new Vector2(x - Middle.X, z - Middle.Z).Length();
        float rise = Smooth(24f, 70f, distance);
        float hills = 3.5f + 2f * MathF.Sin(x * 0.05f + 1.3f) * MathF.Cos(z * 0.04f) + 0.8f * MathF.Sin((x + z) * 0.11f);
        return rise * hills;
    }

    private static float Smooth(float from, float to, float value)
    {
        float amount = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return amount * amount * (3f - 2f * amount);
    }

    private void Scatter(Random random, ModelRef[] models, int count, float near, float far, float smallest, float largest, bool avoidPaths)
    {
        for (int i = 0; i < count; i++)
        {
            float angle = random.NextSingle() * MathF.Tau;
            // The square root spreads them evenly over the ring rather than bunching them inside.
            float distance = MathF.Sqrt(float.Lerp(near * near, far * far, random.NextSingle()));
            var at = Middle + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance;
            if (avoidPaths && Vector3.Distance(at, Rock) < 1.6f)
            {
                continue;
            }

            at.Y = Height(at.X, at.Z);
            float size = float.Lerp(smallest, largest, random.NextSingle());
            var model = models[random.Next(models.Length)];
            var transform = Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationY(random.NextSingle() * MathF.Tau) * Matrix4x4.CreateTranslation(at);
            _things.Add((model, transform, null));
        }
    }
}
