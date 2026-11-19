# Supercharge Your Game

[![CI](https://github.com/Artmann/super/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Artmann/super/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Super?logo=nuget)](https://www.nuget.org/packages/Super)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Windows | macOS](https://img.shields.io/badge/platform-Windows%20%7C%20macOS-0078D4)](#try-it)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

[![The Reef example: schools of fish over a coral reef](docs/media/reef.webp)](docs/media/reef.mp4)

<sub>The [Reef](examples/Reef) example: 6,240 schooling fish. Click for the full 20-second video.</sub>

A modern C# game engine you use as a library, with a 3D renderer built in. Super is
made so a coding agent can build, run, test and see a game with no editor and no
person at the mouse.

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
        view.Lights.Sun(direction: new Vector3(1, 2, 1), color: new Vector3(3f));
        view.Model(key: 1, ModelRef.Cube, Matrix4x4.CreateRotationY(angle));
    }
}
```

## Quick Start

Open your coding agent in an empty folder or an existing C# project, then copy this
prompt into it:

```text
Set up a game in this folder with Super, a C# game engine used as a library.
It needs the .NET 10 SDK. The docs are at https://github.com/Artmann/super/tree/main/docs.

1. If there's no project yet, create a solution with a console project for the
   game, and run `dotnet new gitignore`.
2. Add the Super package from NuGet (https://www.nuget.org/packages/Super) with
   `dotnet add package Super`. Don't reference the Super repository itself.
3. Replace Program.cs with the spinning cube from docs/getting-started.md.
4. Add an xUnit test project that references the game, with one headless test
   from docs/testing.md that uses HeadlessGame and [Seeds].
5. Build, run the tests, then take a screenshot with
   `--seed 7 --ticks 60 --screenshot shots/first.png` and look at it.
```

Then ask your agent to build something:

```text
Make a small game with Super: a ball you roll around a flat arena with the arrow
keys, collecting coins that appear at random spots. Show the score in the top
corner. Add a headless test that collects a coin, and show me a screenshot.
```

## Why Super

**AI-first and friendly to agents.** Everything is code and plain text. There's no
editor, no scene files and no binary project format, so an agent reads and writes the
whole game.

- Every game runs headless. A test plays it through the same `Update` and `Draw` code,
  with no window and no GPU, and fails with the seed and tick that replay the problem.
- Every game has the same command line. `--screenshot`, `--record`, `--profile` and
  `--script` give an agent a picture, a video, a timing report or a scripted click, all
  repeatable from a seed.
- Content is JSON with generated schemas. Mistakes are reported with file and line, such
  as `items/axe.json:4: chopPower must be a number`.
- The interface is laid out for real in headless runs, so a test can check what text is
  on screen and where.

**A game engine, used as a library.** You write a normal .NET program. Super gives you
the window, a fixed-tick game loop, input, game states, cameras, an interface toolkit,
JSON content, seeded randomness and an optional ECS. Your state lives in your own
classes, and you call your own systems in the order you choose.

**Modern C#.** .NET 10, records, primary constructors and
`Microsoft.Extensions.DependencyInjection`. The interface is SwiftUI-style records.
Hot paths allocate nothing, and Super keeps no global state, so many games can run in
one test process.

**A modern 3D renderer.** [Rendition](src/Rendition/README.md) is a deferred,
physically based Vulkan renderer: cascaded soft shadows, image-based lighting,
screen-space reflections, temporal anti-aliasing, ambient occlusion, bloom, colour
grading and your own surface shaders. It also works on its own, without the engine.

![The Courtyard example: a brick wall with ivy, a barrel, a cart wheel and a puddle on flagstones and dirt](docs/media/courtyard.webp)

<sub>The [Courtyard](examples/Courtyard) example: every brick, flagstone, plank and patch of dirt is a material graph made in code.</sub>

## How a game is shaped

| Member | When it runs | What it's for |
| --- | --- | --- |
| `Load()` | Once, optional | Build the starting world |
| `Update(GameTime)` | A fixed number of times per second, 30 by default | Change game state |
| `Draw(View)` | Once per displayed frame | Describe the 3D world. It only reads state |
| `Ui` | Once per displayed frame, optional | Describe the interface on top. It only reads state |

`Update` is the game. `Draw` only looks at it, so the game runs with no screen at all,
and the same seed always plays the same way.

## Try it

You need Windows and a GPU with Vulkan 1.3, or a Mac with Apple silicon, and the .NET 10
SDK. Super is one [NuGet package](https://www.nuget.org/packages/Super):

```
dotnet new console -n MyGame
cd MyGame
dotnet add package Super
```

[Getting started](docs/getting-started.md) has a first game to put in `Program.cs`.

To run the examples or work on the engine, clone this repository. That also needs the
Vulkan SDK (`winget install KhronosGroup.VulkanSDK`), or on a Mac the shader compiler
(`brew install shaderc`):

```
dotnet build Super.slnx -c Release
dotnet test Super.slnx -c Release
dotnet run -c Release --project examples/Terrain
```

## Documentation

Start with [Getting started](docs/getting-started.md), then read what you need:

- [Working with coding agents](docs/agents.md)
- [Drawing with views](docs/views.md), [cameras and clicking](docs/cameras.md) and [rendering](docs/rendering.md)
- [Interfaces](docs/interfaces.md), [input](docs/input.md) and [game states](docs/states.md)
- [JSON content](docs/content.md) and [random numbers](docs/random.md)
- [Testing](docs/testing.md), [the command line](docs/command-line.md) and [profiling](docs/profiling.md)
- [The ECS](docs/ecs.md)
- [What's not here yet](docs/roadmap.md)

The [docs index](docs/README.md) lists every page, and [design.md](docs/design.md)
records why things are the way they are.

## Status

Super is early and changes often. Everything in the docs works today unless it's
marked as planned. The examples are the best place to see it running:

| Example | Shows |
| --- | --- |
| [ColorCube](examples/ColorCube) | The game loop, input actions and seeded randomness |
| [Shapes](examples/Shapes) | Every built-in shape, and shapes made in code with `Shapes` |
| [Bricks](examples/Bricks) | A brick wall and a cobbled street whose mortar sinks in with height maps and darkens with occlusion maps, with fine grit from detail maps; P, D and O turn them off |
| [Rooms](examples/Rooms) | Game states, a loading screen, JSON content, clicking the ground and huts that rise on a curve |
| [Courtyard](examples/Courtyard) | Material graphs: bricks, flagstones, dirt, wood, leather and rusty metal made in code, on a courtyard modelled in Blender |
| [Crafting](examples/Crafting) | An inventory and crafting bench driven entirely by the mouse |
| [Terrain](examples/Terrain) | Endless generated hills with a custom surface shader, a road built along a spline with a cart driving it, and a flyover on F |
| [Toon](examples/Toon) | A cartoon garden in flat bands of light with ink outlines, under a low sun, a lantern and fog, beside a physically based chrome ball; T and O turn them off |
| [Reef](examples/Reef) | 6,240 schooling fish with the ECS, water and a stats panel |
| [Fox](examples/Fox) | A skinned character blending walk into run, with a head turn played over it and footsteps from its clips |
| [Glass](examples/Glass) | Clear, frosted and tinted panes and glass balls that bend the room, with dust between them |
| [Archer](examples/Archer) | A short film: a cutscene with slow motion, root motion, a sky and a forest clearing |
| [Transitions](examples/Transitions) | Fades, dissolves, wipes, irises and camera blends: a showreel cutscene, then each one on a key |
| [Crypt](examples/Crypt) | A stone hall lit only by fire: torches casting shadows, brass candelabras, a lantern's beam and 126 candles, about 150 lights in all |
| [Hamlet](examples/Hamlet) | A village on a hill made from the seed, through a two-minute day: the sun and sky move, and at dusk lamps and windows light up, about 200 lights; R for a new village |
| [Gallery](examples/Gallery) | Plaster rooms along a sunlit colonnade: a bust between a red wall and a blue one, a bronze whale under a skylight, a lamp room and a candlelit chapel, all under today's real-time lights |
| [Wax](examples/Wax) | Skin, candle wax and jade with subsurface scattering, each beside a plain twin under a hard light |
| [Dust](examples/Dust) | Trees falling in a clearing, throwing up soft particle dust along where they land |
| [Cannonball](examples/Cannonball) | Ragdolls: a stuntman fired from a cannon tumbles through crates and balls, then gets up where they land; D shows the ragdoll's bodies |
| [Forest](examples/Forest) | Sound: a looping ambience and music, a hammering builder whose sound pans as the camera circles, and channel volumes |
| [Outpost](examples/Outpost) | A small tower defense made from built-in shapes, built step by step in [the guide](docs/guides/tower-defense.md) |

## License

The code is under the [MIT license](LICENSE). Some assets have their own terms, listed
in [CREDITS.md](CREDITS.md).
