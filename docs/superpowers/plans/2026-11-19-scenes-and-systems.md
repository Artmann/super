# Scenes and Systems Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stage 4 of the world-first design: a `Scenes` service that swaps what's in the world, with transitions, and an optional `Systems` runner.

**Architecture:**
- A scene owns its entities through an internal `SceneMember(int Scene)` tag. It isn't a `Parent` to a root, so a large static scene costs nothing extra per frame.
- `Scenes.Go` only records a request. The game loop lands it at the end of the tick, after state changes, so nothing is swapped in the middle of `Update`.
- A fade's cut only marks the swap as due, so a fade that ends early still waits for the tick boundary.
- `Systems` makes each system through `ActivatorUtilities` and runs them in the order they were added.

**Tech Stack:** C# / .NET 10, xUnit, Microsoft.Extensions.DependencyInjection.

**Spec:** `docs/superpowers/specs/2026-11-19-world-first-design.md`, sections "Scenes" and "Systems, if you want them".

## Global Constraints

- Braces on every control statement. Use conventional commits with no Co-Authored-By line.
- Docs change in the same commit as the code. `docs/ecs.md` gets the user-facing sections and `docs/design.md` gets the internals rows.
- `dotnet roe` must stay clean: no dead code, no new health findings, and duplicates at or below 187.
- Prove every test can fail by breaking the code on purpose.
- `States<T>` and `ScopedTo<T>` don't change.

## Review Focus

1. `Go` is asked for while a fade from an earlier `Go` hasn't reached its cut. The newer build wins, the earlier one never runs, and no second fade starts.
2. Something else ends a fade early (`transitions.Play` or `Stop`). The swap still waits for the end of the tick.
3. Entities from `world.Spawn` survive every `Go`. A `world.Spawn` child of a scene entity goes with it.
4. A build function can't read input.
5. `Rebuild` with no scene yet throws, naming `Go`.

---

### Task 1: Scenes without transitions

**Files:**
- Create: `src/Super/World/Scene.cs`, `src/Super/World/Scenes.cs`
- Modify:
  - `src/Super/World/WorldSpawn.cs`: make `Spawning` internal so `Scene.Spawn` can add the tag in the same spawn.
  - `src/Super/LoopTick.cs`: call `_scenes.Apply()` after `_changes.Apply()`.
  - `src/Super/HostServices.cs`: register `Scenes`.
  - `src/Super.Testing/HeadlessGame.cs`: add `run.Scenes`.
  - Docs: `docs/ecs.md` (a "Scenes" section), `docs/design.md`, `docs/testing.md`.
- Test: `tests/Super.Tests/World/SceneTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class Scene` with `World World` and `Spawn<T1>` through `Spawn<T1..T4>`, each returning `Entity`.
  - `public sealed class Scenes(World world, Transitions transitions)` with:
    - `void Go(Action<Scene> build, Transition? transition = null)`
    - `Scene? Current`, `bool IsChanging`, `void Rebuild()`
    - `internal void Apply()`

- [ ] Write the tests. Each is a headless game whose Update calls `Go` on a chosen tick:
  - `Go_builds_the_scene_at_the_end_of_the_tick`: the build hasn't run during the Update that asked, and it has run after the tick.
  - `Go_destroys_the_old_scene_before_building_the_new_one`
  - `Entities_spawned_on_the_world_outlive_every_scene`
  - `A_world_child_of_a_scene_entity_goes_with_the_scene`
  - `The_last_Go_in_a_tick_wins`
  - `Rebuild_runs_the_current_build_again`
  - `Rebuild_before_any_scene_throws`: the message is "There's no scene to rebuild yet. Call Go first."
  - `A_build_cannot_read_input`: the build throws, as `Input` does outside Update.
  - `Current_is_the_scene_last_built`
- [ ] Run them. Expected: they fail to compile.
- [ ] Implement, then run the fast suite and `dotnet roe`.
- [ ] Commit `feat: Scenes swap what's in the world at the end of a tick`.

### Task 2: Going to a scene through a transition

**Files:**
- Modify: `src/Super/World/Scenes.cs`, `docs/ecs.md`, `docs/design.md`
- Test: `tests/Super.Tests/World/SceneTransitionTests.cs`

**Interfaces:**
- Consumes: Task 1's `Scenes`.

- [ ] Write the tests. At 30 ticks a second, `Go` is called on tick 10 with `Transition.Fade(1f)`:
  - `A_fade_swaps_the_scene_at_the_end_of_its_cut_tick`: the cut is on tick 25, so the build runs at the end of tick 25.
  - `A_wipe_swaps_at_once_and_holds_the_old_picture`: the scene is built at the end of tick 10 and `run.Transition.Hold` is above 0.
  - `A_second_Go_during_the_fade_replaces_the_first_and_keeps_the_fade`: only the second build runs, and `Playing` is the same `Transition` instance.
  - `A_fade_stopped_early_still_swaps_at_the_end_of_the_tick`: `transitions.Stop()` in Update means the build hasn't run during that Update and has run after the tick.
  - `IsChanging_is_true_until_the_fade_ends`
- [ ] Run them. Expected: they fail.
- [ ] Implement. A fade's `atCut` sets `_cutDue`, and `Apply` swaps once `_cutDue` is set. Other transitions start in `Apply` and swap right then.
- [ ] Commit `feat: Go to a scene through a transition`.

### Task 3: Systems

**Files:**
- Create: `src/Super/Systems.cs` (`ISystem` and `Systems`)
- Modify: `src/Super/HostServices.cs`, `docs/ecs.md`, `docs/design.md`
- Test: `tests/Super.Tests/SystemsTests.cs`

**Interfaces:**
- Produces:
  - `public interface ISystem { void Update(GameTime time); }`
  - `public sealed class Systems(IServiceProvider services)` with `Systems Add<T>() where T : class, ISystem` and `void Update(GameTime time)`.

- [ ] Write the tests:
  - `Update_runs_systems_in_the_order_they_were_added`
  - `A_system_gets_its_services_without_being_registered`: a system takes `World` in its constructor.
  - `Adding_a_system_twice_throws`: the message is "Waves is added already."
  - `Nothing_runs_unless_the_game_calls_Update`
- [ ] Implement, run, and commit `feat: Systems run a game's systems in order`.

### Task 4: Stage check

- [ ] Run `dotnet roe`. Expected: clean.
- [ ] Run `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`. Expected: all pass.
- [ ] Grep `docs/` for `Scenes`, `Systems` and `ISystem` so the docs agree.
- [ ] Push the branch.
