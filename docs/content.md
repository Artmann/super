# JSON content

Put JSON in your project's `content` folder. Describe its shape with a C# record, then
load it. Comments and trailing commas are allowed.

```json
// content/items/axe.json
{
  "id": "axe",
  "name": "Iron Axe",
  "chopPower": 3,
  "model": "models/axe.rmodel",
}
```

```csharp
public record Item(string Id, string Name, int ChopPower, ModelRef Model);

class Armory(Content content)
{
    public Catalog<Item> Items { get; } = content.LoadAll<Item>("items");

    public Item StartingTool => Items["axe"];
}
```

Each file is one record, and every record needs a string `Id`. Fields are written in
camelCase. A field with a default value in the record is optional, and the rest are
required. A `ModelRef` is written as its path inside `assets/cooked`.

A `Curve` is written as keys or as an easing, so a designer can shape motion in a file
and tune it while the game runs:

```json
// content/motions/rise.json
{ "id": "rise", "seconds": 0.8, "curve": { "keys": [{ "time": 0, "value": 0 }, { "time": 0.6, "value": 1.15 }, { "time": 1, "value": 1 }] } }
```

```json
// content/motions/drop.json
{ "id": "drop", "seconds": 0.5, "curve": { "easing": "bounceOut" } }
```

Keys out of order, or both keys and an easing, are reported like any other mistake.
See [Tweens and curves](tweens.md#curves-in-content).

A `Vector3` is written as a list of three numbers, such as `[1, 0, 2.5]`. A `Spline` is
written as points or as Bézier knots, so routes, rails and tracks can live in files:

```json
// content/routes/harbour.json
{ "id": "harbour", "start": [0, 0, 0], "path": { "points": [[0, 0, 0], [12, 0, 4], [20, 1, 0]], "closed": true } }
```

See [Splines](splines.md#splines-in-content).

## References between catalogs

To point at a record in another catalog, use `Ref<T>`:

```json
// content/recipes/axe.json
{ "id": "axe", "makes": "axe", "needs": ["stick", "stone"] }
```

```csharp
public record Recipe(string Id, Ref<Item> Makes, Ref<Item>[] Needs);

var made = items[recipe.Makes];
```

A reference to an id that doesn't exist is reported when loading, like any other mistake.
Records can also nest other records and lists, as the recipes in the
[Crafting example](../examples/Crafting/content/recipes) do.

## Mistakes are caught early

Bad data stops loading with a `ContentException` that lists every problem in every file:

```
items/axe.json:4: chopPower must be a number
recipes/axe.json:3: makes points at axes, but there is no Item called that
```

Unknown fields, missing fields, repeated ids and broken references are all reported.
Model paths are checked when `assets/cooked` exists, since cooked assets aren't in git.

## Your editor can help

Run the game from source and Super writes a JSON schema for each catalog into
`content/.schemas`, which gives you autocomplete and red squiggles in VS Code. Point a
file at its schema with a `$schema` field, which loading ignores:

```json
{ "$schema": "../.schemas/items.schema.json", "id": "axe" }
```

Or map a whole folder once, in your VS Code settings:

```json
"json.schemas": [
  { "fileMatch": ["**/content/items/**/*.json"], "url": "./content/.schemas/items.schema.json" }
]
```

The schemas help coding agents too: they say exactly which fields a record takes.

## Change it while the game runs

Run the game from source and saving a file updates it at the start of the next tick. If
the file has a mistake, the error is printed and the old data stays, so a typo won't end
your session. Live reload is off in headless runs, `--screenshot`, `--record`,
`--profile` and shipped games, so it can't change their results.

A catalog stays the same object through a reload, so anything holding it sees the new
data. If you copied something out of a record, refresh it:

```csharp
items.OnChange(() => _axePower = items["axe"].ChopPower);
```

Files load when you call `LoadAll`, at startup or between levels, and after that they're
just C# objects. Going through a catalog with `foreach` is in file path order and doesn't
allocate.

## Images

Images for the interface are named by their path inside the content folder:

```csharp
GameImage icon = content.Image("icons/axe.png");
```

A missing file, or one that isn't an image, stops with a `ContentException`.

## Shipping

To ship a game, publish the content folder beside the program. A game that finds
`content` there knows it's shipped:

```xml
<Content Include="content\**\*.json" Exclude="content\.schemas\**" CopyToPublishDirectory="PreserveNewest" />
```

## In tests

A test can state its data next to its checks, or link the game's real files into its
output folder, as `tests/Rooms.Tests` does:

```csharp
var files = new MemoryContent { ["items/axe.json"] = axeJson };
using var run = HeadlessGame.Create<MyGame>(seed, services => services.AddContentFiles(files));

// Acts as if a file was saved, to test what your game does on a reload.
files["items/axe.json"] = strongerAxeJson;
run.ContentChanged();
run.Ticks(1);
```

## Planned

Levels and saved games will be JSON too, which is handy early on because you can read
them and fix them by hand. Keep your state in plain objects, so the format can change.

```csharp
// Planned: not in Super yet.
saves.Write("slot-1", colony);
var colony = saves.Read<Colony>("slot-1");
```

Mods should be folders of JSON that add or replace entries by id, but only one content
folder can be read so far.
