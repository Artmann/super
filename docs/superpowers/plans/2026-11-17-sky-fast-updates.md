# Sky Fast Updates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The sky and sun can change every frame: the environment cubes are rebuilt on the GPU inside the frame's command buffer, with no CPU wait, cheaply enough to do every frame.

**Architecture:** `EnvironmentMap` gains `Record(CommandBuffer, SkySun)`, which draws the sharp levels analytically and prefilters the rough ones from a mip-chained source cube with filtered importance sampling (few samples). The renderer records it in the frame after a change instead of waiting for the GPU; the blocking `Bake` stays as `SubmitAndWait(Record)` for loading, tests and bakes. This is PR 1 of the stack in the spec.

**Tech Stack:** C# / .NET 10, Silk.NET Vulkan, GLSL compute, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-17-skies-design.md` (section "1. Fast sky updates").

## Global Constraints

- Always use braces for control statements.
- Descriptor setup goes through `Descriptors` in `src/Rendition/Vulkan`; no new Roe duplicates, no dead code. `dotnet roe` before each commit.
- Docs in `docs/` change in the same commit as the code.
- Conventional commits; no Claude author or Co-Authored-By lines.
- Budget: the whole sky update under about 0.5 ms GPU on the dev GPU at the High preset. Over it, time-slice the rough mips (Task 3).
- No new per-frame GPU cost when the sky doesn't change.
- Zero validation messages.
- The gradient sky, panels and studio keep their look (Task 1 tests).
- Branch `feat/sky-fast-updates` from `main`, the first branch of a `gh stack`.

## Review Focus

1. A sky change in a frame that then bakes (`Bake`, `BakeChunk`, reflection probe capture): the pending update must reach the cubes before the bake reads them. Test in Task 2.
2. A frame drawn twice (again without its baked sun): `BakeSky` runs once per frame from the original view; a second change in the same frame must not record two updates or leave the first half-done. Test in Task 2.
3. `SetEnvironment` before the first frame (the constructor, Super's `SceneRenderer` setup): there is no frame command buffer yet, so it must still give a filled environment by the first draw. Test in Task 2.
4. A small, very bright panel (a softbox of radiance 20+) in the rough mips: few-sample filtering of a hot spot speckles unless it reads a blurred enough source mip. Test in Task 1.
5. `Graphics.EnvironmentLight` off reads `Flat`, now read back without a wait and so up to the frames in flight behind. After a change it must settle on the new average within a few frames. Test in Task 2.

Known limit, documented rather than fixed: a scene with reflection probes under a sun that moves every frame recaptures them every frame, and a capture waits for the GPU. No example does this today.

---

### Task 1: Prefilter the environment from a mip-chained source, recordable into any command buffer

**Files:**
- Modify: `src/Rendition/Vulkan/EnvironmentMap.cs`
- Modify: `src/Rendition/Shaders/environment.comp` (draws a level analytically: the sharp reflection, the source cube, the sky cube)
- Create: `src/Rendition/Shaders/environment_filter.comp` (GGX or cosine filtered importance sampling from a sampled cube)
- Modify: `src/Rendition/Vulkan/CubeCentres.cs` (record the copy into a given command buffer; keep `Average` for the blocking path or drop it if unused)
- Test: `tests/Rendition.Gpu.Tests/EnvironmentPrefilterGpuTests.cs`

**Interfaces:**
- Produces:
  - `void EnvironmentMap.SetDescription(in EnvironmentDescription description)`: stores the bands and panels; nothing is drawn until the next `Record`.
  - `void EnvironmentMap.Record(CommandBuffer cmd, in SkySun sky)`: records the whole update with barriers on both sides: before it, earlier frames' fragment and compute reads of the three cubes; after it, fragment and compute shader reads. Uniforms go in through `vkCmdUpdateBuffer` into a device-local buffer, so a frame in flight never sees them change.
  - `void EnvironmentMap.Bake(in EnvironmentDescription description, in SkySun sky)` and `void Rebake(in SkySun sky)` stay, as `SetDescription` + `SubmitAndWait(cmd => Record(cmd, sky))`, then read `Flat` exactly.
  - `(Vector3 Irradiance, Vector3 Reflection) Flat`: from a host-visible buffer that `Record` copies the face centres into at its end. After a blocking bake it is exact; after a recorded one it is the last finished update's.
  - `Reflections`, `Irradiance`, `Sky`, `Sampler` keep their meaning, sizes and formats (512 with 6 mips, 32, 64). `Sky` may gain a mip chain; `SkySh` reads its 16² level, so if mips are added, `sky_sh.comp` must still read the right level.

The pipeline, in order, inside `Record`:
1. Draw a new private source cube, 256² with a full mip chain, analytically: bands or clear sky, with panels, without the sun's disc. Fill its mips with `vkCmdBlitImage` (linear).
2. Draw reflections mip 0 analytically, with panels and the disc, as today.
3. Prefilter reflections mips 1–5 from the source cube: GGX with V = N, roughness `mip / 5`, 64 Hammersley samples, each sample reading the source at `lod = 0.5 * log2(Ωs / Ωp) + 1`, where `Ωs = 1 / (N · pdf)`, `pdf = D(h) / 4`, `Ωp = 4π / (6 · 256²)`.
4. Draw the sky cube (64²) analytically, without panels or disc, then its mips by blit.
5. Irradiance (32²) by cosine-weighted filtered importance sampling of the sky cube, 64 samples, `pdf = cos θ / π`.
6. Copy the face centres of irradiance and of reflections mip 5 into the readback buffer.

- [ ] **Step 1: Write the failing tests**

In `EnvironmentPrefilterGpuTests` (`[Collection(GpuCollection.Name)]`, `Speed=Slow`, `Needs=GPU`, as `FlatEnvironmentGpuTests`):

```csharp
[Theory]
[InlineData(1)]
[InlineData(3)]
[InlineData(5)]
public void Rough_reflections_match_a_reference_integral_of_the_gradient(int mip)
// Bake a gradient (Ceiling (0.1,0.3,0.9), Horizon (0.9,0.6,0.3), Floor (0.2,0.15,0.1)), read the face centre
// texel of reflections mip `mip` facing +Y, +X and -Y. Compare with a CPU integral in the test: 4096 GGX samples
// at roughness mip/5 of the band function mix(horizon, ceiling, sqrt(y)) / mix(horizon, floor, sqrt(-y)).
// Assert each channel within 3% relative (or 0.005 absolute below 0.15).

[Fact]
public void A_small_bright_panel_spreads_smoothly_in_the_rough_reflections()
// Panel overhead, HalfWidth/HalfHeight 0.1, Radiance 40. Read every texel of the +Y face of mip 3.
// Assert no texel is more than 1.5x the mean of its 3x3 neighbours (no speckles), and the +Y centre is within
// 10% of the CPU reference.

[Fact]
public void Recording_into_a_command_buffer_waits_for_nothing_and_fills_the_cubes()
// SetDescription with a uniform colour, then context.SubmitAndWait(cmd => environment.Record(cmd, default)).
// Assert SubmitAndWaitCount went up by exactly 1 (only the test's own submit), and Flat equals the colour within 2e-3.
```

Add a readback helper in the test for a cube mip's face (copy to a host-visible buffer, `Half` to float), unless one already exists in `Rendition.Gpu.Tests`; search first.

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test tests/Rendition.Gpu.Tests -c Release --filter "FullyQualifiedName~EnvironmentPrefilterGpuTests"`
Expected: the `Record` test fails to compile; the integral tests may pass against the old 1024-sample bake. That is fine: they guard the look. Prove they can fail by changing the GGX lod bias in Step 3 to `+ 4` and watching them fail.

- [ ] **Step 3: Implement**

Write `SetDescription`, `Record`, `environment_filter.comp`, and the source cube in `EnvironmentMap.cs`, with descriptor sets through `Descriptors`. `environment.comp` loses its sample loop and becomes the analytic draw. Keep `REFLECTION_LODS` as it is.

- [ ] **Step 4: Run the new tests and the existing environment tests**

Run: `dotnet test tests/Rendition.Gpu.Tests -c Release --filter "FullyQualifiedName~Environment|FullyQualifiedName~SkySh|FullyQualifiedName~Flat"`
Expected: PASS, with the validation count unchanged.

- [ ] **Step 5: Measure**

Add `"Sky"` at the front of `GpuTimer.FramePasses` (Task 2 marks it). For now, time a `Record` in a throwaway test with `GpuTimer` or timestamps, and note the number in the commit message. Over 0.5 ms: drop the reflection filter samples to 32 and re-run Step 4; still over, note it for Task 3.

- [ ] **Step 6: Update `design.md` "Environment lighting"**

Replace "a compute shader bakes that into two cube maps … Baking waits for the GPU, so it belongs in loading" with the new pipeline: the analytic levels, the source cube and filtered importance sampling, the sample counts, the lod formula, and `Flat` read back without a wait.

- [ ] **Step 7: Roe and commit**

Run: `dotnet roe`. Expected: no new findings.
```bash
git add src/Rendition docs/design.md tests/Rendition.Gpu.Tests/EnvironmentPrefilterGpuTests.cs
git commit -m "perf: prefilter the environment from a mip chain, recordable in a frame"
```

---

### Task 2: Record sky changes in the frame instead of waiting for the GPU

**Files:**
- Modify: `src/Rendition/Renderer.cs` (`SetEnvironment` ~1242, `BakeSky` ~2959, the frame after `BeginCommandBuffer` ~4060, the blocking paths that call `BakeSky` at ~2080, ~2783, ~3527)
- Modify: `src/Rendition/Vulkan/GpuTimer.cs` (the "Sky" pass)
- Modify: `docs/rendering.md` ("The sky": drop "Changing the colours rebuilds the sky's lighting, which takes a moment, so keep them steady rather than animating them every frame"; say it can change every frame)
- Modify: `docs/design.md` (the paragraph "Super keeps one set of bands … would rebake every frame", and "The renderer compares `SkyModel.Sun` … waits for the GPU and runs `EnvironmentMap.Rebake`")
- Test: `tests/Rendition.Gpu.Tests/SkyUpdateGpuTests.cs`

**Interfaces:**
- Consumes: `EnvironmentMap.SetDescription`, `EnvironmentMap.Record`, `EnvironmentMap.Bake` (Task 1).
- Produces:
  - `Renderer.SetEnvironment` no longer waits: it calls `SetDescription`, bumps `_environmentVersion`, sets `_environmentPending = true`. Its doc comment drops "Waits for the GPU, so call this while loading".
  - `BakeSky(view)` no longer waits: on a change it sets `_bakedSky` and `_environmentPending = true` and counts `SkyRebakes` as now.
  - `private void RecordEnvironment(CommandBuffer cmd)`: if pending, records `_environment.Record(cmd, _bakedSky)` under the "Sky" timer mark and clears the flag. Called in the frame right after `_uploads.Record`, before any pass reads the cubes.
  - `private void FlushEnvironment()`: if pending, `SubmitAndWait(RecordEnvironment)`. Called in the blocking paths right after their `DeviceWaitIdle` and `BakeSky`, and by the constructor after its first `SetEnvironment`.

Light probes: leave `ProbeLightingKey` as it is. A change marks every probe dirty, but a dirty probe keeps its light until it is redone nearest first, so under a sky that changes every frame this is a rolling refresh, which the spec asks for. Check it in Task 3's recording; only if probes visibly flicker or lag badly, come back to this.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_sun_moving_every_frame_rebakes_the_sky_every_frame_without_waiting()
// Clear sky (Turbidity 3), one shadow-casting sun. Render once to settle. Then for 6 frames turn the sun 2° a frame.
// Assert SkyRebakes went up by 6, Context.SubmitAndWaitCount did not change, the validation count did not change,
// and ProjectSky() after the last frame differs from ProjectSky() before the first (coefficient 0, more than 1e-3).

[Fact]
public void A_new_gradient_shows_on_the_next_frame_without_waiting()
// Render with bands A. SetEnvironment(bands B). Assert SubmitAndWaitCount unchanged by the call. Render one frame.
// Assert ProjectSky() coefficient 0's rgb matches B's irradiance (as FlatEnvironmentGpuTests' uniform-colour case)
// within 2e-3.

[Fact]
public void A_sky_change_before_a_bake_reaches_the_bake()
// Render with sun direction A, change to direction B, then call Renderer.Bake on a small space (reuse the Room
// fixture from BakedSunGpuTests). Assert the bake ran (non-empty result) and ProjectSky() equals the projection
// after a separate blocking EnvironmentMap.Bake with direction B, within 1e-3.

[Fact]
public void Flat_settles_on_the_new_sky_within_the_frames_in_flight()
// Render with bands A, SetEnvironment(uniform colour C), render FramesInFlight + 1 frames.
// Assert the frame uniforms' FlatIrradiance (via the existing test accessor, or add an internal one) equals C within 2e-3.
```

For Review Focus 2 add to the first test: render one of the frames through the path that draws a frame again without its baked sun, as `BakedSunGpuTests` does, and assert `SkyRebakes` still rose by one for that frame.

- [ ] **Step 2: Run and check they fail**

Run: `dotnet test tests/Rendition.Gpu.Tests -c Release --filter "FullyQualifiedName~SkyUpdateGpuTests"`
Expected: the first two fail on `SubmitAndWaitCount` (today each change does about ten submits).

- [ ] **Step 3: Implement the interfaces above**

- [ ] **Step 4: Run the sky, bake, probe and environment GPU tests**

Run: `dotnet test tests/Rendition.Gpu.Tests -c Release --filter "FullyQualifiedName~Sky|FullyQualifiedName~Bake|FullyQualifiedName~Environment|FullyQualifiedName~Probe"`
Expected: PASS. `BakedSunGpuTests` still sees one rebake.

- [ ] **Step 5: Docs, Roe, commit**

Update the two docs above. Search `docs/` for `SetEnvironment`, `Rebake`, `keep them steady`, `while loading` and fix any old description.
Run: `dotnet roe`.
```bash
git add src/Rendition docs tests/Rendition.Gpu.Tests/SkyUpdateGpuTests.cs
git commit -m "feat: change the sky every frame without waiting for the GPU"
```

---

### Task 3: Hamlet's sky glides, and the update fits its budget

**Files:**
- Modify: `examples/Hamlet/DayCycle.cs` (remove `SkyStep` and the stepping; use `seconds` directly)
- Modify: `src/Rendition/Vulkan/EnvironmentMap.cs` (only if over budget: time-slicing)
- Modify: `docs/rendering.md` (the Hamlet mention, if any, of the stepping; search for `SkyStep` and `half second`)
- Create: `docs/media/sky-fast-updates.webp` (or a short `.webm`/animated `.webp` of Hamlet's dusk)

- [ ] **Step 1: Remove the stepping and run Hamlet's tests**

Run: `dotnet test tests/Hamlet.Tests -c Release`
Expected: PASS.

- [ ] **Step 2: Measure the GPU time**

Run Hamlet with the profiler from `docs/command-line.md` over a dusk stretch (sun moving every frame) at the High preset and read the "Sky" pass time.
Expected: under 0.5 ms. Over: in `EnvironmentMap`, add `Record(cmd, sky, slice)` that each frame draws mip 0, the sky cube and irradiance, and one of reflection mips 1–5 in turn, so a full refresh takes five frames; re-measure.

- [ ] **Step 3: Record and look**

Record Hamlet from sunset into dusk with `--record` (same `--seed`, `--size`) per `docs/command-line.md` and `--validate`. Watch it: no stepping in the sky, no hitch, no probe flicker. Note the validation count. Pull a before/after pair of frames half a second apart (old stepped vs new) if it shows the change, labelled with ffmpeg as CLAUDE.md says, about 1200 wide, saved as `.webp` in `docs/media/`, and use it in `rendering.md`'s sky section.

- [ ] **Step 4: Full checks, Roe, commit**

Run: `dotnet test Super.slnx -c Release --filter "Needs!=GPU"` and `dotnet test tests/Rendition.Gpu.Tests -c Release`.
Expected: PASS (bake-test flakes known on main: see if they fail on `main` too before chasing them).
Run: `dotnet roe`.
```bash
git add examples/Hamlet docs src/Rendition
git commit -m "feat: Hamlet's sky follows the sun smoothly"
```

- [ ] **Step 5: Open the first PR of the stack**

Install `gh stack` if needed (`gh extension install github/gh-stack`), make `feat/sky-fast-updates` the first branch of a stack on `main`, and open its PR: what changed and why, how it was tested (GPU time, validation count), what it still can't do (reflection probes under a moving sun), with the Hamlet media linked by commit.
