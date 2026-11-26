using System.Numerics;
using Super;

namespace PlotsExample;

/// <summary>
/// Grids of every kind side by side: a 2 m building grid over a mound and a 0.5 m farm grid on the ground, a 1 m grid on
/// a deck that is raised and turned, and a hex field with rocks. The mouse picks a cell on whichever grid it's over, or
/// the screen's middle does until the mouse moves. The grids are drawn for the player, with how far a walker from the
/// field's corner gets filled in, and O shows and hides them. G, L and H show and hide the debug outlines, the
/// coordinates and the walker's heat map, as <c>--debug grid,grid.labels,grid.heat:walk</c> does.
/// </summary>
public sealed class PlotsGame(World world, Content content, Input input, Grids grids, DebugDraw debug) : IGame
{
    private const string Walk = "grid.heat:walk";
    private const float DeckSize = 5f;
    private const int FieldSteps = 3;

    private static readonly Color Hover = new(1f, 0.85f, 0.2f);
    private static readonly Vector3 DeckMiddle = new(0f, 2.2f, -1.5f);
    private static readonly Quaternion DeckTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.45f);
    private static readonly Hex[] Rocks = [new(0, 0), new(1, -1), new(0, 1), new(-1, 2), new(2, -2), new(1, 1)];
    private static readonly Hex WalkFrom = new(-3, 0);

    private readonly SquareGrid _build = new(2f, new GridPlacement(new Vector3(-13f, 0.02f, -8f)));
    private readonly SquareGrid _farm = new(0.5f, new GridPlacement(new Vector3(-10f, 0.02f, 3.5f)));
    private readonly SquareGrid _deck = new(1f, new GridPlacement(DeckMiddle - Vector3.Transform(new Vector3(DeckSize / 2f, 0f, DeckSize / 2f), DeckTurn), DeckTurn));
    private readonly HexGrid _field = new(0.8f, HexTop.Pointy, new GridPlacement(new Vector3(8.5f, 0.02f, 0.5f)));
    private Hex[] _walkable = [];
    private Func<Hex, float?>? _walkCost;
    private GridHit? _hover;
    private bool _overlays = true;

    public void Load()
    {
        grids.Add("build", _build, cell => cell.X is >= 0 and < 5 && cell.Z is >= 0 and < 5);
        grids.Add("farm", _farm, cell => cell.X is >= 0 and < 9 && cell.Z is >= 0 and < 9);
        grids.Add("deck", _deck, cell => cell.X is >= 0 and < (int)DeckSize && cell.Z is >= 0 and < (int)DeckSize);
        grids.Add("field", _field, hex => Hex.Distance(hex, default) <= FieldSteps);

        var walk = Hex.Reachable(WalkFrom, 6f, hex => Rocks.Contains(hex) || Hex.Distance(hex, default) > FieldSteps ? null : 1f);
        _walkable = [.. walk.Keys];
        _walkCost = hex => walk.TryGetValue(hex, out float cost) ? cost : null;

        PlotsScenery.Spawn(world, content, (DeckMiddle, DeckTurn, DeckSize), _field, Rocks);
        world.Spawn(new Camera(new Vector3(0f, 19f, 17f), DeckMiddle, 0.8f));
        world.Spawn(new Sun(new Vector3(-0.5f, 1.3f, 0.8f), new Vector3(3f)), new Shadows(30f, 2));
    }

    public void Update(GameTime time)
    {
        Toggle(Key.G, DebugGrids.Outlines);
        Toggle(Key.L, DebugGrids.Coordinates);
        Toggle(Key.H, Walk);
        if (input.Pressed(Key.O))
        {
            _overlays = !_overlays;
        }

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

        if (_overlays)
        {
            PlayerOverlays(view.Overlays);
        }

        HoverCell(view);
    }

    /// <summary>The grids as a player sees them: lines that fade out round the mouse, and where the walker can go.</summary>
    private void PlayerOverlays(ViewOverlays overlays)
    {
        // The building grid shows strongest round the mouse while it's over that grid.
        var lines = new GridLines(new Color(1f, 1f, 1f, 0.75f));
        var focus = _hover is { Name: "build" } hit ? hit.Point : (Vector3?)null;
        overlays.Grid(_build, new Cell(2, 2), 2, lines with { Focus = focus, FocusRadius = 7f });
        overlays.Grid(_farm, new Cell(4, 4), 4, new GridLines(new Color(0.85f, 1f, 0.7f, 0.6f)) { Width = 0.025f });
        overlays.Grid(_deck, new Cell(2, 2), 2, new GridLines(new Color(0.7f, 0.9f, 1f, 0.8f)) { Depth = 0.3f });
        overlays.Grid(_field, default, FieldSteps, lines);
        overlays.Cells(_field, _walkable, new CellFill(new Color(0.3f, 0.8f, 1f, 0.3f)));
    }

    private void HoverCell(View view)
    {
        if (_hover is not { } hit)
        {
            return;
        }

        var canvas = view.Debug;
        var fill = new CellFill(Hover with { A = 0.35f }) { Depth = 0.5f };
        if (hit.Grid is HexGrid hexes)
        {
            Hovered(view, hexes, hit.Cell<Hex>(), fill);
        }
        else if (hit.Grid is SquareGrid squares)
        {
            Hovered(view, squares, hit.Cell<Cell>(), fill);
        }

        canvas.Text(hit.Point + hit.Grid.Placement.Up * 0.3f, hit.Name, Hover);
    }

    /// <summary>Fills the hovered cell for the player, or outlines it in debug lines when the overlays are hidden.</summary>
    private void Hovered<TCell>(View view, IGrid<TCell> grid, TCell cell, in CellFill fill)
        where TCell : struct, IGridCell<TCell>
    {
        if (_overlays)
        {
            view.Overlays.Cell(grid, cell, fill);
        }
        else
        {
            view.Debug.Cells(grid, [cell], Hover, new DebugLook(Thickness: 3f, OnTop: true));
        }
    }

    private void Toggle(Key key, string channel)
    {
        if (input.Pressed(key))
        {
            debug.Toggle(channel);
        }
    }
}
