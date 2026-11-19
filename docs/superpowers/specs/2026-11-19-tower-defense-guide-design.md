# Tower defense guide: design

## Intent

Super has a reference page for each part of the engine, but nothing that shows the parts
working together in one small game. This guide fills that gap. A C# developer trying out
Super follows it from `dotnet new console` to a finished, playable tower defense, and comes
away knowing how a Super game is put together and where to look next.

The reader knows C# and .NET well. They may know nothing about games or Super. So the guide
skips C# basics and spends its words on Super: what each part is for, and why the game is
written that way.

The tone is simple and friendly: plain words, short sentences, "we" and "you". No run-in
bold headings (paragraphs that open with a bold label); use real headings instead.

## Goals

- A guide at `docs/guides/tower-defense.md` that builds the game in chapters. Each chapter
  adds one thing and ends with a game that runs.
- The finished game in `examples/Outpost`, built in CI like every other example, with a
  small test project, `tests/Outpost.Tests`.
- The game uses only built-in shapes (`ModelRef.Cube`, `Cylinder`, `Cone`, `Capsule` and so
  on), so the reader needs no models, Blender, downloads or shaders.
- A `docs/guides/README.md` index (one entry for now) and a link to it from `docs/README.md`
  and from the end of `docs/getting-started.md`.
- One or two screenshots in `docs/media/` used in the guide and in the example's README.

## Non-goals

- Sound. There are no built-in sounds, and adding files would break the no-assets rule.
  The last chapter points to `audio.md`.
- Physics, the ECS, animation, cutscenes, hot reload. Each gets one line in a "where to go
  next" section with a link, nothing more.
- Pretty lighting work. A sun, the default sky and plain colours are enough.
- Balance. The game should be winnable and losable, not tuned.

## Anti-goals

- A guide whose code doesn't match the example. If they drift, it's broken.
- Chapters that rewrite earlier code. Each chapter only adds, or changes a line or two
  and says so plainly.
- A wall of API listings. Link to the reference page instead of repeating it.
- New engine APIs made just for the guide. If something is awkward in the guide, note it
  for later rather than changing the engine in this work.

## The game: Outpost

- A grid of ground tiles, seen from above at an angle.
- A winding road across the grid, ending at the base.
- Enemies walk the road in waves. Capsules for walkers; later waves add a faster, weaker
  kind and a slower, tougher kind (different colour and size).
- Clicking an empty grass tile buys a tower (a cylinder with a cone on top) for gold. A
  tower shoots the enemy nearest the base that's in range, with a small sphere that flies to
  it.
- Killing an enemy gives gold. An enemy reaching the base costs a life.
- A title screen, then 10 waves, then a win or game-over screen. R starts again.
- A small UI showing gold, lives and the wave, and the tower's price.

## Approaches

1. The game grows chapter by chapter; `examples/Outpost` holds the finished version.
   Readers build and run it after every chapter. CI covers the final code; early chapters
   are checked by hand while writing. **Chosen.**
2. A code snapshot per chapter (`Step1`..`Step7`). Every chapter compiles, but it's seven
   copies of a growing game in the repo, which Roe would flag as duplication.
3. A tour of the finished game, file by file. Shortest to write, but it reads like reference
   docs and the reader never builds anything.

The user picked 1, because the reader should be able to type it in and see it run after each
chapter.

## Chapters (proposed)

1. A new project: `dotnet new console`, `dotnet add package Super`, an `IGame` with a camera,
   a sun and the ground grid. Update versus Draw.
2. The road: a spline through the grid, drawing road tiles along it.
3. Enemies: spawning, walking the spline by distance, drawing them smoothly.
4. Towers: actions for the mouse, picking the tile under the cursor, placing a tower.
5. Shooting: finding a target, projectiles, damage, gold and lives.
6. Waves and the UI: a wave list, the gold/lives/wave display.
7. Screens: game states for title, playing, won and lost; restarting.
8. Testing it: a headless test, and `--screenshot` to see it.
9. Where to go next: links to sound, physics, the ECS, content files and so on.

## Calls made (the builder can revisit)

- Game state is plain C# classes and lists, not the ECS, since the guide is about seeing
  how a game fits together, and plain classes are the shortest path. The ECS gets a
  pointer at the end.
- Ranges and hits are plain distance checks, not physics.
- The example project references `src/Super/Super.csproj` like the other examples; the
  guide shows `dotnet add package Super`. The guide says this once.
- Code split into a few files in the example (`OutpostGame.cs`, `Program.cs`, and a file
  per idea such as `Road.cs`, `Enemies.cs`, `Towers.cs`), and the guide says which file each
  snippet belongs in.

- Guide and example are kept in sync by hand, not by tooling that pulls regions out of the
  source: the guide is written alongside the code, and its last chapter's full code is
  compared with `examples/Outpost` before merging.
- Each chapter is checked by building it in a fresh console project in a scratch folder
  that references the repository's `src/Super/Super.csproj`, run with `--screenshot`. Only
  APIs in the published package (v1.6.0, and nothing new since) are used, so no release has
  to come first.

## Left to the builder

- Grid size, road shape, numbers (prices, health, speeds, wave makeup).
- Which Super APIs fit each chapter best (splines, picking, `States<T>`, `Super.Ui`,
  `Actions<T>`, `GameRandom`, the test helpers), after reading their docs pages.
- Exact chapter split, if a chapter turns out too long or too short.
- The test(s): at least one that proves a wave with no towers costs lives, and one that
  proves a tower kills enemies. Each must be shown to fail when the code is broken.
