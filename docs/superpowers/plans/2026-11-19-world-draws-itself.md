# The World Draws Itself Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stage 2 of the world-first design. Every frame is made from the world before the game's `Draw` runs, so a game with only entities needs no `Draw` at all.

**Architecture:** A new internal `WorldFrame` describes the world into the `View` at the start of each frame, in `LoopDraw.RunOnce` after `view.Begin` and before `IGame.Draw`. The game's `Draw` then adds to the frame or overrides parts of it, because the view's setters overwrite. The model drawing in `ViewExtensions.Models` moves into `WorldFrame`, and the public `view.Models(world)` goes. For the one-per-frame components (camera, sky, shadows and the rest), the world remembers the order they were given, so the newest one wins.

**Tech Stack:** C# / .NET 10, xUnit, Super's `World` (`src/Super/World`), `View` (`src/Super/View*.cs`).

**Spec:** `docs/superpowers/specs/2026-11-19-world-first-design.md`, sections "The world draws itself", "Cameras" and "Emitters that loop". This goes on `feat/world-in-core` into the same PR as stage 1 (#148). The user asked for every stage in that one PR (2026-11-19), so stages 3 to 6 follow on this branch.

## Global Constraints

- Every control statement has braces. Use conventional commits, with no Co-Authored-By line and no Claude author.
- Docs change in the same commit as the code: the user guide (`ecs.md`, `views.md`, `rendering.md`, `cameras.md`) and the engine internals in `design.md`. Before committing, grep `docs/` for each name you touched.
- `dotnet roe` must stay clean: no dead code, no new health findings, and no new duplicate groups (187 now). `World` is at the 20-member limit, so new world calls go in extension classes. A method takes at most 5 parameters.
- Every new test is broken on purpose once, so you see it fail, and then the code is restored. Slow tests (over 0.5 s) get `[Trait("Speed","Slow")]`.
- Run `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"` while working, and `--filter "Needs!=GPU"` plus `dotnet test tests/Rendition.Gpu.Tests -c Release` before the last commit. Two GPU tests fail on main too (LiveProbe allocation, AutoBake budget).
- Frame code allocates nothing in steady state. Use no lambdas on the frame path (memory: they allocate when JITted).
- Every new component gets the same checks as the `view` call it stands for. It runs through that call, so a bad value throws with the same message.
- Defaults must look best, not match old behaviour (memory: feature-switches-and-defaults).

## Review Focus

- A game that has both a `Camera` entity and a `view.Camera(...)` in `Draw`. `Draw` wins, and a cutscene wins over both.
- A one-per-frame component (camera, sky) removed from the newest holder. The next newest wins at once; it must not fall back to the default camera.
- A `Hidden` parent whose child has a `PointLight`, `Emitter` or `Sound`. None of them is drawn, lit or heard, at any depth.
- A static entity changed through `world.Get<Transform>` without `world.Changed`. It stays where it was described, and the docs say so beside `Static`.
- An `Emitter` at a time scale of 0, or across a reload. The same particles show, because they depend only on game time and the entity.

---

### Task 1: Run OnAdded before the next queued change

This fixes a stage 1 minor. When one query adds and then removes a type, `OnRemoved` runs but `OnAdded` never does.

**Files:**
- Modify: `src/Super/World/PendingChanges.cs` (`Apply`, `Make`)
- Test: `tests/Super.Tests/World/ReactionTests.cs`

**Interfaces:**
- Produces: nothing new. A queued add now runs its `OnAdded` handlers right after it is made, before the next queued change.

- [ ] **Step 1: Write the failing test.** `Adding_then_removing_in_one_query_runs_both_handlers`: add `Tag` and then remove it inside one `world.Query<Transform>()` loop. Assert `added == 1`, `removed == 1`, and that the entity ends without `Tag`.
- [ ] **Step 2: Run it.** `dotnet test tests/Super.Tests -c Release --filter FullyQualifiedName~ReactionTests` fails with `added == 0`.
- [ ] **Step 3: Implement.** In `Apply`, remember `_changes.Count` before a `Kind.Add` change is made. Then run, at once, the `Kind.Added` entries that the Make appended for that entity, and mark them done (set their `Kind` to a new `Kind.Done`) so the later pass skips them.
- [ ] **Step 4: Run all of ReactionTests and QueuedChangesTests.** They pass.
- [ ] **Step 5: Commit** `fix: run a queued add's OnAdded before the next queued change`. Update the "Changes during a query" row in `design.md`.

### Task 2: The world describes its models before Draw, and Draw is optional

**Files:**
- Create: `src/Super/World/WorldFrame.cs` (internal static class), with the `ModelTypes` and `ModelRows` code from `ViewExtensions.cs`
- Delete: the public `ViewExtensions.Models`. Keep `Blended` and `WorldMatrix` as internal helpers in `WorldFrame`. Delete `ViewExtensions.cs` once it is empty.
- Modify: `src/Super/IGame.cs` (`Draw` gets an empty default body), `src/Super/LoopDraw.cs` (take `World`, call `WorldFrame.Describe` after `view.Begin`), `src/Super/View.cs` (`internal World? World`, `public Matrix4x4 TransformOf(Entity entity)`), and the host where `View` is made, which sets `view.World`
- Modify: `examples/Cannonball/CannonballGame.cs:142`, `examples/Forest/ForestGame.cs:210`, `examples/Reef/ReefGame.cs:89` and `examples/Rooms/RoomsGame.cs:170`, which drop `view.Models(world)`. Any `Draw` left empty is deleted.
- Modify: the tests that call `view.Models` (`ModelsTests`, `ScopedTests`, `SmoothMotionTests`). Headless tests drop it; `ModelsTests` calls `WorldFrame.Describe(_view, _world)`.
- Test: `tests/Super.Tests/World/WorldFrameTests.cs`

**Interfaces:**
- Produces: `internal static void WorldFrame.Describe(View view, World world)`, which later tasks extend with one call per part (`Cameras`, `Lights`, `Settings`, `Emitters`). `public Matrix4x4 View.TransformOf(Entity entity)` gives the entity's blended matrix in the world, following its parents, and throws `InvalidOperationException("Entity {id} has no Transform.")` for an entity without one. `internal static Matrix4x4 WorldFrame.WorldMatrix(World, Entity, float blend)`.

- [ ] **Step 1: Write the failing tests.**
  - `A_game_with_no_Draw_shows_its_entities`: a headless game whose `IGame` has no `Draw`, spawning a cube. After `run.Ticks(1); run.Frame(0f)`, `run.Drawn` has one model keyed by the entity's ID.
  - `Draw_adds_to_what_the_world_drew`: the same game, with a `Draw` that draws one hand model, shows 2 models.
  - `TransformOf_follows_the_parent_between_ticks`: a parent moving 2 on x per tick and a child at local (0,1,0). At blend 0.25 after 2 ticks, the translation is (2.5, 1, 0).
- [ ] **Step 2: Run them.** They fail to compile until `Draw` has a default. Then they show 0 models.
- [ ] **Step 3: Implement** as in Files and Interfaces. `RunStopped` doesn't describe the world. The stopped-by-Update path in `RunOnce` does, as now.
- [ ] **Step 4: Run the fast suite.** It passes. Grep the repo for `view.Models(` and `.Models(world)`: there are no matches outside `docs/superpowers`.
- [ ] **Step 5: Update the docs.** In `ecs.md` and `views.md`, entities draw themselves, `Draw` is optional and is for frame-only things, and `TransformOf` is described. Update `design.md`'s frame-order row and `getting-started.md` if it shows `view.Models`.
- [ ] **Step 6: Commit** `feat!: draw the world before Draw and make Draw optional`.

### Task 3: Static entities are described once

**Files:**
- Modify: `src/Super/World/EntitySlots.cs` and `src/Super/World/World.cs` (bump a static version when an entity whose archetype, old or new, has `Static` is moved, freed or made). This is `internal long StaticVersion` on `ArchetypeTable`, so it doesn't count against World's member limit.
- Create: `src/Super/World/WorldChanges.cs`, with the extension `public static void Changed(this World world, Entity entity)`, which bumps the version when the entity is static and does nothing otherwise.
- Modify: `src/Super/World/WorldFrame.cs`. Static archetypes are drawn into a kept `List<ModelDraw>` only when `StaticVersion` changed, and replayed with `AddRange` otherwise, the way `View.Cached` does. The list lives on a `WorldFrameCache`, which `View` holds as `internal WorldFrameCache Frame { get; } = new();`.
- Test: `tests/Super.Tests/World/StaticCacheTests.cs`, plus `tests/Super.Tests/World/StaticBenchmarkTests.cs` (Slow, `DisableParallelization`)

**Interfaces:**
- Produces: `World.Changed(Entity)`, and `ArchetypeTable.StaticVersion`.

- [ ] **Step 1: Write the failing tests.**
  - `A_static_entity_moved_in_place_stays_until_Changed`: spawn a static cube and draw a frame. Write `world.Get<Transform>(e).Position = (5,0,0)` and draw again: x is 0. Call `world.Changed(e)` and draw: x is 5.
  - `A_static_entity_spawned_later_appears`: there are 2 models after the second spawn.
  - `A_destroyed_static_entity_goes`: there are 0 models after the destroy.
  - `Removing_Static_lets_it_move`: after `Remove<Static>`, a moving entity blends again.
- [ ] **Step 2: Run them.** The first fails, because x is 5 at once.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Write the benchmark.** `Fifty_thousand_static_pieces_describe_no_slower_than_Cached`: 50,000 static cubes in a grid. Take the median of 200 frames of `WorldFrame.Describe`, against the same 50,000 drawn through `view.Cached` once and replayed. Assert world ≤ 1.5 × cached, and print both numbers for the PR. Steady frames allocate 0 bytes (`GC.GetAllocatedBytesForCurrentThread`).
- [ ] **Step 5: Run StaticCacheTests and the benchmark.** They pass. Record the numbers in the ledger.
- [ ] **Step 6: Update the docs.** In `ecs.md` beside `Static`, say to call `world.Changed(entity)` after changing a static entity in place. Add a `design.md` row on the static cache.
- [ ] **Step 7: Commit** `feat: describe static entities once and add world.Changed`.

### Task 4: Camera entities

**Files:**
- Create: `src/Super/World/WorldCameras.cs`, with the internal `LastCamera` component (`Previous`, `HasPrevious`) and the extension `public static Camera? ActiveCamera(this World world)`
- Create: `src/Super/World/Newest.cs`, an internal record of the order each one-per-frame type was given. It hooks into `Reactions` internally, so it doesn't use the public `OnAdded`. It keeps one `List<Entity>` per type, and `Newest(int type)` gives the newest live holder.
- Modify: `RequiredComponents` (`Camera` requires `LastCamera` with `replace: true`), `LastTicks.Remember` (copies `Camera` into `LastCamera`), `WorldMotion.Teleport` (on an entity with a `Camera`, it also sets `LastCamera` to the current value; add an overload `Teleport(this World, Entity, Camera)`), and `WorldFrame` (the newest camera, blended with `Camera.Lerp` and placed by its entity's `WorldMatrix` when it has a `Transform` or `Parent`, goes to `view.Camera` before Draw)
- Test: `tests/Super.Tests/World/CameraEntityTests.cs`

**Interfaces:**
- Consumes: `WorldFrame.WorldMatrix` (Task 2).
- Produces: `Newest.Of<T>(World)`, used by Task 5's frame settings. `World.ActiveCamera()` gives the newest camera on this tick, in the world, unblended, or null when there is none. `World.Teleport(Entity, Camera)`.

- [ ] **Step 1: Write the failing tests.**
  - `A_camera_entity_is_the_frame_camera`.
  - `A_camera_written_each_tick_is_blended`: position x goes 0, then 4. At blend 0.25 the shot's x is 1.
  - `A_new_camera_shows_where_it_is_on_its_first_frame`.
  - `A_teleported_camera_cuts`.
  - `The_newest_camera_wins`.
  - `Removing_the_newest_camera_falls_back_to_the_one_before`.
  - `A_camera_with_a_parent_follows_it`: the parent is at x 10, and the camera's position (0,2,-5) and target (0,0,0) become (10,2,-5) and (10,0,0).
  - `Draw_overrides_the_world_camera`.
  - `ActiveCamera_reads_the_camera_in_Update`.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run CameraEntityTests and the fast suite.** They pass. Check that a cutscene still wins: `Director` sets the camera after Draw, unchanged.
- [ ] **Step 5: Update the docs.** In `cameras.md`, a camera is an entity, with the rig pattern `world.Get<Camera>(camera) = rig.Current;`, `ActiveCamera` and the parent-follow. Add a `design.md` row on `Newest` and `LastCamera`.
- [ ] **Step 6: Commit** `feat: draw the frame through the newest Camera entity`.

### Task 5: Lights and the frame's settings as components

**Files:**
- Rename: `src/Super/SunLight.cs` becomes `Sun` (`record struct Sun(Vector3 Direction, Vector3 Color, bool CastsShadows = false, LightMode Mode = LightMode.Mixed)`). There are 6 uses.
- Create: `src/Super/World/FrameSettings.cs`, with `public readonly record struct Sky(SkySettings Settings)`, `Shadows(float Radius, int Cascades = 1, float SunSize = ViewLights.DefaultSunSize, float Contact = ShadowSettings.DefaultContactLength)`, `AmbientOcclusion(float Radius = OcclusionSettings.DefaultRadius, float Strength = OcclusionSettings.DefaultStrength)`, `Finish(PostSettings Settings)` and `Outlines(Vector3? Color = null, float Thickness = …, float FadeStart = …, float FadeEnd = …)`, taking the defaults from the matching view calls.
- Modify: `src/Super/PointLight.cs` and `src/Super/SpotLight.cs`, doc comments only. On an entity, `Position` and `Direction` count from the entity's `Transform` (through `WorldMatrix`), so `default` sits at the entity and points along its +z (`SpotLight` defaults its direction to the entity's forward when `Direction` is zero). In `view.Lights` they stay in the world. One type serves both.
- Modify: `src/Super/World/WorldFrame.cs`. Each `Sun` goes through `view.Lights.Sun`, every `PointLight` and `SpotLight` through `view.Lights.Point` and `Spot` keyed by the entity's ID, and the newest `Sky`, `Shadows`, `AmbientOcclusion`, `Finish` and `Outlines` (through `Newest`) through their view calls.
- Test: `tests/Super.Tests/World/LightEntityTests.cs`, `tests/Super.Tests/World/FrameSettingsTests.cs`

**Interfaces:**
- Consumes: `Newest.Of<T>` (Task 4), `WorldFrame.WorldMatrix` (Task 2).
- Produces: the component types named above.

- [ ] **Step 1: Write the failing tests.**
  - `A_point_light_sits_at_its_entity`: the entity is at (3,1,0), so `DrawnLights[0].Position` is (3,1,0) and `Key` is the entity's ID.
  - `A_spot_light_points_along_its_entity`: the entity is turned 90° about y, so the light's direction is +x.
  - `A_sun_entity_lights_the_frame`.
  - `The_newest_sky_wins`.
  - `A_bad_shadow_radius_throws_with_the_view_message`: `Shadows(0f)`, with the message "The shadow radius must be more than zero."
  - `Draw_overrides_the_world_sky`.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and the fast suite.** They pass.
- [ ] **Step 5: Update the docs.** In `rendering.md`, each light and setting gets its component form beside the view call. Update the `ecs.md` component table.
- [ ] **Step 6: Commit** `feat!: lights, sky, shadows, occlusion, finish and outlines as components, and rename SunLight to Sun`.

### Task 6: Ghost and Hidden

**Files:**
- Modify: `src/Super/World/WorldFrame.cs`. An entity with `Rendition.Ghost` as a component draws through the ghost path of `View.Model`. An entity that has `Hidden`, or has it anywhere up its `Parent` chain, is skipped for models, lights and emitters. Check its archetype first, and walk the parents only for entities with a `Parent`.
- Create: `public readonly record struct Hidden;` in `src/Super/World/Components.cs`
- Modify: `src/Super/World/Audio/SoundSync.cs`, which skips hidden entities, so their loops fade out as if the component were gone
- Test: `tests/Super.Tests/World/GhostHiddenTests.cs`, `tests/Super.Tests/World/SoundTests.cs`

**Interfaces:**
- Produces: `internal static bool WorldFrame.IsHidden(World, Entity)`, which SoundSync uses.

- [ ] **Step 1: Write the failing tests.**
  - `A_ghost_entity_draws_as_a_ghost`: `Drawn[0].Ghost` is the given value.
  - `A_hidden_entity_is_not_drawn`.
  - `A_hidden_parent_hides_its_children_lights_and_emitters`: a grandchild at depth 2.
  - `A_hidden_entity_is_not_heard`.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and the fast suite.** They pass.
- [ ] **Step 5: Update the docs** in `ecs.md` and `audio.md`.
- [ ] **Step 6: Commit** `feat: Ghost and Hidden components`.

### Task 7: Emitters that loop

**Files:**
- Create: `src/Super/World/Emitter.cs`, with `public readonly record struct Emitter(ParticleEffect Effect, float Every = 1f)`. A new burst starts every `Every` seconds of game time, and each plays its whole `Duration`, so `ceil(Duration / Every)` bursts overlap. This is the general form of `CryptFire.Draw`. The burst for start `n` is keyed `HashCode.Combine(entity.Id, n)` and drawn at the entity's `WorldMatrix` with age `now - n * Every`, where `now` is `ViewClock.Now`.
- Modify: `src/Super/World/WorldFrame.cs`, which draws the emitters
- Test: `tests/Super.Tests/World/EmitterTests.cs`

**Interfaces:**
- Consumes: `WorldFrame.WorldMatrix`, `WorldFrame.IsHidden`.

- [ ] **Step 1: Write the failing tests.**
  - `An_emitter_shows_particles_at_its_entity`.
  - `An_emitter_never_runs_out`: there are particles at 0.5, 10.5 and 100.5 seconds.
  - `An_emitter_looks_the_same_at_the_same_time`: two runs, seed 1, compare positions.
  - `Every_must_be_more_than_zero`: `Every = 0` throws `ArgumentOutOfRangeException` with "An emitter's Every must be more than zero seconds."
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and the fast suite.** They pass.
- [ ] **Step 5: Update the docs.** Add a "Particles that loop" section in `views.md`, or wherever particles are taught.
- [ ] **Step 6: Commit** `feat: Emitter plays a particle effect for as long as it's there`.

### Task 8: Check the whole stage

**Files:**
- Modify: `roe-baseline.json`, only if a finding was fixed or moved.

- [ ] **Step 1: Run `dotnet roe`.** There is no dead code, there are no new findings, and there are 187 duplicate groups or fewer.
- [ ] **Step 2: Run `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`.** It passes. Run `dotnet test tests/Rendition.Gpu.Tests -c Release`: only the two known main failures.
- [ ] **Step 3: Take screenshots** of Reef, Rooms, Cannonball and Forest with `--screenshot --validate --seed 1 --ticks 300 --size 1280x720`, on main and on the branch. Look at each: they show the same picture with 0 validation messages.
- [ ] **Step 4: Profile Reef** with `--profile`. It is within noise of main.
- [ ] **Step 5: Search the docs.** Grep `docs/` for `view.Models`, `SunLight` and "Draw describes everything", with no stale text left.
- [ ] **Step 6: Commit** whatever the check fixed, and record Task 8 in the ledger.
