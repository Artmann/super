using System.Numerics;
using Rendition;
using Super;
using Super.Terrain;

namespace Archer;

/// <summary>
/// The forest clearing: clumps of grass and wildflowers in the open, ferns, nettles and
/// mossy rocks along the edge, a dark wall of firs all round, a boulder for Erika to leap
/// onto and a fallen log. It never changes, so it's drawn once and replayed.
/// </summary>
public sealed class Clearing
{
    public static readonly Vector3 Middle = new(-1f, 0f, 10f);
    public static readonly Vector3 Rock = new(-5f, -0.1f, 7.5f);
    public const float RockScale = 1.1f;
    private const int FirstKey = 1000;
    private const int ChunkQuads = 32;
    private const int GroundChunks = 3; // Each way from zero, so the ground is 192 metres wide.

    // Where the forest floor takes over from grass, in metres from the middle.
    private const float ForestStart = 16.5f;
    private const float ForestFull = 23f;

    // The trodden patch where Erika starts.
    private static readonly Vector3 Worn = new(0.2f, 0f, 1.2f);
    private const float WornRadius = 3.2f;

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
        var nettle = content.Model("props/nettle.rmodel");
        var flowers = new[] { content.Model("props/dandelion-a.rmodel"), content.Model("props/dandelion-b.rmodel") };
        var periwinkles = new[] { content.Model("props/periwinkle-a.rmodel"), content.Model("props/periwinkle-b.rmodel") };
        var mossyRocks = new[] { content.Model("props/mossy-rock-a.rmodel"), content.Model("props/mossy-rock-b.rmodel") };
        var branches = content.Model("props/branches.rmodel");
        var youngFir = content.Model("props/fir-young.rmodel");
        var firs = new[] { content.Model("props/fir-tall.rmodel"), content.Model("props/fir-broad.rmodel"), youngFir };

        AddGround(content);

        // The boulder is the larger mossy rock, sunk a little so it sits in the ground.
        _things.Add((mossyRocks[0], Matrix4x4.CreateScale(RockScale) * Matrix4x4.CreateRotationY(0.6f) * Matrix4x4.CreateTranslation(Rock), null));
        _things.Add((content.Model("props/log.rmodel"), Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(3.6f, -0.05f, 5.5f), null));
        _things.Add((content.Model("props/stump.rmodel"), Matrix4x4.CreateRotationY(1.2f) * Matrix4x4.CreateTranslation(-9.5f, 0f, 12f), null));
        _things.Add((content.Model("props/stump-b.rmodel"), Matrix4x4.CreateRotationY(2.4f) * Matrix4x4.CreateTranslation(8.5f, 0f, 14.5f), null));
        _things.Add((mossyRocks[0], Matrix4x4.CreateScale(0.45f) * Matrix4x4.CreateRotationY(1.9f) * Matrix4x4.CreateTranslation(-6.6f, -0.1f, 9.2f), null));
        _things.Add((mossyRocks[1], Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(-7.4f, -0.05f, 6.2f), null));

        // The open ground: grass in clumps with thinner grass between, and flowers.
        Clumps(random, [tallGrass], 260, 9, 1.4f, 0.5f, 19f, 1.5f, 2.8f, Grass);
        Scatter(random, [tallGrass], 900, 0.5f, 22f, 1.2f, 2.2f, avoidPaths: true, Grass);
        Clumps(random, [largeGrass], 40, 5, 1.2f, 3f, 18f, 0.9f, 1.5f, Grass);
        Clumps(random, flowers, 30, 6, 1.1f, 1.5f, 15f, 1.6f, 2.6f, null);
        Clumps(random, periwinkles, 18, 5, 0.9f, 3f, 16f, 0.8f, 1.2f, null);
        Scatter(random, [branches], 9, 3f, 16f, 0.6f, 1.1f, avoidPaths: true, null);

        // The edge of the forest: undergrowth, rocks and young firs coming out into the light.
        Clumps(random, [fern], 24, 4, 1.6f, 13f, 20f, 0.9f, 1.6f, null);
        Clumps(random, [nettle], 16, 6, 0.9f, 12f, 19f, 3f, 5f, null);
        Scatter(random, [shrub], 30, 15f, 22f, 0.8f, 1.4f, avoidPaths: false, null);
        Scatter(random, mossyRocks, 10, 13f, 22f, 0.25f, 0.6f, avoidPaths: false, null);
        Scatter(random, [youngFir], 45, 15.5f, 21f, 0.7f, 1.2f, avoidPaths: false, Fir);

        // The wall of firs, dark against the bright clearing, thick enough that no gap shows.
        Scatter(random, firs, 220, 18.5f, 46f, 0.85f, 1.35f, avoidPaths: false, Fir);

        // Undergrowth among the trunks, so the far side of the forest never shows beneath them.
        Scatter(random, [youngFir], 90, 21f, 40f, 0.6f, 1f, avoidPaths: false, Fir);
        Scatter(random, [shrub], 70, 20f, 34f, 1f, 1.6f, avoidPaths: false, Shade);
    }

    public void Draw(View view) => view.Cached(FirstKey, 1, _things, static (things, view) =>
    {
        for (int i = 0; i < things.Count; i++)
        {
            var (model, transform, tint) = things[i];
            view.Model(FirstKey + i, model, transform, tint);
        }
    });

    private void AddGround(Content content)
    {
        TextureRef[] colors =
        [
            content.Texture("textures/grass-ground.jpg"),
            content.Texture("textures/forest-floor.jpg"),
            content.Texture("textures/leaves.jpg"),
        ];
        TextureRef[] normals =
        [
            content.Texture("textures/grass-ground-normal.jpg"),
            content.Texture("textures/forest-floor-normal.jpg"),
            content.Texture("textures/leaves-normal.jpg"),
        ];
        var ground = new Surface
        {
            Color = new Vector3(0.7f, 0.82f, 0.45f),
            Roughness = 0.95f,
            Shader = content.Shader("clearing"),
            Parameters =
            [
                new Vector4(Middle.X, Middle.Z, ForestStart, ForestFull),
                new Vector4(Worn.X, Worn.Z, WornRadius, 0f),
            ],
            ShaderArrays = [content.TextureArray(colors), content.TextureArray(normals)],
        };

        // The arrays keep their own copy of each layer.
        foreach (var texture in colors.Concat(normals))
        {
            content.Unload(texture);
        }

        for (int z = -GroundChunks; z < GroundChunks; z++)
        {
            for (int x = -GroundChunks; x < GroundChunks; x++)
            {
                _things.Add((content.Mesh(Field.Chunk(x, z, ChunkQuads, ground, uvPerMetre: 0.3f)), Matrix4x4.Identity, null));
            }
        }
    }

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

    // Grass varies from lush to a little sun-dried.
    private static Vector3 Grass(Random random) => Vector3.Lerp(new Vector3(0.85f, 1f, 0.8f), new Vector3(1.1f, 1f, 0.7f), random.NextSingle());

    // Firs are darkened so the forest reads as one deep mass behind the lit clearing.
    private static Vector3 Fir(Random random) => Vector3.Lerp(new Vector3(0.42f, 0.5f, 0.45f), new Vector3(0.6f, 0.66f, 0.55f), random.NextSingle());

    // Plants in the shade of the forest are darker than the same ones out in the clearing.
    private static Vector3 Shade(Random random) => new Vector3(0.55f, 0.6f, 0.55f) * float.Lerp(0.8f, 1f, random.NextSingle());

    // Groups of plants: each clump has a middle in the ring, and its plants fall around it.
    private void Clumps(Random random, ModelRef[] models, int clumps, int perClump, float clumpRadius, float near, float far, float smallest, float largest, Func<Random, Vector3>? tint)
    {
        for (int c = 0; c < clumps; c++)
        {
            var middle = InRing(random, near, far);
            int count = perClump / 2 + random.Next(perClump);
            for (int i = 0; i < count; i++)
            {
                float angle = random.NextSingle() * MathF.Tau;
                // Squaring the distance packs them tighter in the middle of the clump.
                float distance = clumpRadius * random.NextSingle() * random.NextSingle();
                Place(random, models, middle + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance, smallest, largest, avoidPaths: true, tint);
            }
        }
    }

    private void Scatter(Random random, ModelRef[] models, int count, float near, float far, float smallest, float largest, bool avoidPaths, Func<Random, Vector3>? tint)
    {
        for (int i = 0; i < count; i++)
        {
            Place(random, models, InRing(random, near, far), smallest, largest, avoidPaths, tint);
        }
    }

    private static Vector3 InRing(Random random, float near, float far)
    {
        float angle = random.NextSingle() * MathF.Tau;
        // The square root spreads them evenly over the ring rather than bunching them inside.
        float distance = MathF.Sqrt(float.Lerp(near * near, far * far, random.NextSingle()));
        return Middle + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance;
    }

    private void Place(Random random, ModelRef[] models, Vector3 at, float smallest, float largest, bool avoidPaths, Func<Random, Vector3>? tint)
    {
        if (avoidPaths && (Vector3.Distance(at with { Y = 0f }, Rock with { Y = 0f }) < 2f || Vector3.Distance(at, Worn) < WornRadius * 0.45f))
        {
            return;
        }

        at.Y = Height(at.X, at.Z);
        float size = float.Lerp(smallest, largest, random.NextSingle());
        var model = models[random.Next(models.Length)];
        var transform = Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationY(random.NextSingle() * MathF.Tau) * Matrix4x4.CreateTranslation(at);
        _things.Add((model, transform, tint?.Invoke(random)));
    }
}
