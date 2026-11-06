# Super documentation

Each page covers one part of Super, with examples. Code marked as planned shows an API
that doesn't exist yet; everything else works today.

| Page | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Requirements, a first game, the game loop, packages and where your code goes |
| [Working with coding agents](agents.md) | How an agent builds, runs, tests and sees a Super game |
| [Drawing with views](views.md) | `Draw`, keys, view memory, smoothing, `Cached` and state versus view |
| [Cameras and clicking](cameras.md) | `Camera`, the camera helpers, orthographic and shifted views, and picking |
| [Rendering](rendering.md) | Graphics settings for speed, lights, shadows, the sky, water, post-processing, meshes made by code and along splines, surface shaders, terrain and models |
| [Animation](animation.md) | Rigged models, Mixamo clips, states, actions, blends, masks, root motion and joints |
| [Cutscenes](cutscenes.md) | Shots, moments, slow motion and the `Director` |
| [Transitions](transitions.md) | Fades, dissolves, wipes, irises and camera blends, in play and between shots |
| [Tweens and curves](tweens.md) | Easings, curves drawn by key or in JSON, tweens, sequences and groups |
| [Splines](splines.md) | Paths for rails, roads and patrols: asking where, following, camera rails and meshes along them |
| [Interfaces](interfaces.md) | `Super.Ui`: views, modifiers, layout, colours, state and the mouse |
| [Input](input.md) | Actions, bindings and the raw `Input` service |
| [Audio](audio.md) | Sounds, music, channel volumes and ambiences |
| [Game states](states.md) | `States<T>`, enter and exit callbacks, `Reenter` and loading screens |
| [Change code while the game runs](hot-reload.md) | `dotnet watch`, what applies live, `CodeReloaded`, and errors that stop the game instead of closing it |
| [JSON content](content.md) | Catalogs, references, schemas, live reload and shipping |
| [Material graphs](materials.md) | Materials written as C# recipes of noises, tiles and filters, cooked into textures: writing a graph, the nodes, variants, sizes, checks and cook time |
| [User data](user-data.md) | `UserData`: settings and progress kept per player, outside saved games |
| [Random numbers](random.md) | `GameRandom` and named streams |
| [Testing](testing.md) | Headless runs, seeds, invariants, played input and checking the screen |
| [The command line](command-line.md) | `--screenshot`, `--record`, `--script` and capturing from code |
| [Profiling](profiling.md) | `--profile` and reading its report |
| [The ECS](ecs.md) | `Super.Ecs`: queries, drawing entities and entities scoped to a state |
| [Physics](physics.md) | `Super.Physics`: bodies that fall and collide, characters, trigger areas, rays and shape casts |
| [What's not here yet](roadmap.md) | Known gaps and planned features |

Background reading:

- [design.md](design.md) records why the engine and renderer are built the way they are.
- [The Rendition README](../src/Rendition/README.md) covers using the renderer alone.
- `superpowers/specs` and `superpowers/plans` hold the design notes behind each feature.
