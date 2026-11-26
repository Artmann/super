using System.Numerics;
using Super;

namespace PlotsExample;

/// <summary>
/// Grids of every kind side by side: a 2 m building grid and a 0.5 m farm grid on the ground, a 1 m grid on a deck that
/// is raised and turned, and a hex field with rocks. The mouse picks a cell on whichever grid it's over, or the screen's
/// middle does until the mouse moves. G, L and H show and hide the outlines, the coordinates and how far a walker from
/// the field's corner gets, as <c>--debug grid,grid.labels,grid.heat:walk</c> does.
/// </summary>
public sealed class PlotsGame(World world, Content content, Input input, Grids grids, DebugDraw debug) : IGame
{
    private const string Walk = "grid.heat:walk";
    private const float DeckSize = 5f;
    private const float DeckTop = 2.2f;
    private const int FieldSteps = 3;

    private static readonly Color Hover = new(1f, 0.85f, 0.2f);
    private static readonly Vector3 DeckMiddle = new(0f, DeckTop, -1.5f);
    private static readonly Quaternion DeckTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.45f);
    private static readonly Hex[] Rocks = [new(0, 0), new(1, -1), new(0, 1), new(-1, 2), new(2, -2), new(1, 1)];
    private static readonly Hex WalkFrom = new(-3, 0);

    private readonly SquareGrid _build = new(2f, new GridPlacement(new Vector3(-13f, 0.02f, -8f)));
    private readonly SquareGrid _farm = new(0.5f, new GridPlacement(new Vector3(-10f, 0.02f, 3.5f)));
    private readonly SquareGrid _deck = new(1f, new GridPlacement(DeckMiddle - Vector3.Transform(new Vector3(DeckSize / 2f, 0f, DeckSize / 2f), DeckTurn), DeckTurn));
    private readonly HexGrid _field = new(0.8f, HexTop.Pointy, new GridPlacement(new Vector3(8.5f, 0.02f, 0.5f)));
    private IReadOnlyDictionary<Hex, float> _walk = new Dictionary<Hex, float>();
    private Func<Hex, float?>? _walkCost;
    private GridHit? _hover;

    public void Load()
    {
        grids.Add("build", _build, cell => cell.X is >= 0 and < 5 && cell.Z is >= 0 and < 5);
        grids.Add("farm", _farm, cell => cell.X is >= 0 and < 9 && cell.Z is >= 0 and < 9);
        grids.Add("deck", _deck, cell => cell.X is >= 0 and < (int)DeckSize && cell.Z is >= 0 and < (int)DeckSize);
        grids.Add("field", _field, hex => Hex.Distance(hex, default) <= FieldSteps);

        // Shown from the start; the coordinates and the heat wait for L, H or --debug.
        debug.Show(DebugGrids.Outlines);

        _walk = Hex.Reachable(WalkFrom, 6f, hex => Rocks.Contains(hex) || Hex.Distance(hex, default) > FieldSteps ? null : 1f);
        _walkCost = hex => _walk.TryGetValue(hex, out float cost) ? cost : null;

        Scenery();
        world.Spawn(new Camera(new Vector3(0f, 19f, 17f), DeckMiddle, 0.8f));
        world.Spawn(new Sun(new Vector3(-0.5f, 1.3f, 0.8f), new Vector3(3f)), new Shadows(30f, 2));
    }

    public void Update(GameTime time)
    {
        Toggle(Key.G, DebugGrids.Outlines);
        Toggle(Key.L, DebugGrids.Coordinates);
        Toggle(Key.H, Walk);

        _hover = null;
        if (world.ActiveCamera() is { } camera)
        {
            var pointer = input.Mouse.Position == Vector2.Zero ? input.ScreenSize / 2f : input.Mouse.Position;
            _hover = grids.Under(camera.Ray(pointer, input.ScreenSize));
        }
    }

    public void Draw(View view)
    {
        var canvas = view.Debug;
        canvas.Grid(_build, new Cell(2, 2), 2);
        canvas.Grid(_farm, new Cell(4, 4), 4, new Color(0.6f, 1f, 0.5f, 0.6f));
        canvas.Grid(_deck, new Cell(2, 2), 2, new Color(0.6f, 0.85f, 1f, 0.8f));
        canvas.Grid(_field, default, FieldSteps);

        canvas.GridLabels(_build, new Cell(2, 2), 2);
        canvas.GridLabels(_deck, new Cell(2, 2), 2);
        canvas.GridLabels(_field, default, FieldSteps);

        canvas.GridHeat(_field, default, FieldSteps, _walkCost!, new HeatScale(0f, 5f) { Cold = new Color(0.2f, 0.9f, 0.4f) }, Walk);
        canvas.Cells(_field, Rocks, new Color(0.3f, 0.22f, 0.15f));

        HoverCell(canvas);
    }

    private void HoverCell(DebugCanvas canvas)
    {
        if (_hover is not { } hit)
        {
            return;
        }

        if (hit.Grid is HexGrid hexes)
        {
            canvas.Cells(hexes, [hit.Cell<Hex>()], Hover, new DebugLook(Thickness: 3f, OnTop: true));
        }
        else if (hit.Grid is SquareGrid squares)
        {
            canvas.Cells(squares, [hit.Cell<Cell>()], Hover, new DebugLook(Thickness: 3f, OnTop: true));
        }

        canvas.Text(hit.Point + hit.Grid.Placement.Up * 0.3f, hit.Name, Hover);
    }

    private void Toggle(Key key, string channel)
    {
        if (input.Pressed(key))
        {
            debug.Toggle(channel);
        }
    }

    /// <summary>The ground, the deck on its posts and the field's rocks.</summary>
    private void Scenery()
    {
        var ground = content.Mesh(Shapes.Plane(new Vector2(40f, 30f), surface: new Surface { Color = new Vector3(0.35f, 0.45f, 0.3f), Roughness = 0.9f }));
        world.Spawn(Transform.Identity, new Model(ground));

        var deck = content.Mesh(Shapes.Box(new Vector3(DeckSize, 0.25f, DeckSize)));
        world.Spawn(new Transform(DeckMiddle - new Vector3(0f, 0.125f, 0f), DeckTurn, Vector3.One), new Model(deck, new Vector3(0.55f, 0.4f, 0.28f)));
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var corner = DeckMiddle + Vector3.Transform(new Vector3(x, 0f, z) * (DeckSize / 2f - 0.3f), DeckTurn);
            var post = new Transform(new Vector3(corner.X, DeckTop / 2f, corner.Z), DeckTurn, new Vector3(0.25f, DeckTop, 0.25f));
            world.Spawn(post, new Model(ModelRef.Cylinder, new Vector3(0.4f, 0.3f, 0.2f)));
        }

        foreach (var rock in Rocks)
        {
            var at = _field.Center(rock);
            world.Spawn(new Transform(at + new Vector3(0f, 0.2f, 0f), Quaternion.Identity, new Vector3(0.9f, 0.4f, 0.8f)), new Model(ModelRef.Sphere, new Vector3(0.45f, 0.43f, 0.4f)));
        }
    }
}
