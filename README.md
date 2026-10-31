# Super

A C# game engine you use as a library. There's no editor and no scene file format.
You write a normal .NET program, and Super gives you the window, the game loop,
a modern 3D renderer and the building blocks that take time to get right.

> **Status:** Super is early. [Rendition](src/Rendition/README.md), the renderer, works
> today. So do the game loop, the window, `GameRandom`, `HeadlessGame` and `[Seeds]`,
> which the [ColorCube example](examples/ColorCube) shows, and the ECS, capture tools and
> interfaces, which the [Reef example](examples/Reef) shows. Everything else in this
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

`StartWindowsGameLoop` opens the window and runs until it closes. Your game has four
members, and two of them are required:

| Method | When it runs | What it's for |
| --- | --- | --- |
| `Load()` | Once, optional | Build the starting world |
| `Update(GameTime)` | A fixed number of times per second, 30 by default | Change game state |
| `Draw(View)` | Once per displayed frame | Describe what's on screen. It only reads state |
| `Ui` | Once per displayed frame, optional | Describe the [interface](#interfaces) on top. It only reads state |

Keeping `Update` and `Draw` apart is the most important idea in Super. `Update` is the
game. `Draw` only looks at it, so the game can run with no screen at all, and the
display can run faster or slower than the simulation.

## Project layout

| Package | What it gives you | Depends on |
| --- | --- | --- |
| `Super` | Window, game loop, input, views, interfaces, content | `Rendition`, `Super.Random` |
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

## Interfaces

Worlds are flat lists, redrawn every frame. Interfaces are the opposite: they're trees,
and most of a tree stays the same from one frame to the next. So `Super.Ui` works like
SwiftUI. You describe the interface with small records, and Super works out what changed.

```csharp
using Super;
using Super.Ui;

record StatRow(string Label, string Value) : UiView
{
    public override UiView Body => new HStack(Spacing: 24f)
    {
        new Text(Label).Foreground(Color.Hex("#9FD3E0")),
        new Spacer(),
        new Text(Value).MonospacedDigit(),
    };
}

record ColonyPanel(int Workers, int Wood) : UiView
{
    public override UiView Body => new VStack(HorizontalAlignment.Leading, Spacing: 4f)
    {
        new Text("Colony").Bold().FontSize(16f),
        new StatRow("Workers", $"{Workers}"),
        new StatRow("Wood", $"{Wood}"),
    }
    .Padding(12f)
    .Background(Color.Hex("#06202C", 0.72f))
    .FixedSize();
}

class MyColony(Colony colony) : IGame
{
    public UiView? Ui => new ColonyPanel(colony.Workers.Count, colony.Wood)
        .Padding(16f)
        .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    // Update and Draw as usual.
}
```

A component is a record with a `Body`. `ColonyPanel` and `StatRow` are components, and
you use your own exactly like the built-in ones. Super reads `Ui` every frame, so always
describe the whole interface and let Super find the differences.

| View | What it does |
| --- | --- |
| `Text("Hello")` | Text. It wraps at spaces when it's offered too little width |
| `VStack`, `HStack` | Children top to bottom, or left to right |
| `ZStack` | Children on top of each other, the last one in front |
| `Spacer()` | Pushes its neighbors apart along the stack |

Modifiers wrap a view, so their order matters. `.Padding(8f).Background(color)` colors
the padding too, and `.Background(color).Padding(8f)` doesn't.

| Modifier | What it does |
| --- | --- |
| `.Padding(all)`, `.Padding(horizontal, vertical)` | Adds room around the view |
| `.Frame(width, height, maxWidth, maxHeight, alignment)` | Fixes the size, or lets the view stretch |
| `.FixedSize()` | Uses the size the view would like, whatever it's offered |
| `.Background(color)` | Fills the view's area |
| `.Foreground(color)`, `.FontSize(pixels)`, `.Bold()` | Styles text here and everywhere inside |
| `.MonospacedDigit()` | Gives digits one width, so changing numbers don't jiggle |
| `.Id(key)` | Names a view among its siblings |

### Colors

`Color` takes whichever notation you like, and each one takes an optional alpha:

```csharp
Color.Hex("#06202C", 0.72f);        // also #RGB, #RGBA and #RRGGBBAA
Color.Rgb(6, 32, 44);               // 0 to 255
Color.Hsl(198f, 0.76f, 0.1f);       // hue in degrees, the rest 0 to 1
Color.Oklch(0.72f, 0.12f, 210f);    // lightness 0 to 1, chroma 0 to about 0.4, hue in degrees
Color.White with { A = 0.5f };      // change just the opacity
```

Oklch is the best one for building a palette, because colors with the same lightness
look equally bright. A color too vivid for a screen gets clipped.

`Color` is for interfaces. 3D colors and lights stay as `Vector3`, because they're
linear and a bright light can go well past 1.

### Layout

Layout works like SwiftUI's, and does what flexbox does:

1. A parent offers its child some space.
2. The child picks its own size. The parent has to accept the answer.
3. The parent places the child, using its alignment.

Stacks share their space out starting with the least flexible child, and spacers go
last. To make something grow like `flex-grow`, give it `.Frame(maxWidth:
UiExtensions.Infinity)`. To pin something to a corner, wrap it in a frame that fills the
screen, as `MyColony` does above. The top of the tree is centered otherwise.

### State

Most interfaces only show game state. For anything the interface owns itself, such as
which tab is selected, use `UseState`. Super keeps the value for as long as that view
stays in the tree:

```csharp
record Tabs : UiView
{
    public override UiView Body
    {
        get
        {
            var selected = UseState(0);
            return new Text($"Tab {selected.Value}");
        }
    }
}
```

Reading `Value` during `Body` signs that component up for changes, and setting it runs
only the components that read it. Call `UseState` the same number of times and in the
same order on each run. Hand a `State<T>` to a child to let the child use it too.

There's nothing to set it with yet, because the interface can't take input. The rule is
the same as for views: if gameplay cares, it's game state, and it belongs in your own
classes, not in `UseState`.

### How it stays cheap

This is a virtual tree, like React's and SwiftUI's. The records are cheap descriptions
that live for moments. Behind them sits a tree of long-lived nodes, which hold state and
layout. Each frame, the reconciler walks the new description against those nodes:

- A record that equals the last one is skipped, with everything inside it.
- If nothing changed, there's no layout and no drawing work. The panel in Reef only
  redoes its work twice a second, when its numbers change.
- A view keeps its state while it stays in the same place with the same type. A
  different type in that place starts fresh.
- `show ? view : null` keeps its slot when it's null, so what follows doesn't move.
  In a list that changes, use `.Id(item.Id)`, like a `key` in React, so rows keep their
  state as they move.

The default font is [Inter](https://rsms.me/inter/), built into `Super.dll`, so text
works with no files around.

### Showing how the game runs

Ask for `GameStats` in your constructor and read `Latest`, which updates twice a second.
It holds the frame rate, frame time, bytes allocated per frame on the game's thread, heap
size and garbage collections. Work done for the interface isn't counted, so a stats panel
doesn't report itself. Reef's panel is in
[StatsPanel.cs](examples/Reef/StatsPanel.cs).

These numbers come from the real clock, so only show them. If they reach `Update`, a seed
stops repeating.

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
- **`run.ShownText`** lists the text the interface showed and where it landed. Layout
  and fonts are real in a headless run, so a test can check that a label fits.

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
dotnet run --project MyColony -- --seed 42 --ticks 6000 --record day-200.mp4 --seconds 20 --fps 60 --size 1920x1080
dotnet run -c Release --project MyColony -- --seed 42 --ticks 6000 --profile day-200.txt --frames 60
```

`--json` prints a result a script can read:

```json
{ "seed": 42, "ticks": 20000, "passed": true, "events": { "TreeChopped": 318 } }
```

`--screenshot` renders the real picture of that exact moment, so it needs a GPU.

`--record` plays to `--ticks`, then saves `--seconds` of video. `--fps` defaults to 60
and `--size` to the game's window size. It needs a GPU, and
[ffmpeg](https://ffmpeg.org) on the PATH (`winget install Gyan.FFmpeg`). A video runs
as fast as the machine can make it, and the same seed gives the same frames.

`--profile` shows where a game spends its time. See [Profiling](#profiling).

Today `--seed`, `--screenshot`, `--record`, `--profile` and `--size` work, and `--ticks`
works alongside those three. `--headless` and `--json` are still to come.

## Profiling

`--profile` plays to `--ticks`, then measures `--frames` frames (60 by default) and
writes a short text report. The game needs no setup, and game and engine code appear
in the same call stacks. Use a Release build, because Debug timings mean very little.

```
Reef: seed 7, from tick 360, 1280x720, 60 frames

Frame: 13.1 ms on average (76 fps), fastest 1.1, slowest 27.9
  Update       2.47 ms  (30 ticks in all)
  Draw         0.19 ms  (the game's Draw, and the interface)
  Render      10.49 ms  (of which 9.47 ms waiting for the GPU and the display)
  Allocated     792 bytes per frame

Limited by the GPU: CPU busy 3.7 ms, GPU busy 7.0 ms per frame, enough for about 143 fps.

GPU: 6.98 ms per frame
  Shadows               2.62 ms   37%
  G-buffer              2.57 ms   37%
  ...

   Self   Total  Function
 73.8%   79.7%  Rendition.Renderer.Render
 17.8%   18.1%  Reef.Boids.Update
  ...
```

After that come the most common call stacks. To compare before and after a change, use
the same `--seed`, `--ticks` and `--size`, so both runs measure the same moment.

- **Busy times are the numbers to trust.** The frame time includes waiting for the
  display, so it can stay near the screen's refresh rate however light the game is.
- **Waiting counts as time in the function table.** That's why `Renderer.Render` leads
  it above. Look past it to the functions doing work.
- **Native code shows up as the C# call that entered it.** The samples stop at the
  managed code, so time inside the GPU driver lands on the function that called it.
- **A profile run differs a little from a normal one.** It's a hidden window with
  Vulkan validation off, and it asks the GPU for timestamps.

A `.nettrace` file lands next to the report. Open it in
[PerfView](https://github.com/microsoft/perfview) to dig further.

## Screenshots and videos from code

Ask for `GameCapture` in your constructor and call it from anywhere in the game:

```csharp
internal sealed class MyColony(GameCapture capture) : IGame
{
    public void OnRaidStarted()
    {
        capture.Screenshot("raid.png");
        capture.StartRecording("raid.mp4", framesPerSecond: 60);
    }

    public void OnRaidEnded() => capture.StopRecording();

    // Load, Update and Draw as usual.
}
```

- Pictures come straight from the renderer, so other windows and overlays can't end
  up in them.
- `Screenshot` saves the next frame as a PNG. Folders are created for you.
- While recording, each frame moves the game on by exactly one video frame. Instead
  of a stuttering video you get a game that may run slowly until `StopRecording`.
- `StopRecording` waits for the file to be finished. Closing the game finishes it too.
- Resizing the window ends the recording, and the video stops at that point.
- In a headless test there's nothing to capture, so these calls do nothing.

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

The Reef example is a wide underwater view of 6,240 fish schooling over coral, with a
live stats panel in the corner. Its models
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
- **Interactive interfaces.** The interface draws text and colored rectangles. It has no
  input yet, so there are no buttons, and it can't show images.
- **Audio.** `view.Play` in the examples needs an audio package that isn't designed yet.
- **Other platforms.** Windows only for now, with Linux kept possible.

An editor is not planned. Super is a library, and your code and JSON files are the
source of truth.
