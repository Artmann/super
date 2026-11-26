# Grids

Colony builders, city builders, dungeon crawlers and tactics games are built on grids.
Super has square and hex grids, with the maths every grid game needs. You can turn a
world position into a cell and pick a cell with the mouse. You can also find
neighbours, distances, lines, ranges and rings, and turn cells about each other. None
of it needs the renderer, so it runs the same in a headless test.

```csharp
var build = new SquareGrid(2f);                          // 2 m cells on level ground
var map = new HexGrid(1f, HexTop.Pointy);                // hexes 1 m from middle to corner

Cell under = build.CellAt(input.Mouse.Ray(camera)) ?? default;
Vector3 middle = build.Center(under);

foreach (Hex hex in Hex.Range(map.HexAt(player.Position), 2))
{
    // the 19 hexes within two steps of the player
}
```

A game can have as many grids as it needs. Each one has its own size and placement, so
a 2 m building grid can sit next to a 0.5 m farm grid, or each floor of a building can
have a grid at its own height. Going from one grid to another goes through the world:
take a cell's `Center` on one grid and find the cell at that point on the other.

## Cells and hexes

A `Cell` is a square on a `SquareGrid`. `X` is its column along the grid's X and `Z` is
its row along the grid's Z. Cell 0, 0 runs from the grid's origin to one cell size
along both, so its middle is half a cell in.

A `Hex` is a hexagon on a `HexGrid`, in axial coordinates, `Q` and `R`. The third cube
coordinate, `S`, is always `-Q - R`. These are the coordinates of the
[Red Blob Games hexagon guide](https://www.redblobgames.com/grids/hexagons/), which is
worth reading before writing hex code of your own. Hex 0, 0 has its middle at the
grid's origin.

Both are small structs that compare and hash by value, so they work as dictionary keys
and in sets. You can add and subtract them, so `cell + new Cell(1, 0)` is the cell
next along X.

`HexTop.Pointy`, the default, has a corner pointing along the grid's Z, so rows of
hexes run along X. `HexTop.Flat` has a side facing along Z, so columns of hexes run
along Z.

## From the world to a cell and back

| Square | Hex | What it gives |
| --- | --- | --- |
| `CellAt(position)` | `HexAt(position)` | The cell a world position lies over or under |
| `CellAt(ray)` | `HexAt(ray)` | The cell a ray meets the grid in, or null when it misses |
| `Center(cell)` | `Center(hex)` | The world position of a cell's middle, on the grid |
| `Corners(cell, span)` | `Corners(hex, span)` | The 4 or 6 corners in the world |

To pick with the mouse, pass `input.Mouse.Ray(camera)`. The answer is null when the ray
runs alongside the grid or points away from it. `Corners` writes into a span you give
it, so it doesn't allocate:

```csharp
Span<Vector3> corners = stackalloc Vector3[6];
map.Corners(hex, corners);
```

## Placing a grid

A grid lies flat on its own X and Z, on its `GridPlacement`: the point its origin is at
and how it is turned. Left out, the grid lies on level ground at the world's origin.

```csharp
var deck = new SquareGrid(1f, new GridPlacement(new Vector3(0f, 3f, 0f), shipRotation));
```

A grid can be raised, turned, tilted or stood up like a wall. `CellAt` and `HexAt` look
straight along the grid's own up, so a point above the grid finds the cell under it.
To make a grid follow something that moves, such as a ship's deck or a floating
island, set its `Placement` again each frame. The cells stay the same. Only where they
are in the world changes.

## Neighbours and distance

`Cell.Sides` holds the four steps to the cells that share a side, and `Cell.Around`
holds the eight that share a side or a corner. `Hex.Sides` holds a hex's six. Add one to
a cell to step there:

```csharp
foreach (Cell step in Cell.Sides)
{
    Cell next = here + step;
}
```

There are three ways to count the steps between two cells:

- `Cell.Manhattan(a, b)` counts the steps when you only move across sides, like a rook.
- `Cell.Chebyshev(a, b)` counts corners as steps too, like a king.
- `Hex.Distance(a, b)` counts the steps across hex sides.

## Lines, ranges, rings and spirals

`Line(from, to)` gives the cells a straight line from one middle to the other passes
nearest, from end to end. Each step is a neighbour, so a line has one more cell than
the distance. Use it for line of sight or to aim a shot along a row.

`Range(center, steps)` gives every cell within that many steps. For hexes it is a big
hexagon of 3n(n+1)+1 hexes. For squares it is a square, counting king's steps.
`Ring(center, steps)` gives only the cells exactly that far: 6n hexes or 8n squares,
going round in order. `Spiral(center, steps)` gives the same cells as `Range`, ring by
ring outwards from the centre. That is handy for finding the nearest free spot.

These return `IEnumerable`, so `foreach` over them, or call `.ToList()` to keep them.

## Turning

`Cell.Rotate(cell, around, quarterTurns)` turns a cell about another, and
`Hex.Rotate(hex, around, sixthTurns)` does the same in sixths of a turn. They turn the
same way as `Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle)` with a positive
angle, so a building's cells and its model turn together. Negative turns go the other
way, and four quarter turns or six sixth turns come back to where they started.

## What's not here yet

Some parts of issue #31 aren't built yet:

- Footprints for things that cover more than one cell, with a check that one fits.
- `GridMap` storage, a registry of named grids, and stacked grids for floors.
- Offset coordinates for hexes.
- Flood fills for the cells a budget can reach.
- Drawing the grid on screen, which is issue #33.
