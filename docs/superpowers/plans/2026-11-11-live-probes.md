# Live probes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the load-time light probe volume with cascades of probes centred on the camera that update a fixed number of probes a frame. Updates are rasterized and lit by the sun, the sky, point and spot lights and the probes themselves on every GPU, and traced with 128 rays a probe where the GPU can trace. There is no hitch at load or while playing, and every example looks at least as good as before.

**Architecture:** Two pure classes do the bookkeeping and are unit tested without a GPU. `ProbeCascades` holds the cascade layout, snapping and toroidal scrolling. `ProbeScheduler` picks which probes update each frame, by staleness, distance and lighting changes, within a count budget. `ProbeVolume` becomes a set of 2D array atlases, with one layer per cascade plus one for the optional fixed grid. Each frame the renderer records, in the frame's own command buffer, one of two update paths:

- Rasterized: a forward multiview cube capture of the batch, lit by a shared `probe_light.glsl`.
- Traced: `probe_trace.comp`, which uses the plan 1 hit path and the same `probe_light.glsl`.

Either path writes new tiles into a staging buffer. A blend dispatch then folds them into the atlases with hysteresis. The lighting pass looks up the finest cascade that holds the point, falling back to coarser cascades and then the sky. `Graphics.ProbeQuality` sets the probes per cascade and the updates per frame.

**Tech Stack:** C# (.NET 10), Silk.NET.Vulkan 2.23 (dynamic rendering, multiview from Vulkan 1.1 core), GLSL compiled by glslc for Vulkan 1.3 (`GL_EXT_multiview`, `GL_EXT_ray_query` for the traced path only), xunit 2.9, ffmpeg for the shots.

**Spec:** `docs/superpowers/specs/2026-11-11-lighting-design.md`. The relevant sections are "Live probes", "Who can trace", "Switches" under "At runtime", "Order" (step 2) and "How it's tested". Roadmap: `docs/superpowers/plans/2026-11-11-lighting-roadmap.md`, plan 3. The traced path uses the layer from `2026-11-11-ray-tracing-foundation.md`.

## Global Constraints

- Every player must see working probes without ray tracing. The rasterized path ships first (Tasks 1–6) and works on its own. The traced path (Task 7) is chosen automatically when `Renderer.CanTrace`, and players get no switch between the two paths.
- No hitches. Nothing on the frame path may call `DeviceWaitIdle`, `QueueWaitIdle` or wait on a fence for probe work, and probe work is recorded in the frame's own command buffer. A frame never updates more probes than its quality allows.
- Storage keeps today's octahedral tiles, the 8×8 RGBA16F irradiance tile and the 16×16 RG16F depth-moments tile, each with a 1-texel border. It also keeps the Chebyshev leak test, the more-than-half-back-faces "inside" rule and the lookup bias, so a probe reads the same way it does today.
- High quality uses the spec's layout: four cascades at 0.5, 1, 2 and 4 m spacing, each 32 × 16 × 32 probes (x, y, z), scrolling with the camera. `view.LightProbes(min, max, spacing)` stays, with the same validation and 4,096-probe limit, and adds a fixed grid that wins inside its box.
- `Graphics.LightProbes` stays the on/off switch. `Graphics.ProbeQuality` (`Low`, `Medium`, `High`) defaults to `High`, the best look, and `Graphics.Fastest()` sets it to `Low`.
- Updates are scheduled by frame count, never by wall-clock time. Traced ray directions are rotated by a seed from the frame number. The same `--seed`, `--ticks` and `--size` must therefore give the same picture.
- Probe hits and captures read the material table the way plan 1's hit path does: base colour, emissive and cut-outs. They skip custom surface shaders, world variation, wear, dirt and puddles. The traced path includes `trace.glsl` and doesn't copy it.
- Draws left out of probe updates are the same as `TraceScene.Collect` leaves out on both paths: ghosts, skinned draws and swimming draws. Wind-swayed draws are captured at rest.
- With `Graphics.LightProbes` off, nothing is allocated or recorded, and the lighting falls back to sky irradiance as today.
- Vulkan 1.3 is the floor. Multiview is core and every conformant GPU supports at least 6 views, but `Supported` still checks `multiview` and `maxMultiviewViewCount >= 6`.
- Code style: braces on every control statement. Conventional commits, with no Co-Authored-By lines and no Claude as author.
- Docs change in the same commit as the code: `rendering.md` ("Bounced light" and the Graphics settings table), `design.md` ("Light probes" and the limitations list), `command-line.md` (`--no-trace` and `--debug-view probes`) and `views.md` (`LightProbes`).
- Test rules from `CLAUDE.md` apply:
  - Break the code to see each test fail, then revert.
  - Use no fixed sleeps.
  - GPU tests go in `[Collection(GpuCollection.Name)]` with `[Trait("Speed","Slow")]`.
  - Check exception messages.
  - Rebuild rather than use `--no-build`.

## Review Focus

1. Hitches. No wait on the frame path, and no frame over its probe budget, including the first frame, a teleport and a moved light. Tested in Task 3 (`A_frame_never_hands_out_more_probes_than_the_budget`) and Task 6 (`Moving_the_light_changes_the_probe_and_no_frame_goes_over_budget`).
2. Toroidal addressing must agree between C# and GLSL. A mismatch looks right while the camera stands still and wrong once it moves. Tested in Task 2 (`A_slot_and_its_world_position_round_trip_after_a_scroll`) and Task 5 (`Scrolling_the_camera_keeps_the_probes_it_did_not_leave`).
3. Leaks. A probe in a wall, or a closed room under a bright sky, must read dark on both paths, and a probe nobody has captured yet must get no weight rather than read black. Tested in Task 5 (`A_closed_box_reads_black_under_a_bright_sky_rasterized`), Task 7 (the same test as a theory over both paths) and Task 4 (`A_probe_not_yet_captured_gets_no_weight`).
4. Feedback. Bounce reads the atlases while updates write them. Reading a tile mid-write, or energy that grows with each bounce, shows as flicker or a room that brightens forever. Tested in Task 5 (`A_white_room_lit_by_one_light_settles_and_does_not_grow_rasterized`) and Task 7 (the same test over both paths).
5. Repeatable shots. The same seed, ticks and size must give the same image, and a screenshot must wait until the near probes have settled. Tested in Task 3 (`The_same_frames_give_the_same_batches`), Task 6 (`Screenshot_waits_for_settled_probes_up_to_the_cap`) and Task 9, where every example is shot twice and the files compared.

---

### Task 1: Baseline shots of every example

Nothing changes in code. The "before" shots have to exist before anything is touched.

**Files:**
- Create: `shots/baseline/probes-<example>.png` for the 18 examples (Archer, Bricks, ColorCube, Courtyard, Crafting, Crypt, Dust, Forest, Fox, Gallery, Glass, Hamlet, Reef, Rooms, Shapes, Terrain, Transitions, Wax). These are not committed: `shots/` is untracked.
- Create: `shots/baseline/probes-<example>.txt`, the `--validate` output, to count messages.

**Interfaces:** none.

- [ ] **Step 1: Build in Release**

Run: `dotnet build -c Release`
Expected: build succeeds with 0 errors.

- [ ] **Step 2: Shoot every example on this machine's default path**

Run, in Git Bash from the repo root:

```bash
for e in Archer Bricks ColorCube Courtyard Crafting Crypt Dust Forest Fox Gallery Glass Hamlet Reef Rooms Shapes Terrain Transitions Wax; do
  n=$(echo "$e" | tr 'A-Z' 'a-z')
  dotnet run -c Release --project examples/$e -- --screenshot shots/baseline/probes-$n.png --validate --seed 1 --ticks 300 --size 1280x720 > shots/baseline/probes-$n.txt 2>&1
done
```

Expected: 18 PNGs and 18 text files, all written. The same `--seed 1 --ticks 300 --size 1280x720` is used for every after shot in Task 9.

- [ ] **Step 3: Shoot Courtyard and Gallery with `--no-trace` as well**

These are the two examples that use probes today, so their baselines also stand for the rasterized path. Run the same command with `--no-trace` to `shots/baseline/probes-courtyard-notrace.png` and `shots/baseline/probes-gallery-notrace.png`.

- [ ] **Step 4: Open every PNG and note anything already broken**

Read each image. Write a list in `shots/baseline/notes.txt` of what each one shows: which examples have bounced light today (only Courtyard and Gallery), and any validation message counts above zero. Task 9 compares against this list.

No commit. `shots/` is not tracked.

---

### Task 2: Cascade layout and scrolling

**Files:**
- Create: `src/Rendition/Passes/ProbeCascades.cs`
- Modify: `src/Rendition/Types.cs` (add `public enum ProbeQuality { Low, Medium, High }`)
- Test: `tests/Super.Tests/ProbeCascadesTests.cs`
- Docs: `docs/design.md` ("Light probes": cascade shape, snapping and toroidal slots)

**Interfaces:**
- Produces: `public enum ProbeQuality { Low, Medium, High }` in `Rendition`.
- Produces: `internal readonly record struct ProbeCascadeLayout(float Spacing, int CountX, int CountY, int CountZ)`, with `int Total`.
- Produces: `internal sealed class ProbeCascades`, with:
  - `public const int Count = 4`
  - `public static ProbeCascadeLayout[] Layouts(ProbeQuality quality)`:
    - High is 32×16×32;
    - Medium is 24×12×24;
    - Low is 16×8×16;
    - the spacing is always 0.5, 1, 2 and 4 m.
  - `public ProbeCascades(ProbeQuality quality)`
  - `public Int3 Origin(int cascade)`: the cell index of the cascade's minimum corner.
  - `public ScrollResult Move(Vector3 camera)`: snaps each cascade so the camera sits in its middle cell. It returns, per cascade, the range of slots that are new, so the scheduler marks them never captured.
  - `public int Slot(int cascade, Int3 cell)`: toroidal, `((cell mod count) + count) mod count`, x fastest.
  - `public Vector3 Position(int cascade, int slot)`: the world position of the probe currently in that slot.
  - `public int Finest(Vector3 point)`: the finest cascade whose box holds the point at least one cell inside its edge, or -1. This mirrors the shader's choice in Task 4.
- Produces: `internal readonly record struct Int3(int X, int Y, int Z)`, unless one already exists in Rendition.
- Produces: `internal readonly record struct ScrollResult(...)`, with `IReadOnlyList<int> NewSlots(int cascade)` (or a compact slab form) and `bool Teleported(int cascade)`.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `High_quality_has_four_cascades_of_32_by_16_by_32_at_half_one_two_and_four_metres` | `Layouts(High)` spacings equal `[0.5, 1, 2, 4]` and every count is `(32, 16, 32)` |
| `Medium_and_low_cover_less_ground_at_the_same_spacings` | The spacings match High's. Low's first cascade is 8 m × 4 m × 8 m (hand-worked: 16 × 0.5) |
| `A_cascade_is_centred_on_the_camera_snapped_to_its_spacing` | Camera at (10.3, 2.2, -4.9), High. Cascade 1 (1 m): `Origin` is (-6, -6, -21). Hand-worked: snapped cell (10, 2, -5) minus half counts (16, 8, 16) |
| `Moving_the_camera_less_than_one_spacing_moves_no_cascade` | A move from (0.1, 0, 0) to (0.4, 0, 0) gives no new slots in any cascade |
| `Moving_one_spacing_along_x_leaves_one_slice_of_new_probes` | High, a move of +1 m: cascade 1 has exactly 16 × 32 = 512 new slots, all with the same x cell. Cascade 0 (0.5 m) has 2 × 512. Cascades 2 and 3 have 0 |
| `Moving_further_than_a_cascade_is_wide_makes_every_probe_in_it_new` | A move of +20 m: cascade 0 (16 m wide) is `Teleported` with all 16,384 slots new. Cascade 3 (128 m) has 5 × 16 × 32 new slots after the snap (hand-worked) |
| `A_slot_and_its_world_position_round_trip_after_a_scroll` | For 12 seeded random camera paths of 30 moves each: for every cascade and 64 random cells inside the current box, `Position(c, Slot(c, cell)) == cell * spacing`, and no two cells in the box share a slot |
| `The_finest_cascade_that_holds_a_point_one_cell_inside_is_chosen` | Camera at the origin, High. A point at (7.6, 0, 0) gives 1 (cascade 0's half-width is 8 m, less one 0.5 m cell is 7.5). A point at (3, 0, 0) gives 0. A point at (200, 0, 0) gives -1 |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~ProbeCascadesTests`
Expected: compile error, `ProbeCascades` is not defined.

- [ ] **Step 3: Implement `ProbeQuality`, `ProbeCascades` and `Int3`**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~ProbeCascadesTests`
Expected: 8 passed.

- [ ] **Step 5: Prove the round-trip test can fail**

Change `Slot` to `cell mod count` without the `+ count`, so negative cells break. Run Step 4: `A_slot_and_its_world_position_round_trip_after_a_scroll` fails. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Rendition/Passes/ProbeCascades.cs src/Rendition/Types.cs tests/Super.Tests/ProbeCascadesTests.cs docs/design.md
git commit -m "feat: lay out probe cascades around the camera with toroidal slots"
```

---

### Task 3: The scheduler

**Files:**
- Create: `src/Rendition/Passes/ProbeScheduler.cs`
- Test: `tests/Super.Tests/ProbeSchedulerTests.cs`
- Docs: `docs/design.md` ("Light probes": how probes are picked each frame)

**Interfaces:**
- Consumes: `ProbeCascades`, `ScrollResult`, `ProbeQuality` (Task 2).
- Produces: `internal readonly record struct ProbeBudget(int Raster, int Traced)` and `public static ProbeBudget For(ProbeQuality q)`:
  - Low: 8 and 256;
  - Medium: 16 and 512;
  - High: 32 and 1024.
- Produces: `internal readonly record struct ProbeId(int Cascade, int Slot)`. Cascade 4 is the fixed grid.
- Produces: `internal sealed class ProbeScheduler`, with:
  - `public ProbeScheduler(ProbeCascades cascades, int fixedGridProbes)`
  - `public void Scrolled(ScrollResult scroll)`: new slots become never captured.
  - `public void LightChanged(Vector3 center, float range)`: probes within range are dirty.
  - `public void EverythingChanged()`: on a sun, sky or environment change, every probe is dirty, but its value stays in use.
  - `public IReadOnlyList<ProbeId> Next(int budget, Vector3 camera, Vector3 forward)`: never more than `budget`.
  - `public void Updated(IReadOnlyList<ProbeId> batch)`
  - `public bool Captured(ProbeId id)`
  - `public bool Settled`: every probe in cascades 0 and 1, and the fixed grid, has been captured since its last change.
  - `public long Frame`
- Priority, highest first:
  1. never captured;
  2. dirty;
  3. stale.

  Within each tier, nearer the camera and finer cascades go first, and probes in front of the camera (dot with `forward` > 0) score higher. Staleness is the number of frames since the probe was last updated, so every probe is refreshed within a bounded period. Ties are broken by `ProbeId` so the order is deterministic.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `A_frame_never_hands_out_more_probes_than_the_budget` | 15 seeds of random camera paths, with teleports, `LightChanged` and `EverythingChanged` mixed in, 400 frames each, at Low, Medium and High raster budgets: every `Next(...).Count <= budget`, and no `ProbeId` appears twice in a batch |
| `New_probes_near_the_camera_go_before_stale_far_ones` | After everything is captured and 1,000 frames pass, scroll by +1 m. The first batch is all new slots from cascade 0, the ones nearest the camera |
| `Every_probe_is_updated_again_within_its_refresh_period` | Low, a camera at rest, a budget of 8. Over `ceil(total / 8) * 2` frames, every probe is updated at least twice, so none starves |
| `A_moved_light_marks_its_old_and_new_range_dirty_first` | After settling, `LightChanged` at A and at B (each 2 m range). The next batches up to the count of dirty probes hold only probes within 2 m of A or B |
| `A_sun_or_sky_change_keeps_every_probe_captured` | After `EverythingChanged`, `Captured` is still true for every probe, `Settled` is false, and it is true again once every near probe has updated |
| `Settled_waits_for_the_two_finest_cascades_and_the_fixed_grid` | A fixed grid of 27 probes. `Settled` stays false until cascades 0 and 1 and the grid are captured, and is true while cascades 2 and 3 are still uncaptured |
| `The_same_frames_give_the_same_batches` | Two schedulers fed the same 200-frame camera path give equal batches every frame |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~ProbeSchedulerTests`
Expected: compile error, `ProbeScheduler` is not defined.

- [ ] **Step 3: Implement `ProbeBudget`, `ProbeId` and `ProbeScheduler`**

Use a priority structure that doesn't sort 65,536 probes a frame. Use per-tier buckets: never captured, dirty and stale, each ordered by distance ring. Rescore lazily when the camera crosses a cell. `ProbeSchedule` stays in `ProbeGrid.cs` while the renderer still uses it. Task 5 deletes it and its tests.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~ProbeSchedulerTests`
Expected: 7 passed.

- [ ] **Step 5: Prove the budget and starvation tests can fail**

First, make `Next` return `budget + 1` probes when a teleport happens. `A_frame_never_hands_out_more_probes_than_the_budget` fails. Revert.

Then make staleness never count, so only never-captured and dirty probes are chosen. `Every_probe_is_updated_again_within_its_refresh_period` fails. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Rendition/Passes/ProbeScheduler.cs tests/Super.Tests/ProbeSchedulerTests.cs docs/design.md
git commit -m "feat: pick probes to update by staleness, distance and lighting changes"
```

---

### Task 4: Storage and the cascade lookup

**Files:**
- Modify: `src/Rendition/Vulkan/ProbeVolume.cs`. Its atlases become 2D arrays with five layers: four cascades and the fixed grid. It adds the staging tile buffer and the blend pipeline, and is sized from `ProbeQuality`.
- Create: `src/Rendition/Shaders/probe_blend.comp`
- Create: `src/Rendition/Shaders/probe_fold.glsl`, the fold maths moved out of `probe_fold.comp`. The traced path reuses it.
- Modify: `src/Rendition/Shaders/probe_fold.comp` (includes `probe_fold.glsl` and writes to the staging buffer instead of the atlas)
- Modify: `src/Rendition/Shaders/probes.glsl` (adds cascade selection and fallback, and gives an uncaptured probe zero weight)
- Modify: `src/Rendition/Shaders/common.glsl` (frame uniforms: `probeCascadeOrigin[4]`, as xyz cell and w spacing; `probeCascadeCounts`; the fixed grid's `lightProbeMin` and `lightProbeCounts`, which stay)
- Modify: `src/Rendition/Renderer.cs` (`WriteUniforms`)
- Create: `src/Rendition/Shaders/probe_sample.comp` and an internal `Renderer.SampleProbeIrradiance` for tests
- Modify: `src/Rendition/Passes/ProbeGrid.cs` (the `Weight` mirror gains the uncaptured rule)
- Test: `tests/Super.Tests/ProbeGridTests.cs`, `tests/Rendition.Gpu.Tests/ProbeStorageTests.cs`
- Docs: `docs/design.md` ("Light probes": the layers, the valid flag, the lookup order, memory per quality)

**Interfaces:**
- Consumes: `ProbeCascades`, `ProbeQuality`.
- Produces: `ProbeVolume(VulkanContext, ProbeQuality)`, with:
  - `void Resize(ProbeQuality)`: waits for the device. It runs only on a settings change and never on the frame path.
  - `void RecordBlend(CommandBuffer cmd, IReadOnlyList<ProbeId> batch, float alpha)`
  - `Buffer StagingTiles`
  - `const int FixedGridLayer = 4`
- Valid flag: an irradiance tile's alpha is 1 once captured and 0 while cleared. The lookup multiplies the trilinear weight by it, so an uncaptured probe gets no weight and the point falls back to the next cascade. The inside marker stays as today: -1 in the depth tile's first corner.
- Hysteresis: `alpha = 1` for a probe's first capture. After that it is 0.5 rasterized and 0.25 traced (Task 7).
- Lookup order in `probeIrradiance(p, n, v)`:
  1. the fixed grid if `p` is inside it;
  2. otherwise the finest cascade from the same rule as `ProbeCascades.Finest`, blended toward the next coarser one over its outermost cell;
  3. if a cascade's total weight is under 1e-3, the next coarser cascade;
  4. then -1, which `softLight` turns into sky irradiance as today.
- Produces: `internal Vector3[] Renderer.SampleProbeIrradiance(ReadOnlySpan<(Vector3 Point, Vector3 Normal)> samples)`. It dispatches `probe_sample.comp`, which calls the real `probeIrradiance`, and reads the result back. It is for tests only, and waits on its own fence off the frame path.
- Produces: `internal Renderer.WriteProbeTileForTest(ProbeId id, Vector3 irradiance)`, so storage can be tested before any capture exists.

- [ ] **Step 1: Write the failing tests**

Unit, in `ProbeGridTests`:

| Test | Asserts |
|---|---|
| `A_probe_not_yet_captured_gets_no_weight` | `ProbeGrid.Weight(..., captured: false)` is 0 for a probe that would otherwise get nearly all the weight (the setup from `A_probe_in_front_of_the_surface_on_its_point_gets_nearly_all_the_weight`) |

GPU, in `ProbeStorageTests` (`[Collection(GpuCollection.Name)]`, `[Trait("Speed","Slow")]`):

| Test | Asserts |
|---|---|
| `A_written_tile_reads_back_through_the_lookup` | Write (0.2, 0.4, 0.6) to the 8 probes of cascade 0 around a point. `SampleProbeIrradiance` at that point is within 2% of (0.2, 0.4, 0.6). `gpu.ValidationErrors` is empty |
| `A_point_whose_fine_probes_are_uncaptured_falls_back_to_the_coarser_cascade` | Write only cascade 1's probes with (1, 0, 0). A sample inside cascade 0 reads within 2% of (1, 0, 0), not black |
| `A_point_with_no_captured_probes_reads_the_sky` | Write nothing, sky irradiance (0, 0, 0.5). The sample is -1, which the lighting pass turns into sky |
| `The_fixed_grid_wins_inside_its_box` | Set `FrameView.LightProbes` over a 4 m box. Write the grid with (0, 1, 0) and cascade 0 with (1, 0, 0). Inside the box the sample reads green; outside it, red |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~ProbeGridTests` then `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~ProbeStorageTests`
Expected: compile errors (`captured` parameter, `SampleProbeIrradiance`).

- [ ] **Step 3: Implement the layered atlases, staging, blend, the lookup and the sample shader**

Keep `CaptureLightProbes` working in this commit: it writes through staging and blend with `alpha = 1` into the fixed-grid layer, so Courtyard and Gallery look as they did. Task 5 removes it.

- [ ] **Step 4: Run the tests and see them pass**

Same commands as Step 2. Expected: all pass, and validation is empty.

- [ ] **Step 5: Prove the fallback test can fail**

Remove the "next coarser cascade when weight < 1e-3" branch. `A_point_whose_fine_probes_are_uncaptured_falls_back_to_the_coarser_cascade` fails, reading -1. Revert.

- [ ] **Step 6: Check Courtyard still looks as it did**

Run: `dotnet run -c Release --project examples/Courtyard -- --screenshot shots/task4-courtyard.png --validate --seed 1 --ticks 300 --size 1280x720`
Expected: 0 validation messages, and the image matches `shots/baseline/probes-courtyard.png` by eye.

- [ ] **Step 7: Commit**

```bash
git add src/Rendition docs/design.md tests/Super.Tests/ProbeGridTests.cs tests/Rendition.Gpu.Tests/ProbeStorageTests.cs
git commit -m "feat: store probes in cascade layers with a lookup that falls back to coarser ones"
```

---

### Task 5: Rasterized updates in the frame, lit by every light

This task replaces `CaptureLightProbes` and its load-time hitch. The rasterized path is complete after it.

**Files:**
- Create: `src/Rendition/Passes/ProbeCapturePass.cs`
- Create: `src/Rendition/Shaders/probe_capture.vert`, `src/Rendition/Shaders/probe_capture.frag`
- Create: `src/Rendition/Shaders/probe_light.glsl`, the radiance at a surface point for probe updates. Shared with Task 7.
- Create: `src/Rendition/Passes/ProbeSunShadow.cs`, a camera-centred orthographic sun shadow map that covers the largest cascade.
- Modify: `src/Rendition/Renderer.cs`:
  - Delete `CaptureLightProbes`, `CaptureFaces`, `_lightProbeTargets`, `_capturingLightProbes`, `LightingHash` and the capture branch in `WriteLocalLights`.
  - Add `RecordProbeUpdate(cmd, view, draws)` after the shadow pass and before lighting.
  - Change `ReflectionLightingHash` to use `ProbeScheduler.Settled`.
- Modify: `src/Rendition/Shaders/lighting.frag` (drop the capture-mode branches: `probeCapture`, `NOTHING_DRAWN_DISTANCE`, black inside the box)
- Modify: `src/Rendition/Shaders/common.glsl` (drop `probeCapture`)
- Modify: `src/Rendition/Passes/GBufferPass.cs` (drop the `allTwoSided` capture case)
- Modify: `src/Rendition/Vulkan/ProbeVolume.cs` (`Capture` becomes a 16×16 array target with 6 layers per batch slot. `FaceSize` becomes 16)
- Modify: `src/Rendition/Shaders/probe_fold.comp` (reads the array target. `SAMPLES` stays `6 * 16 * 16`)
- Modify: `src/Rendition/Passes/ProbeGrid.cs` (delete `ProbeSchedule`)
- Test: `tests/Rendition.Gpu.Tests/LiveProbeTests.cs`, `tests/Rendition.Gpu.Tests/ProbeScenes.cs`. In `tests/Super.Tests/ProbeGridTests.cs`, delete the four `ProbeSchedule` tests, from `A_new_box_captures_in_batches_through_two_rounds_and_is_ready_after_the_first` to `The_same_box_and_lighting_do_not_start_again`.
- Docs:
  - `docs/rendering.md`: rewrite "Bounced light" for live cascades, point and spot lights, no load hitch, and what `view.LightProbes` adds now;
  - `docs/design.md`: "Light probes" internals, and delete the limitation that probes are captured only when lighting changes, hitch, and have no point or spot light;
  - `docs/views.md`: `LightProbes` now adds a fixed grid on top of the cascades.

**Interfaces:**
- Consumes: `ProbeCascades`, `ProbeScheduler`, `ProbeBudget.Raster`, `ProbeVolume.RecordBlend`, `TraceScene.Collect`'s rule for which draws to leave out.
- Produces: `internal sealed class ProbeCapturePass`, with `Record(CommandBuffer cmd, IReadOnlyList<ProbeId> batch, ReadOnlySpan<DrawItem> draws)`, binding the same frame descriptor sets the lighting pass binds (uniforms, local lights, shadow atlas, probe atlases):
  - one `CmdBeginRendering` per probe with `viewMask = 0x3F` into that slot's 6-layer view;
  - draws outside 16 × the cascade's spacing are culled on the CPU, with no draw-indirect;
  - two-sided, with back faces writing a negative distance, so the fold's inside rule works as today.
- Produces: `probe_light.glsl`, which needs `PROBE_SUN_VISIBILITY(p, n)` and `PROBE_LIGHT_VISIBILITY(i, p, n)` defined by the includer. It provides:

  ```glsl
  vec3 probeRadiance(vec3 position, vec3 normal, vec3 albedo, vec3 emissive);
  ```

  The radiance is sun plus sky plus every local light within range (point and spot, cone included) plus `albedo * probeIrradiance(position, normal, normal)`, the bounce from the probes, plus `emissive`. In the rasterized path, the sun's visibility comes from `ProbeSunShadow`. A local light's visibility comes from its own shadow tile when it has one, and is 1 otherwise.
- Produces: `internal int Renderer.ProbeUpdatesLastFrame`, `internal bool Renderer.ProbesSettled`, and `public ProbeQuality Renderer.ProbeQuality { get; set; }` (resizes on change).
- Lighting changes reach the scheduler as follows:
  - a local light whose position, range, colour, cone or shadow flag changed calls `LightChanged` for its old and new places;
  - a sun or environment change (`envVersion`, sun direction or colour) calls `EverythingChanged`;
  - toggling `Graphics.LightProbes` back on clears the atlases and starts again.

`ProbeScenes` holds test scenes, built from `TestMeshes` and `GpuFixture.Matte`:

- `RedWall(bool lightIsSpot)`: black sky, no sun, a white floor, a red wall at x = 1, and one point light (or a spot aimed at the floor) at (0, 1.5, 0).
- `ClosedBox()`: a 2 m closed white box at the origin under a sky of radiance 4.
- `WhiteRoom()`: a 3 m white room with an opening, and one point light inside.

- [ ] **Step 1: Write the failing tests**

Each test runs on `new GpuFixture(disableRayTracing: true)` for the rasterized path, renders frames with the camera at the origin until `ProbesSettled` (at most 2,000 frames), checks `gpu.ValidationErrors` is empty, and asserts:

| Test | Asserts |
|---|---|
| `A_red_wall_beside_a_probe_tints_it_red_rasterized` | `RedWall(false)`: the sample at (0.7, 0.3, 0) with normal +x has `r > 2 * g`, `r > 2 * b` and `r > 0.01`. The same scene with a white wall has `r < 1.2 * g`, which proves the wall's colour is the cause |
| `A_spot_light_lights_the_probe_only_inside_its_cone_rasterized` | `RedWall(true)`, a 30° spot aimed at (0, 0, 0): a floor sample at (0, 0.1, 0), normal up, is at least 10 × brighter than one at (0, 0.1, -3) |
| `A_closed_box_reads_black_under_a_bright_sky_rasterized` | `ClosedBox()`: a sample at the box's centre is below 0.01 on every channel, and one outside the box is above 1 |
| `A_white_room_lit_by_one_light_settles_and_does_not_grow_rasterized` | `WhiteRoom()`: after settling, the sample at the room's centre is taken every 50 frames over 500 frames. The last reading is within 2% of the one 50 frames earlier, and no reading is above twice the first settled one |
| `Scrolling_the_camera_keeps_the_probes_it_did_not_leave` | `RedWall(false)`: settle, sample at (0.7, 0.3, 0), move the camera +1 m along z, render one frame, and sample again. The two are within 1% |
| `No_frame_on_the_load_path_waits_for_probes` | The first frame of a scene renders with `ProbeUpdatesLastFrame <= ProbeBudget.For(High).Raster`, and `ProbesSettled` is false after it. Probes no longer capture at load |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~LiveProbeTests`
Expected: compile error (`ProbesSettled`), or failures where only the fixed grid captures.

- [ ] **Step 3: Implement the capture pass, `probe_light.glsl`, the probe sun shadow, and the frame wiring**

`ProbeSunShadow` is redrawn when the sun's direction changes, when the camera moves more than an eighth of its extent, or every 240 frames, so moved draws show up. Redrawing it costs one shadow-map draw in that frame, the same work the main shadow pass does every frame, so it isn't a hitch.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~LiveProbeTests`
Expected: all 6 pass.

- [ ] **Step 5: Prove two tests can fail**

First, make `probeRadiance` skip local lights. `A_red_wall_beside_a_probe_tints_it_red_rasterized` fails, with no light at all. Revert.

Then make the capture write positive distances for back faces. `A_closed_box_reads_black_under_a_bright_sky_rasterized` fails. Revert.

- [ ] **Step 6: Run the whole suite and check for leftover capture code**

Run: `dotnet test`
Expected: all tests pass.

Run: `grep -rn "DeviceWaitIdle\|QueueWaitIdle" src/Rendition/Renderer.cs src/Rendition/Passes/ProbeCapturePass.cs src/Rendition/Vulkan/ProbeVolume.cs`
Expected: no match on the frame path. `Resize` and disposal are the only ones allowed.

Run: `grep -rn "probeCapture\|_capturingLightProbes\|CaptureLightProbes" src docs`
Expected: no matches.

- [ ] **Step 7: Shoot Courtyard on the rasterized path**

Run: `dotnet run -c Release --project examples/Courtyard -- --screenshot shots/task5-courtyard.png --validate --seed 1 --ticks 300 --size 1280x720 --no-trace`
Expected: 0 validation messages. Bounce is at least as strong as `shots/baseline/probes-courtyard-notrace.png`, and the lanterns now light it. Look at the image.

- [ ] **Step 8: Commit**

```bash
git add src/Rendition tests/Rendition.Gpu.Tests tests/Super.Tests/ProbeGridTests.cs docs/rendering.md docs/design.md docs/views.md
git commit -m "feat: update light probes a few a frame, lit by point and spot lights too"
```

---

### Task 6: `Graphics.ProbeQuality`, the shot settle, and the budget test

**Files:**
- Modify: `src/Super/Graphics.cs` (`ProbeQuality` property, default `High`, validated; `Fastest()` sets `Low`)
- Modify: `src/Super/Rendering/GameWindow.cs`:
  - `RendererSettings` returns `ProbeQuality` too;
  - `ApplyGraphics` sets `_scene.ProbeQuality`;
  - `Settle` waits for `ProbesSettled` when shooting, up to `ScreenshotProbeFrames = 2048`.
- Modify: `src/Super/Rendering/SceneRenderer.cs` (`ProbeQuality` forwards to `Renderer.ProbeQuality`; `ProbesSettled` forwards)
- Test: `tests/Super.Tests/GraphicsTests.cs`, `tests/Super.Tests/ScreenshotSettleTests.cs`, `tests/Rendition.Gpu.Tests/LiveProbeTests.cs`
- Docs:
  - `docs/rendering.md`: a Graphics settings table row for `ProbeQuality` (what each level changes, memory, default High), and the `LightProbes` row updated;
  - `docs/command-line.md`: `--screenshot` now waits until near probes are settled, up to 2,048 frames.

**Interfaces:**
- Produces: `public ProbeQuality Graphics.ProbeQuality { get; set; }`. Setting a value outside the enum throws `ArgumentOutOfRangeException` with "Probe quality can be Low, Medium or High." and `ParamName` "value".
- Produces: the `GameWindow.RendererSettings(...)` tuple gains `ProbeQuality ProbeQuality`.
- Produces: `internal static int GameWindow.ExtraSettleFrames(Func<bool> settled, Action renderFrame, int cap)`. It renders frames until `settled()` or the cap is reached, and returns how many it rendered. It is pure enough to test with fakes.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `Out_of_the_box_every_effect_is_drawn_with_vsync_and_no_frame_limit` (existing) | Add `Assert.Equal(ProbeQuality.High, graphics.ProbeQuality)` |
| `An_undefined_probe_quality_is_refused` | `new Graphics { ProbeQuality = (ProbeQuality)7 }` throws `ArgumentOutOfRangeException`, the message starts with "Probe quality can be Low, Medium or High.", and `ParamName` is "value" |
| `Fastest_sets_probe_quality_to_low_and_light_probes_off` | `Graphics.Fastest()` has `ProbeQuality.Low` and `Off` has `RenderEffects.LightProbes` |
| `A_changed_probe_quality_reaches_the_renderer_next_frame` | The same pattern as `A_changed_render_scale_reaches_the_renderer_next_frame`: set `Medium` on the game's `Graphics`, and `GameWindow.RendererSettings(host.Graphics, hidden: false).ProbeQuality` is `Medium` |
| `Screenshot_waits_for_settled_probes_up_to_the_cap` | `ScreenshotSettleTests`: a fake that settles after 37 frames returns 37 and rendered 37. One that never settles returns the cap, 2048. One already settled returns 0 |
| `Moving_the_light_changes_the_probe_and_no_frame_goes_over_budget` | In `LiveProbeTests`, rasterized: `RedWall(false)` settled, then the light moved 2 m towards +z over 100 frames. Every frame `ProbeUpdatesLastFrame <= ProbeBudget.For(High).Raster`. 300 frames later the sample at (0.7, 0.3, 1.5) is at least 1.5 × brighter than before the move. The renderer is set to `ProbeQuality.Low` once mid-way and the budget check uses Low's count after that |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Tests --filter "FullyQualifiedName~GraphicsTests|FullyQualifiedName~ScreenshotSettleTests"`
Expected: compile errors (`ProbeQuality` on `Graphics`, `ExtraSettleFrames`).

- [ ] **Step 3: Implement the property, its flow to the renderer, and the settle**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Super.Tests --filter "FullyQualifiedName~GraphicsTests|FullyQualifiedName~ScreenshotSettleTests"` and `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~LiveProbeTests`
Expected: all pass.

- [ ] **Step 5: Prove the forwarding and settle tests can fail**

Return `High` from `RendererSettings` whatever the game set. `A_changed_probe_quality_reaches_the_renderer_next_frame` fails. Revert.

Make `ExtraSettleFrames` stop one frame early. `Screenshot_waits_for_settled_probes_up_to_the_cap` fails. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Super tests/Super.Tests tests/Rendition.Gpu.Tests docs/rendering.md docs/command-line.md
git commit -m "feat: add Graphics.ProbeQuality and settle probes before a screenshot"
```

---

### Task 7: The traced update path

**Files:**
- Create: `src/Rendition/Passes/ProbeTracePass.cs`
- Create: `src/Rendition/Shaders/probe_trace.comp`:
  - one workgroup per probe and 128 invocations, one ray each;
  - directions on a spherical Fibonacci set, rotated by a per-update rotation seeded from `(frame, probe id)`;
  - includes `trace.glsl`, `probe_light.glsl` and `probe_fold.glsl`.
- Modify: `src/Rendition/Shaders/trace.glsl` (add `bool traceVisible(accelerationStructureEXT scene, vec3 origin, vec3 dir, float maxDist)`, a terminate-on-first-hit query that is cut-out aware like `traceClosest`)
- Modify: `src/Rendition/Renderer.cs` (`RecordProbeUpdate` uses `ProbeTracePass` with `ProbeBudget.Traced` when `Tracing != null`; the top-level structure is built once a frame, from `TraceScene.Collect`, and shared with the trace debug view)
- Test: `tests/Rendition.Gpu.Tests/LiveProbeTests.cs`
- Docs:
  - `docs/rendering.md` ("Bounced light": traced when the GPU can, with no switch);
  - `docs/design.md` (the trace path, rays, hysteresis 0.25, shadow rays);
  - `docs/command-line.md` (`--no-trace` now changes probes: replace "Only `--debug-view trace` draws with rays so far, so no other picture changes").

**Interfaces:**
- Consumes: `RayTracing.TopLevel`, `TraceScene.Collect`, `trace.glsl` (`traceClosest`, `hitAlbedo`, emissive from the materials buffer), `probe_light.glsl`, `probe_fold.glsl`, `ProbeVolume.StagingTiles` and `RecordBlend` with `alpha = 0.25` (1 for a first capture).
- Produces: `internal sealed class ProbeTracePass`, with `Record(CommandBuffer cmd, IReadOnlyList<ProbeId> batch, TopLevelStructure scene)`, binding the same frame descriptor sets as `ProbeCapturePass` plus the trace sets.
- In `probe_trace.comp`:
  - `PROBE_SUN_VISIBILITY` and `PROBE_LIGHT_VISIBILITY` are shadow rays with `traceVisible`;
  - a miss returns sky radiance, with a distance of `2 * spacing`, like today's nothing-drawn case;
  - a back-face hit (`!hit.frontFace`) writes a negative distance, so the fold's inside rule is shared.

- [ ] **Step 1: Write the failing tests**

Turn the four rasterized-only tests from Task 5 into `[Theory]` tests with `[InlineData(false)]` and `[InlineData(true)]` on a `traced` parameter, and rename them without the `_rasterized` suffix. `traced: true` uses the shared `GpuFixture` and starts with `Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.")`. `traced: false` uses `new GpuFixture(disableRayTracing: true)`.

| Test | Asserts (traced) |
|---|---|
| `A_red_wall_beside_a_probe_tints_it_red(traced)` | As in Task 5, plus a check that the traced path ran: `ProbeUpdatesLastFrame` exceeds the raster budget on some frame |
| `A_spot_light_lights_the_probe_only_inside_its_cone(traced)` | As in Task 5 |
| `A_closed_box_reads_black_under_a_bright_sky(traced)` | As in Task 5 |
| `A_white_room_lit_by_one_light_settles_and_does_not_grow(traced)` | As in Task 5. Traced readings are noisier, so the 50-frame tolerance is 5% |
| `Moving_the_light_changes_the_probe_and_no_frame_goes_over_budget(traced)` | The Task 6 test as a theory. Traced uses `ProbeBudget.For(High).Traced` |
| `Traced_and_rasterized_probes_agree_on_a_simple_room` | `WhiteRoom()` settled on both paths: the samples at 5 points are within 15% of each other, so the two paths aren't different looks |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~LiveProbeTests`
Expected: the `traced: true` cases fail, because the traced path isn't used yet and the raster-budget check fails. The `traced: false` cases pass.

- [ ] **Step 3: Implement `traceVisible`, `ProbeTracePass`, `probe_trace.comp` and the path choice**

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Rendition.Gpu.Tests`
Expected: all pass. `TraceDebugTests` still pass with the top-level structure shared.

- [ ] **Step 5: Prove the traced closed-box and agreement tests can fail**

First, treat back-face hits as front faces in `probe_trace.comp`. `A_closed_box_reads_black_under_a_bright_sky(traced: true)` fails. Revert.

Then leave out the `albedo * probeIrradiance` bounce term in the traced includer only. `Traced_and_rasterized_probes_agree_on_a_simple_room` fails. Revert.

- [ ] **Step 6: Check `--no-trace` and the trace path on Courtyard**

Run both:

```bash
dotnet run -c Release --project examples/Courtyard -- --screenshot shots/task7-courtyard-trace.png --validate --seed 1 --ticks 300 --size 1280x720
dotnet run -c Release --project examples/Courtyard -- --screenshot shots/task7-courtyard-raster.png --validate --seed 1 --ticks 300 --size 1280x720 --no-trace
```

Expected: 0 validation messages each. The two images look alike, with the traced one a little softer. Look at both.

Run: `dotnet run -c Release --project examples/Courtyard -- --profile shots/task7-profile.txt --seed 1 --ticks 300`
Expected: the probe update pass is under 1 ms at High on this GPU. Write the figure in the PR. If it is over, lower `ProbeBudget.For(High).Traced` and note the new figure in `design.md`.

- [ ] **Step 7: Commit**

```bash
git add src/Rendition tests/Rendition.Gpu.Tests docs/rendering.md docs/design.md docs/command-line.md
git commit -m "feat: trace probe updates with 128 rays where the GPU can"
```

---

### Task 8: `--debug-view probes`

**Files:**
- Modify: `src/Rendition/Types.cs` (`DebugView.Probes`)
- Create: `src/Rendition/Shaders/probe_debug.vert`, `src/Rendition/Shaders/probe_debug.frag` (an instanced sphere per probe of the camera's finest cascade and the fixed grid, shaded with `probeIrradiance` at the probe's own position in the sphere's normal direction, drawn over the lit scene)
- Modify: `src/Rendition/Renderer.cs`, `src/Super/GameArguments.cs` (accepts `probes`)
- Test: `tests/Super.Tests/GameArgumentsTests.cs`, `tests/Rendition.Gpu.Tests/LiveProbeTests.cs`
- Docs: `docs/command-line.md` (`--debug-view probes`), `docs/rendering.md` ("Bounced light": how to look at the probes)

**Interfaces:**
- Produces: `DebugView.Probes`. Unlike `trace`, it needs no tracing. It draws uncaptured probes as small grey wireframes, and inside probes not at all.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `Debug_view_probes_is_accepted` | `GameArguments.Parse(["--debug-view", "probes"]).DebugView == DebugView.Probes` |
| `An_unknown_debug_view_names_both_choices` | Parsing `--debug-view nope` fails with a message containing "trace" and "probes" |
| `The_probe_view_draws_a_red_tinted_sphere_beside_the_red_wall` | `RedWall(false)` settled, and `RenderOneFrame(draws, DebugView.Probes)`. The pixel at the projected position of the probe nearest (0.5, 0.5, 0), on its +x side, has `r > 2 * g` (with the `AssertPixel` helper from `TraceDebugTests`) |

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~GameArgumentsTests` and `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~LiveProbeTests`
Expected: the `probes` name is refused, and `DebugView.Probes` doesn't exist.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run the tests and see them pass**

Same commands. Expected: all pass.

- [ ] **Step 5: Prove the pixel test can fail**

Shade the spheres with plain white. `The_probe_view_draws_a_red_tinted_sphere_beside_the_red_wall` fails. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Rendition src/Super tests docs/command-line.md docs/rendering.md
git commit -m "feat: add --debug-view probes to see the live probes"
```

---

### Task 9: Every example on live probes, after shots and docs

**Files:**
- Modify: `examples/Courtyard/CourtyardGame.cs` (line 160) and `examples/Gallery/GalleryGame.cs` (line 216). Remove the `view.LightProbes` call if the after shot with cascades alone looks at least as good as with the fixed grid. Otherwise keep it, now updated live, and say why in a comment. These are the only examples that capture at load today. The other 16 had no bounced light at all and get the cascades with no code change.
- Create: `docs/media/probes-<example>.webp` for each example whose picture changed. Each is a labelled before and after pair, about 1200 px wide, cropped to the part that changed.
- Create: `docs/media/probes-courtyard-paths.webp`: rasterized and traced side by side.
- Modify: `docs/rendering.md` ("Bounced light" uses `probes-courtyard.webp` and `probes-courtyard-paths.webp`)
- Modify: the examples' `README.md` files where they describe probes (grep first)

**Interfaces:** none new.

- [ ] **Step 1: Shoot every example after the change, twice**

Run, in Git Bash:

```bash
dotnet build -c Release
for e in Archer Bricks ColorCube Courtyard Crafting Crypt Dust Forest Fox Gallery Glass Hamlet Reef Rooms Shapes Terrain Transitions Wax; do
  n=$(echo "$e" | tr 'A-Z' 'a-z')
  for run in a b; do
    dotnet run -c Release --project examples/$e -- --screenshot shots/after/probes-$n-$run.png --validate --seed 1 --ticks 300 --size 1280x720 > shots/after/probes-$n-$run.txt 2>&1
  done
  cmp shots/after/probes-$n-a.png shots/after/probes-$n-b.png || echo "$e differs between runs"
done
```

Expected: no "differs between runs" line, and 0 validation messages in every `.txt`. For any example that already had messages in its baseline, the count is no higher. Shoot Courtyard and Gallery once more with `--no-trace` to `shots/after/probes-<n>-notrace.png`.

- [ ] **Step 2: Decide the fixed grids in Courtyard and Gallery**

Comment out the `view.LightProbes` line, shoot, and compare with the `-a` shot. Keep the version that looks better. Leave the line removed when they look the same.

- [ ] **Step 3: Look at every pair**

For each example, open `shots/baseline/probes-<n>.png` and `shots/after/probes-<n>-a.png`. An example must look at least as good: no darker corners that were lit, no light leaking through walls, no blotches. Fix any that don't before going on. Write which examples changed into `shots/after/notes.txt`.

- [ ] **Step 4: Make the comparison images**

For each changed example, run:

```bash
ffmpeg -y -i shots/baseline/probes-$n.png -i shots/after/probes-$n-a.png -filter_complex "[0]crop=W:H:X:Y,drawtext=fontfile='C\:/Windows/Fonts/segoeui.ttf':text='Load-time probes':x=12:y=12:fontsize=28:fontcolor=white:box=1:boxcolor=black@0.5[a];[1]crop=W:H:X:Y,drawtext=fontfile='C\:/Windows/Fonts/segoeui.ttf':text='Live probes':x=12:y=12:fontsize=28:fontcolor=white:box=1:boxcolor=black@0.5[b];[a][b]hstack,scale=1200:-2" -c:v libwebp -quality 90 docs/media/probes-$n.webp
```

`W:H:X:Y` is the crop around what changed, chosen per example. For the 16 examples that had no probes before, label the left image "No bounced light" instead.

Make `probes-courtyard-paths.webp` the same way from the `-notrace` and `-a` shots, labelled "Rasterized" and "Traced".

Open every `.webp` and check that it shows what its labels say.

- [ ] **Step 5: Update the docs**

Put `probes-courtyard.webp` and `probes-courtyard-paths.webp` in `rendering.md`'s "Bounced light". Then check for stale text:

Run: `grep -rn "load\b\|hitch\|captured again\|no point and spot\|LightProbes" docs/rendering.md docs/design.md docs/views.md docs/command-line.md examples/*/README.md`
Expected: every match describes live probes. No text says probes are captured at load, hitch, or skip point and spot lights.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add examples docs
git commit -m "docs: show every example on live probes, before and after"
```

The PR description lists the validation message count for every shot, the probe pass time from Task 7's profile, and links the images by commit as `https://github.com/Artmann/super/blob/<sha>/docs/media/probes-<n>.webp?raw=true`.

---

## Not in this plan

- Probes over bakes. Lightmapped pixels skipping probe diffuse light, and baked probes replacing live ones in a baked space, need plan 4's `Static` and plan 5's runtime. Plan 5 wires them into the lookup order from Task 4, before the cascades.
- Relocating a probe that sits inside a wall to a nearby open spot. The inside rule gives it zero weight, as today.
- Marking probes dirty when geometry moves. Moved draws are picked up by the steady refresh and the periodic probe sun shadow redraw, not at once.
- Traced reflections that reuse the probes for their misses. That is a later plan.
- The 1, 2, 3 and P keys in Gallery and Hamlet. `--debug-view probes` covers looking at probes, and the keys come with plans 5 and 6.

## Calls made

1. The rasterized capture moves out of the deferred pipeline. Today's capture runs the full G-buffer, lighting, submit and wait for each face, and that is the hitch. Instead, a forward multiview pass draws all six faces of a probe in one render, in the frame's own command buffer. It uses no surface shaders, the same as the traced hit, so both paths see the same scene.
2. One shared `probe_light.glsl` lights rasterized captures and traced hits. Only the shadow test differs: shadow maps against shadow rays, through two macros. This keeps the two paths agreeing, as `Traced_and_rasterized_probes_agree_on_a_simple_room` checks, and follows the roadmap's rule to reuse rather than copy.
3. The rasterized sun shadow comes from a dedicated camera-centred orthographic map over the largest cascade. The frame's own cascaded map covers only the view frustum, while probes sit behind the camera too.
4. Local lights shadow probe captures only where they already have a shadow tile. A light without one lights probes unshadowed, the same as it lights the scene.
5. `ProbeQuality` changes the probes per cascade (32×16×32, 24×12×24 or 16×8×16) and keeps the spacings. Lower quality covers less ground rather than going blurrier near the camera. Each quality has separate rasterized and traced budgets: 8/256, 16/512 and 32/1024 probes a frame. The traced figures are a starting point that Task 7's profile confirms.
6. The spec says "a fixed number of probes a frame" but gives no millisecond budget. The budget is a count, tested exactly, and the GPU time is measured by hand with `--profile` and reported in the PR.
7. A screenshot waits until the probes in the two finest cascades and the fixed grid are captured, up to 2,048 frames. That matters because 168 settle frames are too few for the rasterized path to cover 16,384 probes. Waiting counts frames, not time, so shots stay repeatable.
8. A probe's valid flag lives in its irradiance tile's alpha. That needs no new binding, and an uncaptured probe gets zero weight and falls back to a coarser cascade, then the sky. Before, a probe was treated as black until captured.
9. Updates are written to a staging buffer and blended into the atlases by a separate dispatch after a barrier. Bounce never reads a tile that is being written. Hysteresis is 1 for a first capture, then 0.5 rasterized (noise-free) and 0.25 traced (128 rays). A first capture snaps in, so new slices never fade in from black.
10. Lighting changes don't restart anything. A moved local light marks probes near its old and new place dirty, and a sun, sky or environment change marks all probes dirty. Old values stay in use until refreshed, so nothing goes dark. This replaces `LightingHash` and `ProbeSchedule`'s rounds.
11. `ReflectionLightingHash` uses `ProbeScheduler.Settled` rather than every probe update. Otherwise reflection probes would recapture all the time.
12. `view.LightProbes` stays as the spec says, and its grid wins inside its box. Courtyard and Gallery keep their call only if it looks better than the cascades alone (decided by shot in Task 9).
13. The fixed grid shares the cascades' scheduler and budget as layer 4 rather than updating separately, so one budget bounds the whole frame.
14. `--debug-view probes` is added. The spec doesn't ask for it, but nobody can review probes without seeing them, and plan 1 did the same with `trace`.
15. Baselines are named `shots/baseline/probes-<example>.png`, because `Courtyard.png`, `gallery-*.png` and `hamlet-*.png` already exist there for other comparisons. Every pair uses `--seed 1 --ticks 300 --size 1280x720`. The 300 ticks give games with a start-up animation time to settle, and the screenshot path renders only the last 120 ticks, so this costs little.
16. `Graphics.Fastest()` sets `ProbeQuality.Low` as well as turning probes off, so turning probes back on from the fastest preset stays cheap.
