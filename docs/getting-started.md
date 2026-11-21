# Getting started

## Requirements

- Windows and a GPU with Vulkan 1.3 support, or a Mac with Apple silicon (see [On a Mac](#on-a-mac))
- .NET 10 SDK
- Vulkan SDK, to compile shaders: `winget install KhronosGroup.VulkanSDK`, or on a Mac
  `brew install shaderc`. A game that has no shaders of its own doesn't need it. Building
  this repository does.

## Start a game

Super is one NuGet package, which holds the engine, the Rendition renderer, the ECS,
physics and the test helpers:

```
dotnet new console -n MyGame
cd MyGame
dotnet add package Super
```

Put [a first game](#a-first-game) in `Program.cs` and start it with `dotnet run -c Release`.
The package sets up the build too, with nothing to add to the project file. A `content`
folder beside the project is [cooked by `dotnet build`](command-line.md#in-the-build), and
shaders in a `Shaders` folder are [compiled](rendering.md#custom-shaders). A test project
that references the game gets the engine, the cooked content and the shaders through it.

The package runs on Windows and on Macs with Apple silicon, and it brings xUnit and the C#
compiler with it, for the test helpers and for material graphs.

### On a Mac

A Mac has no Vulkan of its own, so the package brings [MoltenVK](https://github.com/KhronosGroup/MoltenVK),
which runs Vulkan on top of Metal, and a game runs with nothing else installed. Only a few
things differ from Windows:

- **Validation.** [Screenshots](command-line.md#screenshots) and videos check every frame with
  Vulkan's validation layer, which needs a Vulkan loader. Install them with
  `brew install vulkan-loader molten-vk vulkan-validationlayers`. The engine finds the loader in
  Homebrew's folder, or in the LunarG SDK's when `VULKAN_SDK` is set, and then uses it instead of
  its own MoltenVK. No environment variables are needed.
- **No ray tracing.** MoltenVK has no ray queries, so light probes are drawn instead of traced,
  as with `--no-trace`, [`--bake`](command-line.md#baking-lighting) and `--debug-view trace`
  can't run, and a game draws with the bakes made on a GPU that can trace.
- **TAA, not FSR.** AMD's FSR library is built for Windows only, so `--upscaler fsr` falls back
  to TAA.
- **955 textures.** MoltenVK allows fewer descriptors in one set, so the renderer's table holds
  955 textures there instead of 1024.
- **Retina screens.** A window has twice the pixels on a Retina screen, so the
  [interface](interfaces.md#scale) is drawn twice as big to look the same. Pictures and videos
  have exactly the pixels asked for with `--size`.
- **Cmd for Ctrl.** A drag sees Cmd as Control, since Ctrl with a click means a right-click on a Mac.

## Work on the engine

Clone the repository, build and test it:

```
dotnet build Super.slnx -c Release
dotnet test Super.slnx -c Release
```

The tests need no window or GPU. GitHub Actions runs the same build and tests on every pull
request and on each push to `main`, on Windows and on an Apple silicon Mac (`.github/workflows/ci.yml`). The recording tests
there need `ffmpeg`, as they do locally. CI keeps the cook cache between runs, so it only cooks
content that changed, and only saves the cache when the cook added to it. Cooking the Courtyard
from scratch takes about six minutes there, which happens when the cook's code or the .NET SDK
changes, and once a month. A test that runs for more than 5 minutes there is taken to have hung:
it's stopped, and the run fails with the test's name.

### Working on the engine on a Mac

Install the shader compiler, and the Vulkan loader and validation layer that screenshots and
the GPU tests need, with Homebrew:

```
brew install shaderc vulkan-loader molten-vk vulkan-validationlayers ffmpeg
```

The GPU tests (`dotnet test tests/Rendition.Gpu.Tests -c Release`) run there too, on a surface
with no window, since macOS only lets the main thread open windows. The ones that need ray
tracing, or a GPU that counts the shaders each pass runs, fail there, as they do on any GPU
without those, saying which they need. The picture matches
Windows closely, apart from the [differences above](#on-a-mac), so compare shots made on the
same machine.

To see games running, try the examples:

```
dotnet run -c Release --project examples/ColorCube
dotnet run -c Release --project examples/Rooms
dotnet run -c Release --project examples/Crafting
dotnet run -c Release --project examples/Terrain
dotnet run -c Release --project examples/Forest
```

The Terrain example is endless grassy hills at sunset that you fly over with WASD, the mouse, Space and
Shift, with a road a cart drives round; press F for a flyover that follows the cart. The Forest example is a rainy forest with music and a builder hammering at a cabin; try
Space and the keys 1 to 4 for the sound. The Reef example needs models that aren't in the repository; see
[Rendering](rendering.md#models) for how to cook them.

Start one with `dotnet watch --project examples/Crafting --non-interactive` instead, and
code you save is applied while the game runs. See
[Change code while the game runs](hot-reload.md).

## A first game

```csharp
using System.Numerics;
using Super;

Game.Run<Spinner>(new GameOptions { Title = "Spinner" });

class Spinner(World world) : IGame
{
    Entity cube;

    public void Load()
    {
        world.Spawn(new Camera(Position: new Vector3(0, 2, 5), Target: Vector3.Zero));
        world.Spawn(new Sun(Direction: new Vector3(1, 2, 1), Color: new Vector3(3f)));
        cube = world.Spawn(new Model(ModelRef.Cube));
    }

    public void Update(GameTime time)
    {
        ref var transform = ref world.Get<Transform>(cube);
        transform.Rotation *= Quaternion.CreateFromAxisAngle(Vector3.UnitY, time.TickSeconds);
    }
}
```

`Game.Run` opens the window and runs until it closes. `ModelRef.Cube` is a
built-in cube, so this runs with no files at all. There are
[other built-in shapes](rendering.md#built-in-shapes) too, such as `ModelRef.Sphere`.

Everything in the game lives in its [world](ecs.md): the camera, the sun and the cube are
each an entity, and the world draws them every frame. `Update` turns the cube, and the
world draws it turning smoothly between ticks. The game asks for its `World` in its
constructor, the same way it asks for anything else Super gives it.

A game implements `IGame`, which has six members. Only `Update` is required:

| Member | When it runs | What it's for |
| --- | --- | --- |
| `Load()` | Once, before the first tick | Build the starting world and bind input |
| `Update(GameTime)` | A fixed number of times per second, 30 by default | Change game state. The only place input can be read |
| `Draw(View)` | Once per displayed frame, after the world has drawn its [entities](ecs.md#drawing-entities) | Optional. Draw by hand what isn't an entity, such as a glow that pulses with the frame. See [Drawing by hand](views.md). It only reads state |
| `Ui` | Once per displayed frame | Describe the [interface](interfaces.md) on top. It only reads state |
| `Scripts` | Once, after `Load` | Named input scripts for [`--script`](command-line.md#scripted-input) |
| `CodeReloaded()` | At the start of the tick after [hot reload](hot-reload.md) changed the code | Rebuild what setup code made, often with `Reenter` |

Keeping the game apart from how it looks is the most important idea in Super. `Update`
is the game. Drawing only looks at it, so the game can run with no screen at all, and
the display can run faster or slower than the simulation. Each frame blends between
the last two ticks so motion stays smooth; see [Drawing entities](ecs.md#drawing-entities).

`GameTime` has `Tick`, `TickSeconds`, `TicksPerSecond` and `TotalSeconds`. Take time
from it rather than the system clock, so a seed always replays the same.

`Scale` sets how fast the world moves: 1 as normal, 0.25 for slow motion, 0 to freeze it.
Ticks keep coming at the same rate, so input and the camera stay smooth. Move things by
`DeltaSeconds`, which is the tick's length times the scale, and they slow with it.
Animators from content follow it on their own, and a
[cutscene](cutscenes.md#slow-motion) can set it.

## Options

```csharp
Game.Run<MyGame>(new GameOptions
{
    Title = "Colony",
    Id = "colony",
    Width = 1600,
    Height = 900,
    TicksPerSecond = 60,
    Seed = 42,
});
```

Without a `Seed`, each run picks a new one. `--seed` on the command line overrides it.
`Id` names the folder the player's [user data](user-data.md) is kept in, and shouldn't
change once the game ships. Without it, the title is used.
If the game falls more than a quarter of a second behind, the clock drops the backlog
instead of trying to catch up.

## Where your code goes

Game logic is plain C#. Split it into classes however you like, ask for them in your
constructor, and call them in the order you want. A "system" is simply a class with an
update method, and there's no hidden scheduler deciding what runs first.

```csharp
Game.Run<MyColony>(new GameOptions { Title = "Colony" }, services =>
{
    services.AddSingleton<Colony>();
    services.AddSingleton<Pathfinder>();
    services.AddSingleton<Woodcutting>();
    services.AddSingleton<IWeather, SeasonalWeather>();
});

class MyColony(Woodcutting woodcutting, IWeather weather) : IGame
{
    public void Update(GameTime time)
    {
        weather.Update(time);
        woodcutting.Update(time);
    }
}
```

```csharp
class Woodcutting(Colony colony, Pathfinder paths, GameRandom random)
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
            if (worker.Tree.Health <= 0)
            {
                colony.Remove(worker.Tree);
            }
        }
    }
}
```

Dependency injection is the standard `Microsoft.Extensions.DependencyInjection`
container, so it works just like ASP.NET. These are already registered:

| Service | What it is |
| --- | --- |
| `World` | [The world](ecs.md): every entity, which it draws each frame |
| `Scenes`, `Systems` | [Scenes](ecs.md#scenes) and [systems](ecs.md#systems) over the world |
| `GameTime` | The current tick and tick length |
| `GameRandom` | [Seeded random numbers](random.md) |
| `Content` | Models, images, meshes, textures and [JSON catalogs](content.md) |
| `Audio` | [Sounds, music and channel volumes](audio.md) |
| `Input`, `Actions<T>` | [Input](input.md) for any enum of actions |
| `States<T>` | [Game states](states.md) for any enum |
| `GameCapture` | [Screenshots and video from code](command-line.md#capturing-from-code) |
| `GameStats` | [The F3 stats panel, and frame rate, GPU and memory figures](interfaces.md#showing-how-the-game-runs) |

Everything is wired up at startup, and nothing is looked up while the game runs. Super
keeps no global state, so several games can run side by side in one process, which is
what lets [tests](testing.md) run in parallel.

## What's in the package

The one `Super` package holds these assemblies, each with a namespace of the same name:

| Assembly | What it gives you | Depends on |
| --- | --- | --- |
| `Super` | Window, game loop, input, game states, the [world](ecs.md), views, cameras, interfaces, content | `Rendition`, `Super.Random` |
| `Super.Random` | Seeded random numbers. Usable alone | Nothing else here |
| `Super.Physics` | [Bodies, characters, triggers and rays](physics.md), on Jolt Physics | `Super` |
| `Super.Testing` | [Headless runs and seeded tests](testing.md) | `Super` |
| `Rendition` | The renderer. Usable alone, with no engine | Nothing else here |

## Next

- [Working with coding agents](agents.md) if an agent is writing your game.
- [The world](ecs.md) to put more on screen.
- [Input](input.md) to make it playable.
- [Change code while the game runs](hot-reload.md) to try changes without restarting.
- [Build a tower defense](guides/tower-defense.md) to put it all together in a small, complete game.
- [Build a 3D platformer](guides/platformer.md) for physics, a character you steer and a camera that follows.
