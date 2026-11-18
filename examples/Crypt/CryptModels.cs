using System.Numerics;
using Super;

namespace Crypt;

/// <summary>Every mesh and model the crypt draws: the hall's stone, built in code, and the props, loaded from files.</summary>
internal sealed record CryptModels(
    ModelRef Floor,
    ModelRef SideWall,
    ModelRef EndWall,
    ModelRef Ceiling,
    ModelRef Pillar,
    ModelRef PillarFoot,
    ModelRef Step,
    ModelRef Altar,
    ModelRef Plinth,
    ModelRef Statue,
    ModelRef Chest,
    ModelRef Lantern,
    ModelRef Barrel,
    ModelRef SevenCandles,
    ModelRef ThreeCandles,
    ModelRef OneCandle)
{
    /// <summary>
    /// Builds the hall's stone and loads the props, in that order. <paramref name="hall"/> is the hall's width, height
    /// and length, and <paramref name="altarTop"/> how high the altar stands.
    /// </summary>
    public static CryptModels Load(Content content, Vector3 hall, float altarTop)
    {
        var floor = Stone(content, "floor");
        var wall = Stone(content, "wall");
        var blocks = Stone(content, "blocks");
        float length = hall.Z;

        return new CryptModels(
            content.Mesh(Shapes.Plane(new Vector2(hall.X, length), surface: floor, uvPerMetre: 0.4f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(0.6f, hall.Y, length), wall, uvPerMetre: 0.35f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(hall.X + 1.2f, hall.Y, 0.6f), wall, uvPerMetre: 0.35f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(hall.X + 1.2f, 0.4f, length + 1.2f), blocks, uvPerMetre: 0.5f), isStatic: true),
            content.Mesh(Shapes.Cylinder(0.34f, 0.38f, hall.Y - 0.6f, 24, blocks, uvPerMetre: 0.6f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(1f, 0.3f, 1f), blocks, uvPerMetre: 0.6f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(4.2f, 0.18f, 2.6f), floor, uvPerMetre: 0.4f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(2.6f, altarTop - 0.18f, 1f), blocks, uvPerMetre: 0.6f), isStatic: true),
            content.Mesh(Shapes.Box(new Vector3(1.8f, 0.7f, 1.4f), blocks, uvPerMetre: 0.6f), isStatic: true),
            content.Model("props/gothic_statue/gothic_statue_1k.gltf"),
            content.Model("props/treasure_chest/treasure_chest_1k.gltf"),
            content.Model("props/Lantern_01/Lantern_01_1k.gltf"),
            content.Model("props/wine_barrel_01/wine_barrel_01_1k.gltf"),
            content.Model("props/brass_candleholders/brass_candleholders_1k.gltf#seven"),
            content.Model("props/brass_candleholders/brass_candleholders_1k.gltf#three"),
            content.Model("props/brass_candleholders/brass_candleholders_1k.gltf#one"));
    }

    private static Surface Stone(Content content, string name) => new()
    {
        Color = Vector3.One,
        Roughness = 1f,
        ColorTexture = content.Texture($"textures/{name}.jpg"),
        NormalTexture = content.Texture($"textures/{name}-normal.jpg"),
        SurfaceTexture = content.Texture($"textures/{name}-surface.png"),
    };
}
