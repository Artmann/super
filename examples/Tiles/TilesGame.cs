using System.Numerics;
using Super;

namespace TilesExample;

/// <summary>
/// Two tile maps loaded from JSON levels: a small dungeon on a square grid, whose walls join up as they're painted, and
/// a hex island whose coast slopes down wherever it meets the water, with trees on a second floor. The tiles are made
/// in code; the levels in content/levels only say which group each cell paints.
/// </summary>
public sealed class TilesGame(World world, Content content) : IGame
{
    private readonly SquareGrid _dungeonGrid = new(2f, new GridPlacement(new Vector3(-23f, 0f, -8f)));
    private readonly LayeredGrid<Hex> _islandFloors = new(new HexGrid(1.1f, HexTop.Flat, new GridPlacement(new Vector3(11f, 0f, 0f))), 0f, 0.5f);
    private TileMap<Cell>? _dungeon;
    private TileMap<Hex>? _island;

    public void Load()
    {
        var levels = content.LoadAll<TileLevel>("levels");
        _dungeon = new TileMap<Cell>(new TileSet<Cell>(TilePieces.Dungeon(content, _dungeonGrid)), _dungeonGrid);
        _dungeon.Load(levels["dungeon"]);
        _island = new TileMap<Hex>(new TileSet<Hex>(TilePieces.Island(content, (HexGrid)_islandFloors[0])), _islandFloors);
        _island.Load(levels["island"]);

        var ground = content.Mesh(Shapes.Plane(new Vector2(30f, 24f), surface: new Surface { Color = new Vector3(0.2f, 0.19f, 0.18f), Roughness = 0.95f }));
        world.Spawn(new Transform(new Vector3(-12f, -0.01f, 0f), Quaternion.Identity, Vector3.One), new Model(ground));
        world.Spawn(new Camera(new Vector3(-1f, 25f, 21f), new Vector3(-1f, 0f, 0.5f), 0.75f));
        world.Spawn(new Sun(new Vector3(-0.6f, 1.2f, 0.5f), new Vector3(3f)), new Shadows(40f, 2));
    }

    public void Update(GameTime time)
    {
    }

    public void Draw(View view)
    {
        view.TileMap(1, _dungeon!);
        view.TileMap(2, _island!);
    }
}
