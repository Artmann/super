# Getting started

## Requirements

- Windows and a GPU with Vulkan 1.3 support
- .NET 10 SDK
- Vulkan SDK, to compile shaders: `winget install KhronosGroup.VulkanSDK`

Super isn't on NuGet yet. Clone the repository, build and test it:

```
dotnet build Super.slnx -c Release
dotnet test Super.slnx -c Release
```

The tests need no window or GPU. GitHub Actions runs the same build and tests on every pull
request and on each push to `main`, on Windows (`.github/workflows/ci.yml`). The recording tests
there need `ffmpeg`, as they do locally.

To see games running, try the examples:

```
dotnet run -c Release --project examples/ColorCube
dotnet run -c Release --project examples/Rooms
dotnet run -c Release --project examples/Crafting
dotnet run -c Release --project examples/Terrain
dotnet run -c Release --project examples/Forest
```

The Terrain example is endless hills you fly over with WASD, the mouse, Space and
Shift. The Forest example is a rainy forest with music and a builder hammering at a cabin; try
Space and the keys 1 to 4 for the sound. The Reef example needs models that aren't in the repository; see
[Rendering](rendering.md#models) for how to cook them.

## A first game

```csharp
using System.Numerics;
using Super;

Game.StartWindowsGameLoop<Spinner>(new GameOptions { Title = "Spinner" });

class Spinner : IGame
{
    float angle;

    public void Update(GameTime time) => angle += time.TickSeconds;

    public void Draw(View view)
    {
        view.Camera(position: new Vector3(0, 2, 5), lookAt: Vector3.Zero);
        view.Sun(direction: new Vector3(1, 2, 1), color: new Vector3(3f));
        view.Model(key: 1, ModelRef.Cube, Matrix4x4.CreateRotationY(angle));
    }
}
```

`StartWindowsGameLoop` opens the window and runs until it closes. `ModelRef.Cube` is a
built-in cube, so this runs with no files at all.

A game implements `IGame`, which has five members. Only `Update` and `Draw` are required:

| Member | When it runs | What it's for |
| --- | --- | --- |
| `Load()` | Once, before the first tick | Build the starting world and bind input |
| `Update(GameTime)` | A fixed number of times per second, 30 by default | Change game state. The only place input can be read |
| `Draw(View)` | Once per displayed frame | Describe the 3D world. It only reads state |
| `Ui` | Once per displayed frame | Describe the [interface](interfaces.md) on top. It only reads state |
| `Scripts` | Once, after `Load` | Named input scripts for [`--script`](command-line.md#scripted-input) |

Keeping `Update` and `Draw` apart is the most important idea in Super. `Update` is the
game. `Draw` only looks at it, so the game can run with no screen at all, and the
display can run faster or slower than the simulation. `Draw` blends between the last
two ticks so motion stays smooth; see [Drawing with views](views.md#smooth-motion).

`GameTime` has `Tick`, `TickSeconds`, `TicksPerSecond` and `TotalSeconds`. Take time
from it rather than the system clock, so a seed always replays the same.

`Scale` sets how fast the world moves: 1 as normal, 0.25 for slow motion, 0 to freeze it.
Ticks keep coming at the same rate, so input and the camera stay smooth. Move things by
`DeltaSeconds`, which is the tick's length times the scale, and they slow with it.
Animators from content follow it on their own, and a
[cutscene](cutscenes.md#slow-motion) can set it.

## Options

```csharp
Game.StartWindowsGameLoop<MyGame>(new GameOptions
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
| `GameTime` | The current tick and tick length |
| `GameRandom` | [Seeded random numbers](random.md) |
| `Content` | Models, images, meshes, textures and [JSON catalogs](content.md) |
| `Audio` | [Sounds, music and channel volumes](audio.md) |
| `Input`, `Actions<T>` | [Input](input.md) for any enum of actions |
| `States<T>` | [Game states](states.md) for any enum |
| `GameCapture` | [Screenshots and video from code](command-line.md#capturing-from-code) |
| `GameStats` | [Frame rate and memory figures](interfaces.md#showing-how-the-game-runs) |

Everything is wired up at startup, and nothing is looked up while the game runs. Super
keeps no global state, so several games can run side by side in one process, which is
what lets [tests](testing.md) run in parallel.

## Packages

| Package | What it gives you | Depends on |
| --- | --- | --- |
| `Super` | Window, game loop, input, game states, views, cameras, interfaces, content | `Rendition`, `Super.Random` |
| `Super.Random` | Seeded random numbers. Usable alone | Nothing else here |
| `Super.Ecs` | An optional [entity component system](ecs.md) | `Super` |
| `Super.Testing` | [Headless runs and seeded tests](testing.md) | `Super` |
| `Rendition` | The renderer. Usable alone, with no engine | Nothing else here |

## Next

- [Working with coding agents](agents.md) if an agent is writing your game.
- [Drawing with views](views.md) to put more on screen.
- [Input](input.md) to make it playable.
