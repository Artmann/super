# Super

A C# game engine you use as a library. There's no editor and no scene file format.
You write a normal .NET program, and Super gives you the window, the game loop,
a modern 3D renderer and the building blocks that take time to get right.

> **Status:** Super is early. [Rendition](src/Rendition/README.md), the renderer, works
> today. So do the game loop, the window, `GameRandom`, `HeadlessGame` and `[Seeds]`,
> which the [ColorCube example](examples/ColorCube) shows. Everything else in this
> document describes the engine we're building, and much of its code shows an intended
> API that doesn't exist yet.

- **You own the game.** Your state lives in your own classes. Super never asks you to
  inherit from a base object or fit your game into its world.
- **The screen follows your state.** You describe what the game looks like for a
  given state, and that's all. Loading a save needs no special cases.
- **Use only what you want.** The ECS is an optional add-on, and you can bring your own.
- **Built to be tested.** Your game runs without a window. One test can play it
  1,000 times with different seeds, and any failure can be replayed exactly.
- **Content is JSON.** Items, recipes and monsters live in plain files you can read,
  edit and reload while the game runs.

## A first game

```csharp
using System.Numerics;
using Super;

Game.StartWindowsGameLoop<Spinner>(new GameOptions { Title = "Spinner" });

class Spinner(Content content) : IGame
{
    readonly ModelRef cube = content.Model("models/cube");
    float angle;

    public void Update(GameTime time) => angle += time.TickSeconds;

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0, 2, 5), lookAt: Vector3.Zero);
        view.Sun(direction: new Vector3(1, 2, 1), color: new Vector3(3f));
        view.Model(key: 1, cube, Matrix4x4.CreateRotationY(angle));
    }
}
```

`StartWindowsGameLoop` opens the window and runs until it closes. Your game has three
methods, and two of them are required:

| Method | When it runs | What it's for |
| --- | --- | --- |
| `Load()` | Once, optional | Build the starting world |
| `Update(GameTime)` | A fixed number of times per second, 30 by default | Change game state |
| `Draw(View)` | Once per displayed frame | Describe what's on screen. It only reads state |

Keeping `Update` and `Draw` apart is the most important idea in Super. `Update` is the
game. `Draw` only looks at it, so the game can run with no screen at all, and the
display can run faster or slower than the simulation.

## Project layout

| Package | What it gives you | Depends on |
| --- | --- | --- |
| `Super` | Window, game loop, input, views, content | `Rendition`, `Super.Random` |
| `Super.Random` | Seeded random numbers. Usable alone | Nothing else here |
| `Super.Ecs` | An optional entity component system | `Super` |
| `Super.Testing` | Headless runs, seeded tests and run logs | `Super` |
| `Rendition` | The renderer. Usable alone, with no engine | Nothing else here |

## Where your code goes

Game logic is plain C#. Split it into classes however you like, ask for them in your
constructor, and call them in the order you want. A "system" is simply a class with
an update method, and there's no hidden scheduler deciding what runs first.

```csharp
Game.StartWindowsGameLoop<MyColony>(new GameOptions { Title = "Colony" }, services =>
{
    services.AddSingleton<Colony>();
    services.AddSingleton<Pathfinder>();
    services.AddSingleton<Woodcutting>();
    services.AddSingleton<IWeather, SeasonalWeather>();
});

class MyColony(Colony colony, Woodcutting woodcutting, IWeather weather, ColonyViews views) : IGame
{
    public void Update(GameTime time)
    {
        weather.Update(time);
        woodcutting.Update(time);
    }

    public void Draw(View view) => views.Draw(colony, view);
}
```

```csharp
class Woodcutting(Colony colony, Pathfinder paths, GameRandom random, EventLog log)
{
    readonly RandomStream rolls = random.Stream("woodcutting");

    public void Update(GameTime time)
    {
        foreach (var worker in colony.Workers)
        {
            if (!worker.AtTree)
            {
                paths.Step(worker);
                continue;
            }

            worker.Tree.Health -= rolls.Between(1, 4);
            if (worker.Tree.Health > 0)
            {
                continue;
            }

            colony.Remove(worker.Tree);
            log.Record(new TreeChopped(worker.Id, time.Tick));
        }
    }
}
```

Dependency injection is the standard `Microsoft.Extensions.DependencyInjection`
container, so it works just like ASP.NET. `GameTime`, `Content`, `GameRandom`, `Input`,
`EventLog` and logging are already registered. Everything is wired up at startup, and
nothing is looked up while the game runs. Super keeps no global state, so several
games can run side by side in one process.

## Drawing: views

A view is a function that takes some data and says how it looks. If you've used
SwiftUI or React, this will feel familiar. You don't create scene objects and keep
them in sync with your game. Each frame, you describe what's visible.

```csharp
struct Box
{
    public int Id;
    public Matrix4x4 Transform;
    public bool IsOpen;
}

class BoxView(Content content)
{
    readonly ModelRef box = content.Model("models/chest");
    readonly AnimationRef opening = content.Animation("models/chest#open");
    readonly SoundRef creak = content.Sound("sounds/creak");

    public void Draw(in Box data, View view)
    {
        float lid = view.Animate(data.Id, data.IsOpen ? 1f : 0f, seconds: 0.4f);
        view.Model(data.Id, box, data.Transform, opening.At(lid));

        if (view.Changed(data.Id, data.IsOpen))
        {
            view.Play(creak, data.Transform.Translation);
        }
    }
}
```

`IsOpen` is game state. `lid` is view memory, which Super keeps for you under the key
you pass. The first time Super sees a key, the value goes straight to its target. So
when a player loads a save, open boxes are simply open, with no animation and no creak.
When `IsOpen` changes during play, the lid swings.

Worlds in Super are flat: a list of things you draw, not a tree of nodes. To attach one
thing to another, multiply their transforms, or ask for a bone on the parent's pose:

```csharp
var hand = view.Bone(colonist.Id, "hand_r");
view.Model(colonist.AxeId, axe, hand);
```

### Keys

A key names something across frames, like a `key` in React. Super uses it for view
memory, and to tell the renderer where an object was last frame, which keeps moving
things crisp. Use a stable ID from your game, and the same one every frame.

### Things that rarely change

A city might have 50,000 walls. Describing them all 60 times a second is wasteful, so
wrap them in `Cached`. It runs your function again only when the version changes:

```csharp
view.Cached(chunk.Version, chunk, static (chunk, view) =>
{
    foreach (ref readonly var wall in chunk.Walls)
    {
        view.Model(wall.Id, wall.Model, wall.Transform);
    }
});
```

### State or view?

If gameplay cares, it's state. If only the eyes care, it's view.

| Example | Where it lives | Why |
| --- | --- | --- |
| Lid of a box | View | Gameplay only needs open or closed |
| Door you can't pass until it's half open | State: an `OpenAmount` your `Update` moves | Gameplay depends on it |
| Ball that can hit things | State: position and velocity | It carries on mid-bounce after a load |
| Dust from a falling tree | View | Nothing depends on it |

Things that move in `Update` advance in steps, so the view blends between the last two
ticks to keep motion smooth at any frame rate:

```csharp
view.Model(ball.Id, ballModel, view.Smooth(ball.PreviousTransform, ball.Transform));
```

### Particles

Particles are worked out from when they started rather than fired and forgotten, so
pausing and loading are always correct:

```csharp
if (tree.FellAt is { } fellAt)
{
    view.Particles(tree.Id, dust, tree.Position, age: view.SecondsSince(fellAt));
}
```

### Materials and the look of the picture

Materials can be JSON content or code. You can also write your own
[surface shader](docs/design.md#custom-shaders) to decide what a surface is made of,
and Rendition's lighting, shadows and reflections treat it like any other surface.

```csharp
view.Sky(content.Environment("environments/overcast"));
view.Post(new PostSettings { Exposure = 0.2f, Vignette = 0.3f, Sharpen = 0.4f });
```

## Content: JSON files

Put JSON in your project's `content` folder. Describe its shape with a C# record, then
load it. Comments and trailing commas are allowed.

```json
// content/items/axe.json
{
  "id": "axe",
  "name": "Iron Axe",
  "chopPower": 3,
  "model": "models/axe",
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

- **Mistakes are caught early.** Bad data stops loading and names the place, such as
  `items/axe.json:4: chopPower must be a number`. Broken references and unknown fields
  are reported too.
- **Your editor can help.** Super generates a schema from your records, which gives you
  autocomplete and red squiggles in VS Code.
- **Change it while the game runs.** During development, saving a file updates the
  running game. Live reload is off in headless runs and shipped builds, so it can't
  change their results.
- **Mods come almost free.** A mod is a folder of JSON that adds or replaces entries.

Everything loads at startup or between levels, and after that it's just C# objects.

Levels and saved games can be JSON too, which is handy early on because you can read
them and fix them by hand. Keep your state in plain objects, so the format can change:

```csharp
saves.Write("slot-1", colony);
var colony = saves.Read<Colony>("slot-1");
```

Expect a more compact format to replace JSON for saves as games grow.

## Random numbers

`GameRandom` is the only source of randomness your game needs. It takes one seed and
gives you named streams:

```csharp
class Weather(GameRandom random)
{
    readonly RandomStream rolls = random.Stream("weather");

    public bool StormToday() => rolls.Chance(0.05f);
}
```

Each stream has its own sequence, so adding a dice roll to combat won't change the
weather. To replay a game exactly, use the same seed.

Streams have the things games reach for:

| Method | What you get |
| --- | --- |
| `Between(1, 6)` | A whole number, including both ends, so this is a die |
| `Between(0.5f, 2f)` | A decimal number in that range |
| `Float()` | A number from 0 up to, but not including, 1 |
| `Chance(0.05f)` | True 5% of the time |
| `Bool()`, `Sign()` | A coin flip, or either -1 or 1 |
| `Pick(items)`, `Shuffle(items)` | A random item, or a random order |
| `PickWeighted(items, item => item.Weight)` | A random item, where bigger weights come up more |
| `Normal(mean, deviation)` | A bell curve, for values that vary but rarely go wild |
| `Angle()`, `Direction2D()`, `Direction3D()` | A random way to face |
| `InsideCircle(radius)`, `InsideSphere(radius)` | A random point in that shape |

## Testing

Reference `Super.Testing` from a normal test project. Your game runs at full speed with
no window and no GPU, through the same `Update` and `Draw` code.

```csharp
public class ColonyTests
{
    [Theory, Seeds(1000)]
    public void Colony_survives_a_year(int seed)
    {
        using var run = HeadlessGame.Create<MyColony>(seed, services =>
            services.AddSingleton<IWeather, EndlessWinter>());

        run.Invariant("nobody is inside a wall", game =>
            game.Colony.Workers.All(worker => !game.Map.IsWall(worker.Cell)));

        run.Ticks(20_000);

        Assert.True(run.Log.Contains<TreeChopped>());
        Assert.Contains(run.Drawn, draw => draw.Key == run.Game.Colony.Hall.Id);
    }
}
```

That test plays the game 1,000 times, once per seed. Runs share a process and go in
parallel, so this fits in a pull request check, and any crash fails the build.

- **Run logs** are how you prove what happened. Record an event with
  `log.Record(new TreeChopped(...))`, then check it in a test.
- **Invariants** run every tick, so they catch a problem when it happens, not at the
  end of the run.
- **Fakes** take one line. Swap any service, as with `IWeather` above.
- **`run.Input`** plays scripted key presses and clicks.
- **`run.Drawn`** lists what `Draw` would have shown.

A failure tells you how to get it back:

```
Invariant failed: nobody is inside a wall
  seed 417, tick 8,932
  last events:
    8,930  WallBuilt { Cell = (14, 9) }
    8,931  WorkerMoved { Id = 12, Cell = (14, 9) }
  replay: dotnet run --project MyColony -- --seed 417 --ticks 8932
```

Runs repeat exactly on the same build and the same kind of CPU. Different CPU types can
round decimal numbers differently, so don't compare runs across them. The guarantee
depends on two rules:

- Take time from `GameTime` and randomness from `GameRandom`, never from the system
  clock or `System.Random`.
- `Draw` only reads state. View memory is never part of the simulation, and it's
  skipped in headless runs unless a test asks for it.

## Running from the command line

Every Super game understands the same options, which suits scripts, CI and coding
agents. None of these show a window.

```
dotnet run --project MyColony -- --headless --seed 42 --ticks 20000 --json
dotnet run --project MyColony -- --seed 42 --ticks 6000 --screenshot day-200.png
```

`--json` prints a result a script can read:

```json
{ "seed": 42, "ticks": 20000, "passed": true, "events": { "TreeChopped": 318 } }
```

`--screenshot` renders the real picture of that exact moment, so it needs a GPU.

Today `--seed` and `--screenshot` work, and `--ticks` works alongside `--screenshot`.
`--headless` and `--json` are still to come.

## Using an ECS

ECS suits games with thousands of similar things, like a colony sim's workers, animals
and items. It's a separate package, and it plugs in as ordinary services:

```csharp
services.AddSuperEcs();
services.AddSingleton<Movement>();

public record struct Position(Vector3 Value);
public record struct Velocity(Vector3 Value);

class Movement(World world)
{
    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Position, Velocity>())
        {
            row.First.Value += row.Second.Value * time.TickSeconds;
        }
    }
}

class Sheep(World world, Content content)
{
    readonly ModelRef model = content.Model("sheep.rmodel");

    public void Draw(View view)
    {
        foreach (var row in world.Query<Position>())
        {
            view.Model(row.Entity.Id, model, Matrix4x4.CreateTranslation(row.First.Value));
        }
    }
}
```

Each row's `First`, `Second` and so on are references into the world, so changing them
changes the entity, and a query allocates nothing. Queries take up to four components.

Views work the same either way. You can also skip this package and use plain lists,
or a different ECS library. The [Reef example](examples/Reef) uses it for thousands of
schooling fish.

## Getting started

You'll need:

- Windows and a GPU with Vulkan 1.3 support
- .NET 10 SDK
- Vulkan SDK, to compile shaders: `winget install KhronosGroup.VulkanSDK`

Super isn't on NuGet yet. To work on it, clone this repository and build the solution:

```
dotnet build Super.slnx -c Release
```

To run the tests, which need no window or GPU:

```
dotnet test Super.slnx -c Release
```

To see a game running, try the example, a spinning cube that changes to a random color
at random times:

```
dotnet run -c Release --project examples/ColorCube
```

The Reef example is a wide underwater view of 6,240 fish schooling over coral. Its models
aren't in this repository because they aren't covered by the license and have their own
terms ([CREDITS.md](CREDITS.md)). With them in `assets/source/reef`, cook each one, then run it:

```
dotnet run -c Release --project tools/Rendition.Import -- assets/source/reef/tuna/TUNAFBX.fbx assets/reef-tuna.materials.json assets/cooked/reef/tuna.rmodel
dotnet run -c Release --project tools/Rendition.Import -- assets/source/reef/tilapia/tillapia_buttikoferi_fish.glb assets/reef-tilapia.materials.json assets/cooked/reef/tilapia.rmodel
dotnet run -c Release --project tools/Rendition.Import -- assets/source/reef/coral/coral.glb assets/reef-coral.materials.json assets/cooked/reef/coral.rmodel
dotnet run -c Release --project tools/Rendition.Import -- assets/source/reef/softcoral/Corals.fbx assets/reef-softcoral.materials.json assets/cooked/reef/softcoral.rmodel
dotnet run -c Release --project examples/Reef
```

To see what the renderer can do today, run the sample:

```
dotnet run -c Release --project src/Rendition.Sample
```

Models are cooked into a fast format by the import tool. The
[Rendition README](src/Rendition/README.md) covers that and the current feature list.

## What's not here yet

- **Animation and particles.** The API above is the plan, but Rendition can't play
  skeletons or draw particles yet.
- **Large numbers of objects.** Rendition's efficiency with many thousands of objects
  still has to be measured and improved.
- **Physics.** Not in the first version. Gameplay physics has to give repeatable
  results, so the engine needs choosing carefully. Until then, write your own movement
  code in `Update`, as in the ball example.
- **Audio.** `view.Play` in the examples needs an audio package that isn't designed yet.
- **Other platforms.** Windows only for now, with Linux kept possible.

An editor is not planned. Super is a library, and your code and JSON files are the
source of truth.
