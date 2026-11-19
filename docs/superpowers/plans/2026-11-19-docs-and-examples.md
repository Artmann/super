# Docs and Examples (stage 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every example and every doc page teaches the world: entities for anything that lasts longer than a frame, scenes for what's in the world, systems where a game splits its update.

**Architecture:** No new engine feature except one: `Systems.Add<T>` uses the game's registered `T` when there is one, so a system can also be asked for by other classes (Outpost's towers fire shots through `Shots`). Each example's hand `view` calls for lasting things become entities spawned once (in `Load`, a constructor or a scene build). Stage 6 features (decals, fog volumes, probes, baked lighting, water, puddles, wind) stay as view calls.

**Tech Stack:** C# / .NET 10, xUnit, Super's `World`, `Scenes`, `Systems`, ffmpeg for picture comparison.

**Spec:** `docs/superpowers/specs/2026-11-19-world-first-design.md`, stage 5.

## Global Constraints

- "When this stage ends, a hand `view` call for a lasting thing appears only on the drawing-by-hand page and for the stage 6 features."
- "Pictures are retaken only where the picture changes."
- "Outpost's `Enemies`, `Towers` and `Shots` have no `Draw`, no keys and no previous positions, and `NewGame` no longer calls a `Clear` on each."
- Braces on every control statement; docs change in the same commit as the code; `dotnet roe health` and `dotnet roe dead-code` clean; conventional commits with no Claude attribution; no run-in bold headings in prose.
- Every example's own tests pass after its conversion, and an example with no tests still starts and takes a `--screenshot --validate` with 0 validation messages.

## How a conversion is checked

Before touching an example, take shots with `--seed 1 --size 1280x720 --validate --screenshot` at a fixed `--ticks` (and its scripts, if it has any) into `$TEMP/s5/<example>-<name>-before.png`. After the conversion take the same shots as `-after.png` and compare:

```
ffmpeg -i before.png -i after.png -lavfi ssim -f null -
```

Expected: `All:` at or above 0.98, or a look at both pictures that explains the difference (a ruling in the ledger). A picture that changes on purpose is retaken in `docs/media/` only if the docs use it.

## Review Focus

- A system that spawns or destroys during a query, and a second system reading the same entities in the same tick: queued changes must not double-count (two shots killing one enemy pay one reward).
- A restart (`scenes.Rebuild` or a new `Go`) that leaves an entity spawned straight on the world instead of through the scene, so it survives the restart.
- An entity whose model used to be drawn at an offset from its gameplay position (a capsule lifted by its size): the new transform must draw it in the same place.
- A camera kept as a value for picking (mouse rays) and as an entity for drawing: the two must not drift apart.
- Docs that still name `view.Models`, `Smoothed`, `AddSuperEcs`, `PreviousPosition` or hand keys for lasting things.

---

### Task 1: Systems use a registered service

**Files:** Modify `src/Super/Systems.cs`, `tests/Super.Tests/SystemsTests.cs`, `docs/ecs.md` (Systems section).

**Interfaces:** Produces `Systems.Add<T>()` that returns the registered `T` when the game registered one, and makes a new one otherwise.

- [ ] Test `A_registered_system_is_the_same_one_the_game_gets`: register `Log` and `Waves` as singletons, `Add<Waves>()`, assert `Update` writes through the registered instance (resolve `Waves` from the game and compare with `Assert.Same` through a game property).
- [ ] Run it: FAIL (two different instances).
- [ ] `ActivatorUtilities.GetServiceOrCreateInstance<T>(services)`.
- [ ] Run SystemsTests: PASS. Docs paragraph. Commit `feat: Systems use the game's own registered system`.

### Task 2: Outpost on entities, scenes and systems

**Files:** Modify every file in `examples/Outpost/` and `tests/Outpost.Tests/OutpostTests.cs`; create `examples/Outpost/Components.cs`.

**Interfaces:**
- `public record struct Enemy(EnemyKind Kind, int Health, float Distance, float Offset);`
- `public record struct Tower(int X, int Z, float Reload);`
- `public record struct Shot(Entity Target);`
- `Enemies : ISystem` — `Entity Spawn(EnemyKind)`, `void Hit(Entity, int)`, `int Count`, `static EnemyStats Stats(EnemyKind)`.
- `Towers : ISystem` — `bool TryBuild(int x, int z)`.
- `Shots : ISystem` — `void Fire(Vector3 from, Entity target)`.
- `Waves : ISystem` — unchanged members.
- `Player.Killed` (moved from `Enemies.Killed`, reset by `Player.Reset`).
- `Scenery.Build(Scene)` spawns camera, sun, shadows, sky, outlines, finish, island, trees, tiles and base.
- `OutpostGame` has no `Draw`; `Load` calls `scenes.Go(scenery.Build)`; `NewGame` is `player.Reset(); waves.Reset(); scenes.Rebuild();`.

- [ ] Baseline shots: title (60 ticks), `--script towers` (900), `--script start` (1500).
- [ ] Rewrite the tests that read lists (`All`, `Killed`) to count the world, and replace `Every_tile_is_drawn` with a count of drawn tiles by model. Add `Two_shots_landing_together_pay_one_reward` (Review Focus 1). Run: FAIL to compile.
- [ ] Convert the classes. Enemies spawn through `scenes.Current`, with the transform at the body's middle (road point lifted by the size).
- [ ] Outpost tests: PASS. After shots, SSIM against before.
- [ ] README of the example updated. Commit `refactor: Outpost on entities, scenes and systems`.

### Task 3: The tower defense guide

**Files:** `docs/guides/tower-defense.md`.

- [ ] Every code block matches the Task 2 code; no keys, no `PreviousPosition`, no `Draw` in Enemies, Towers or Shots, no `Clear`. Chapter pictures stay unless what the chapter shows changed.
- [ ] grep the guide for `view.Model`, `Smooth`, `_000`, `Clear(`: none left outside a sentence about drawing by hand. Commit `docs: the tower defense guide builds on the world`.

### Task 4: Rooms on scenes

**Files:** `examples/Rooms/*`, its tests if any, `docs/` pages that describe Rooms.

- [ ] Shots before; replace the enum-room swapping with `scenes.Go(room.Build, transition)`; shots after; tests pass. Commit `refactor: Rooms goes between rooms with scenes`.

### Tasks 5–11: The other examples

One task, one commit (`refactor: <Example> on entities`) per group, each checked as above:

5. Crypt  6. Gallery  7. Courtyard  8. Hamlet and Transitions  9. Terrain, Glass and Archer  10. Reef, Dust, Toon, Cannonball, Fox and Wax  11. Bricks, Shapes, ColorCube, Forest and Crafting.

In each, lasting `view.Model`, lights, sky, shadows, finish, outlines, ambient occlusion, particles that loop and the camera become entities; `Smooth`/`Blend` of lasting things goes; stage 6 calls stay. A `Draw` left behind holds only what only that frame shows.

### Task 12: The world is the main page

**Files:** `docs/ecs.md` (title "The world"), `docs/views.md` (one page on drawing by hand), `docs/getting-started.md`, `README.md`, links to both pages.

- [ ] getting-started's first game spawns entities. views.md keeps `view.Model` with keys, `Smooth`, `Blend`, `Cached`, particles by hand and what only a frame shows, and points to the world for everything else.

### Task 13: The rest of the docs

**Files:** `docs/rendering.md`, `docs/cameras.md`, `docs/animation.md`, `docs/tweens.md`, `docs/splines.md`, `docs/design.md`, and any page grep finds.

- [ ] Each sample that shows a lasting thing through a view call shows the component instead, unless it's a stage 6 feature.
- [ ] `grep -rn "view\.\(Model\|Lights\|Surroundings\|Effects\.\(Finish\|Outlines\)\|Camera\)" docs --include=*.md` lists only views.md, stage 6 sections and per-frame examples.

### Task 14: Stage check

- [ ] `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`, the GPU tests, `dotnet roe health`, `dotnet roe dead-code`; push.
