using System.Numerics;
using Rendition;
using Super;
using Super.Terrain;

namespace Archer;

/// <summary>
/// The forest clearing: clumps of grass and wildflowers in the open, ferns, nettles and
/// mossy rocks along the edge, a dark wall of firs all round, a boulder for Erika to leap
/// onto and a fallen log. Each thing in it is an entity.
/// </summary>
public sealed class Clearing
{
    public static readonly Vector3 Middle = new(-1f, 0f, 10f);
    public static readonly Vector3 Rock = new(-5f, -0.1f, 7.5f);
    public const float RockScale = 1.1f;
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

    // Places the plants, the same every time.
    private readonly Random _random = new(11);

    public Clearing(World world, Content content)
    {
        var flora = Flora.Load(content);
        AddGround(content);
        AddLandmarks(content, flora.MossyRocks);
        Plant(flora);
        foreach (var (model, transform, tint) in _things)
        {
            world.Spawn(Transform.From(transform), new Model(model, tint));
        }
    }

    /// <summary>The plants and rocks the clearing is planted with.</summary>
    private sealed record Flora(
        ModelRef TallGrass,
        ModelRef LargeGrass,
        ModelRef Fern,
        ModelRef Shrub,
        ModelRef Nettle,
        ModelRef[] Flowers,
        ModelRef[] Periwinkles,
        ModelRef[] MossyRocks,
        ModelRef Branches,
        ModelRef YoungFir,
        ModelRef[] Firs)
    {
        public static Flora Load(Content content)
        {
            var tallGrass = content.Model("props/grass_medium_01/grass_medium_01.gltf#tall");
            var largeGrass = content.Model("props/grass_medium_01/grass_medium_01.gltf#large");
            var fern = content.Model("props/fern_02/fern_02.gltf");
            var shrub = content.Model("props/shrub_02/shrub_02.gltf");
            var nettle = content.Model("props/nettle_plant/nettle_plant_1k.gltf");
            var flowers = new[] { content.Model("props/dandelion_01/dandelion_01_1k.gltf#a"), content.Model("props/dandelion_01/dandelion_01_1k.gltf#b") };
            var periwinkles = new[] { content.Model("props/periwinkle_plant/periwinkle_plant_1k.gltf#a"), content.Model("props/periwinkle_plant/periwinkle_plant_1k.gltf#b") };
            var mossyRocks = new[] { content.Model("props/rock_moss_set_01/rock_moss_set_01_1k.gltf#a"), content.Model("props/rock_moss_set_01/rock_moss_set_01_1k.gltf#b") };
            var branches = content.Model("props/dry_branches_medium_01/dry_branches_medium_01_1k.gltf");
            var youngFir = content.Model("archer-props/firs.glb#young");
            var firs = new[] { content.Model("archer-props/firs.glb#tall"), content.Model("archer-props/firs.glb#broad"), youngFir };
            return new Flora(tallGrass, largeGrass, fern, shrub, nettle, flowers, periwinkles, mossyRocks, branches, youngFir, firs);
        }
    }

    /// <summary>What a planting puts down: one of these models, sized from smallest to largest, tinted or not.</summary>
    private sealed record Planting(ModelRef[] Models, float Smallest, float Largest, Func<Random, Vector3>? Tint);

    /// <summary>A ring round the middle of the clearing, from near to far, in metres.</summary>
    private readonly record struct PlantRing(float Near, float Far);

    /// <summary>How many clumps there are, about how many plants each has, and how far they spread.</summary>
    private readonly record struct ClumpShape(int Count, int PerClump, float Radius);

    /// <summary>The boulder Erika leaps onto, the fallen trunk, two stumps and a couple of smaller rocks.</summary>
    private void AddLandmarks(Content content, ModelRef[] mossyRocks)
    {
        // The boulder is the larger mossy rock, sunk a little so it sits in the ground.
        _things.Add((mossyRocks[0], Matrix4x4.CreateScale(RockScale) * Matrix4x4.CreateRotationY(0.6f) * Matrix4x4.CreateTranslation(Rock), null));
        _things.Add((content.Model("props/dead_tree_trunk/dead_tree_trunk.gltf"), Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(3.6f, -0.05f, 5.5f), null));
        _things.Add((content.Model("props/tree_stump_01/tree_stump_01.gltf"), Matrix4x4.CreateRotationY(1.2f) * Matrix4x4.CreateTranslation(-9.5f, 0f, 12f), null));
        _things.Add((content.Model("props/tree_stump_02/tree_stump_02_1k.gltf"), Matrix4x4.CreateRotationY(2.4f) * Matrix4x4.CreateTranslation(8.5f, 0f, 14.5f), null));
        _things.Add((mossyRocks[0], Matrix4x4.CreateScale(0.45f) * Matrix4x4.CreateRotationY(1.9f) * Matrix4x4.CreateTranslation(-6.6f, -0.1f, 9.2f), null));
        _things.Add((mossyRocks[1], Matrix4x4.CreateScale(0.3f) * Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(-7.4f, -0.05f, 6.2f), null));
    }

    private void Plant(Flora flora)
    {
        // The open ground: grass in clumps with thinner grass between, and flowers.
        Clumps(new Planting([flora.TallGrass], 1.5f, 2.8f, Grass), new PlantRing(0.5f, 19f), new ClumpShape(260, 9, 1.4f));
        Scatter(new Planting([flora.TallGrass], 1.2f, 2.2f, Grass), 900, new PlantRing(0.5f, 22f), avoidPaths: true);
        Clumps(new Planting([flora.LargeGrass], 0.9f, 1.5f, Grass), new PlantRing(3f, 18f), new ClumpShape(40, 5, 1.2f));
        Clumps(new Planting(flora.Flowers, 1.6f, 2.6f, null), new PlantRing(1.5f, 15f), new ClumpShape(30, 6, 1.1f));
        Clumps(new Planting(flora.Periwinkles, 0.8f, 1.2f, null), new PlantRing(3f, 16f), new ClumpShape(18, 5, 0.9f));
        Scatter(new Planting([flora.Branches], 0.6f, 1.1f, null), 9, new PlantRing(3f, 16f), avoidPaths: true);

        // The edge of the forest: undergrowth, rocks and young firs coming out into the light.
        Clumps(new Planting([flora.Fern], 0.9f, 1.6f, null), new PlantRing(13f, 20f), new ClumpShape(24, 4, 1.6f));
        Clumps(new Planting([flora.Nettle], 3f, 5f, null), new PlantRing(12f, 19f), new ClumpShape(16, 6, 0.9f));
        Scatter(new Planting([flora.Shrub], 0.8f, 1.4f, null), 30, new PlantRing(15f, 22f), avoidPaths: false);
        Scatter(new Planting(flora.MossyRocks, 0.25f, 0.6f, null), 10, new PlantRing(13f, 22f), avoidPaths: false);
        Scatter(new Planting([flora.YoungFir], 0.7f, 1.2f, Fir), 45, new PlantRing(15.5f, 21f), avoidPaths: false);

        // The wall of firs, dark against the bright clearing, thick enough that no gap shows.
        Scatter(new Planting(flora.Firs, 0.85f, 1.35f, Fir), 220, new PlantRing(18.5f, 46f), avoidPaths: false);

        // Undergrowth among the trunks, so the far side of the forest never shows beneath them.
        Scatter(new Planting([flora.YoungFir], 0.6f, 1f, Fir), 90, new PlantRing(21f, 40f), avoidPaths: false);
        Scatter(new Planting([flora.Shrub], 1f, 1.6f, Shade), 70, new PlantRing(20f, 34f), avoidPaths: false);
    }

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
            Shader = new SurfaceShader(content.Shader("clearing"))
            {
                Parameters = [new Vector4(Middle.X, Middle.Z, ForestStart, ForestFull), new Vector4(Worn.X, Worn.Z, WornRadius, 0f)],
                Arrays = [content.TextureArray(colors), content.TextureArray(normals)],
            },
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
    private void Clumps(Planting planting, PlantRing ring, ClumpShape shape)
    {
        for (int c = 0; c < shape.Count; c++)
        {
            var middle = InRing(ring);
            int count = shape.PerClump / 2 + _random.Next(shape.PerClump);
            for (int i = 0; i < count; i++)
            {
                float angle = _random.NextSingle() * MathF.Tau;
                // Squaring the distance packs them tighter in the middle of the clump.
                float distance = shape.Radius * _random.NextSingle() * _random.NextSingle();
                Place(planting, middle + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance, avoidPaths: true);
            }
        }
    }

    private void Scatter(Planting planting, int count, PlantRing ring, bool avoidPaths)
    {
        for (int i = 0; i < count; i++)
        {
            Place(planting, InRing(ring), avoidPaths);
        }
    }

    private Vector3 InRing(PlantRing ring)
    {
        float angle = _random.NextSingle() * MathF.Tau;
        // The square root spreads them evenly over the ring rather than bunching them inside.
        float distance = MathF.Sqrt(float.Lerp(ring.Near * ring.Near, ring.Far * ring.Far, _random.NextSingle()));
        return Middle + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance;
    }

    private void Place(Planting planting, Vector3 at, bool avoidPaths)
    {
        if (avoidPaths && (Vector3.Distance(at with { Y = 0f }, Rock with { Y = 0f }) < 2f || Vector3.Distance(at, Worn) < WornRadius * 0.45f))
        {
            return;
        }

        at.Y = Height(at.X, at.Z);
        float size = float.Lerp(planting.Smallest, planting.Largest, _random.NextSingle());
        var model = planting.Models[_random.Next(planting.Models.Length)];
        var transform = Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationY(_random.NextSingle() * MathF.Tau) * Matrix4x4.CreateTranslation(at);
        _things.Add((model, transform, planting.Tint?.Invoke(_random)));
    }
}
