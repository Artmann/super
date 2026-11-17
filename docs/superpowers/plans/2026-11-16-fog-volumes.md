# Fog Volumes and the Rest of Volumetric Fog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish issue #17. Add local fog volumes, light the fog's ambient from the light probes, add a cost setting for the grid, fold the underwater light shafts into the froxel grid, and show it all in the Forest and the Crypt.

**Architecture:** The froxel fog (`volumetric_inject.comp` → `volumetric_integrate.comp` → `fogInFront()`) already exists, with sun shafts, light halos and reprojected history. This plan changes the injected cell to the standard pair: scattered light per metre in rgb, and extinction per metre in a. It then adds three new sources to the inject pass: fog volumes from a per-frame storage buffer, probe ambient, and water as a medium with rgb extinction. Water's extinction is uniform, so its transmittance stays closed form and only its in-scatter goes through the grid.

**Tech Stack:** C# (.NET 10), Vulkan via Silk.NET, GLSL compute shaders, xUnit.

**Spec:** GitHub issue Artmann/super#17 ("Volumetric fog and fog volumes"), plus the user's choices on 2026-11-16: `view.FogVolume` immediate mode, and the underwater shafts folded in now.

## Global Constraints

- Branch `feat/fog-volumes` from `main`, in the main checkout (no worktrees). One PR.
- Braces on every control statement. Conventional commits, with no Claude author and no Co-Authored-By lines.
- Vulkan descriptor writes go through `Descriptors` in `src/Rendition/Vulkan`. Don't add another hand-rolled `ImageWrite`, and move `VolumetricFogVolume`'s to `Descriptors` while you're in there.
- Every new effect gets a `Graphics` switch that defaults on, a `RenderEffects` flag, a row in the Graphics table of `docs/rendering.md`, and an entry in `Graphics.Fastest()` and the Low/Medium presets where cost matters.
- Update the docs in the same commit as the code: user-facing behaviour in `docs/rendering.md` and `docs/views.md`, and internals in `docs/design.md` ("Volumetric fog", "Height fog", the frame structure, and the limits list near line 4925).
- Fast tests: `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"`. GPU tests: `dotnet test tests/Rendition.Gpu.Tests -c Release`. Prove every new test can fail by breaking the code once.
- Every shader sum that a test checks has a C# mirror in `src/Rendition/Passes/` (as `Fog.Transmittance` and `VolumetricFog` do). Keep each mirror in step with its shader.

## Review Focus

1. A fog volume with clear global air (`FogSettings.Density` 0) must still draw. Today `VolumetricFog.Pack` returns zero without global density, so this is the most likely regression.
2. A fog volume that reaches past `VolumetricDistance` is cut at the reach. The docs must say so, and nothing may pop as the camera moves.
3. Outdoors, switching the in-volume ambient from the sky to the probes can leave a seam at the reach, where the plain fog's sky colour takes over. Compare the Courtyard before and after at the reach.
4. Under water with `Graphics.VolumetricFog` off, the shafts are gone, since the old per-pixel march is removed. That's intended; document it, and check that the Reef still looks right with the default settings.
5. Moving caustic shafts under 90% history can smear. Check the Reef recording, and lower the history weight under water if it smears.

---

### Task 1: Grid detail as a cost setting

**Files:**
- Modify: `src/Rendition/Passes/VolumetricFog.cs`, `src/Rendition/Passes/FrameUniforms.cs`, `src/Rendition/Types.cs` (FrameView / render settings that reach the renderer), `src/Super/Graphics.cs` (+ profile load/save and presets), `src/Rendition/Renderer.cs` (size and pack), `src/Rendition/Shaders/common.glsl`, `src/Rendition/Shaders/volumetric.glsl`, `src/Rendition/Shaders/volumetric_inject.comp`, `src/Rendition/Shaders/volumetric_integrate.comp`
- Test: `tests/Super.Tests/VolumetricFogTests.cs`, `tests/Super.Tests/GraphicsTests.cs`, `tests/Super.Tests/GraphicsProfileTests.cs`

**Interfaces:**
- Produces: `Graphics.VolumetricFogCellSize` (int, scene pixels per cell each way, 4–32, default 8) and `Graphics.VolumetricFogSlices` (int, 16–128, default 64). Values outside the range throw `ArgumentOutOfRangeException` with a message giving the range. `VolumetricFog.GridSize(int sceneWidth, int sceneHeight, int cellPixels, int slices)`. A new uniform `Vector4 VolumetricGrid` (x slices, y cell pixels, z fog volume count, w unused), placed right after `Volumetric` in both `FrameUniforms` and `common.glsl`.
- The `VOLUMETRIC_SLICES` constant becomes `int(frame.volumetricGrid.x)` everywhere.

- [ ] **Step 1: Write the failing tests.** `Grid_takes_its_cell_size_and_slices_from_the_settings`: `GridSize(1600, 900, 16, 32)` == `(100, 57, 32)`. `Cell_size_outside_4_to_32_is_refused`: 3 and 33 throw, and the message contains "4 to 32". `Slices_outside_16_to_128_is_refused`: same pattern. Add a profile round trip of both settings to `GraphicsProfileTests`. Ultra sets slices to 128; High keeps 8 and 64.
- [ ] **Step 2: Run them and see them fail** (`dotnet test tests/Super.Tests -c Release --filter VolumetricFog`).
- [ ] **Step 3: Implement.** The renderer remakes the volume when either value changes, just as it does for a size change. The shaders read slices from the uniform.
- [ ] **Step 4: Run the fast tests and `dotnet test tests/Rendition.Gpu.Tests -c Release --filter Volumetric`.** Pass. A Crypt screenshot at the defaults matches the one from before the change.
- [ ] **Step 5: Docs.** Add a cost table to "Volumetric fog" in `rendering.md`: cells at 1600×900, memory and the measured GPU time for 16/32, 8/64 and 8/128. Update the memory figure in the `VolumetricFog` row, and update `design.md`. Commit `feat: choose the volumetric fog's cell size and slices`.

### Task 2: Fog volume API

**Files:**
- Create: `src/Rendition/FogVolumes.cs` (`FogShape`, `FogVolumeSettings`, `FogVolumeDraw`, `FogVolumeBatch.Check`/`MaxVolumes`)
- Modify: `src/Super/View.cs`, `src/Rendition/Types.cs` (`FrameView.FogVolumes`, `RenderEffects.FogVolumes = 1 << n`, `All`), `src/Super/Graphics.cs` (`FogVolumes` switch, the effects map, `Fastest()`), `docs/views.md`, `docs/rendering.md`
- Test: `tests/Super.Tests/FogVolumeTests.cs`

**Interfaces:**
- `public enum FogShape { Box, Ellipsoid, Cylinder, Cone }`. Each is a unit shape moved by the transform: the box spans −0.5..0.5; the ellipsoid is a sphere of radius 0.5; the cylinder runs along Y with radius 0.5, from y −0.5 to 0.5; the cone has its apex at y 0.5 and a base of radius 0.5 at y −0.5.
- `public readonly record struct FogVolumeSettings(float Density)` with init properties `Vector3 Color = (1,1,1)` (scattering colour, each channel 0–1), `float EdgeFade = 0.25f` (0–1, the share of the way in from the surface over which density rises), `float Noise = 0f` (0–1), `float NoiseSize = 2f` (metres per feature, > 0), `Vector3 Wind = 0` (metres per second the noise drifts).
- `public void FogVolume(int key, FogShape shape, Matrix4x4 transform, in FogVolumeSettings settings)` on `View`. It follows `Decal`'s rules: one placement per key per frame, at most `FogVolumeBatch.MaxVolumes` = 64, and the same message styles.
- `FrameView.FogVolumes` is a `ReadOnlySpan<FogVolumeDraw>`, and `FogVolumeDraw(FogShape Shape, Matrix4x4 Transform, FogVolumeSettings Settings)`.

- [ ] **Step 1: Write the failing tests.** Cover each rule with its exact message: a negative or NaN density; Color outside 0–1; EdgeFade outside 0–1; Noise outside 0–1; NoiseSize ≤ 0; a non-finite Wind; a transform that isn't invertible; a repeated key ("Fog volume 3 was already placed this frame. Give each fog volume its own key."); the 65th volume ("At most 64 fog volumes fit in a frame."); and that the volumes placed reach `FrameView.FogVolumes` in order. Add `Graphics.FogVolumes` off → `RenderEffects.FogVolumes` to the existing effects-map test.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** The renderer ignores the volumes until Task 3.
- [ ] **Step 4: Run the fast tests.** Pass.
- [ ] **Step 5: Docs** (`views.md` API, and the `FogVolumes` row in the Graphics table). Commit `feat: place fog volumes from a view`.

### Task 3: Fog volumes in the grid

**Files:**
- Create: `src/Rendition/Passes/FogVolumePack.cs` (packs `FogVolumeDraw` into GPU structs, the C# mirror of the shader's shape density), `src/Rendition/Shaders/fog_volumes.glsl`
- Modify: `src/Rendition/Passes/VolumetricFog.cs` (`Pack` takes the volume count), `src/Rendition/Vulkan/VolumetricFogVolume.cs` (a storage buffer per frame in flight at set 1, binding 4; descriptor writes through `Descriptors`), `src/Rendition/Shaders/volumetric_inject.comp`, `src/Rendition/Shaders/volumetric_integrate.comp`, `src/Rendition/Renderer.cs`
- Test: `tests/Super.Tests/FogVolumeTests.cs`, `tests/Rendition.Gpu.Tests/FogVolumeGpuTests.cs`

**Interfaces:**
- GPU struct, std430, 128 bytes: `mat4 worldToLocal; vec4 colorDensity; vec4 shapeFadeNoise (x shape, y edge fade, z noise, w 1/noise size); vec4 windBounds (xyz wind, w bounding-sphere radius); vec4 centre (xyz, w unused)`.
- `FogVolumePack.Density(in FogVolumeDraw volume, Vector3 position, float time)` is the C# mirror, with noise 0, of `fogVolumeDensity()` in `fog_volumes.glsl`.
- `VolumetricFog.Pack(in FogSettings settings, int fogVolumes, RenderEffects off, bool capturingProbe, bool underwater)` turns the grid on when there's global density or `fogVolumes > 0` (and `FogVolumes` is on). With no global density, the reach is `VolumetricDistance` as usual.

- [ ] **Step 1: Write the failing tests.** In C#: `A_box_is_full_inside_and_empty_outside`; `Edge_fade_rises_from_nothing_at_the_surface` (a box with fade 0.5 has half density a quarter of the way in, hand-worked); `A_cone_is_empty_beside_its_apex` (a point at y 0.45, x 0.3 in local space is outside); `Fog_volumes_need_no_global_fog` (`Pack` with density 0 and 1 volume is non-zero); `Fog_volumes_off_leaves_clear_air_clear` (with the flag off, Pack is zero). On the GPU: `A_fog_volume_darkens_what_is_behind_it` (render a lit wall with and without a 2 m box of density 2 between the wall and the camera; the box's pixels are at least 50% darker or lighter towards its colour, and pixels beside it don't change) and `A_coloured_fog_volume_takes_its_colour` (a green box in sunlight raises G over R in its pixels).
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Change the cell's meaning.** Inject now stores `rgb = σ_s · L_in` (scattered light per metre) and `a = σ_t`. Integrate adds `T · rgb · (1 − e^{−σL}) / σ`, or `T · rgb · L` when σ < 1e-6. Height fog's σ_s·L_in is `fogDensity · L_in`, so the image is unchanged; check the Crypt shot against Task 1's.
- [ ] **Step 4: Add volumes.** For each volume whose bounding sphere holds the cell: `d = density · edgeFade · (noise > 0 ? mix(1, fbm, noise) : 1)`. The fbm is 3 octaves of gradient noise at `(local·scale + wind·time) / noiseSize` in world metres. The volume adds `d` to σ_t and `d · color · L_in` to the scattered light, using the same L_in (sky/probe ambient + suns + local lights) and phase as the height fog. Write `fogVolumeDensity()` so that it also does the shape and fade tests.
- [ ] **Step 5: Run the fast and GPU tests.** Pass. Time the inject pass in the Crypt with 8 volumes using `--profile`, and record the cost in `design.md`.
- [ ] **Step 6: Docs.** Add "Fog volumes" under "Volumetric fog" in `rendering.md`: shapes, settings, the limits (cut at the reach, only drawn while volumetric fog is on, 64 a frame), and an example. Update `design.md`. Commit `feat: draw fog volumes in the volumetric fog`.

### Task 4: Probe ambient in the fog

**Files:**
- Modify: `src/Rendition/Shaders/volumetric_inject.comp` (`#define PROBE_ATLAS_SET 1` and include `probes.glsl`, with bindings 11 and 12), `src/Rendition/Vulkan/VolumetricFogVolume.cs` (`SetSources` takes the probe atlases), `src/Rendition/Renderer.cs`
- Test: `tests/Rendition.Gpu.Tests/FogVolumeGpuTests.cs`

**Interfaces:**
- `vec3 fogAmbient(vec3 position, vec3 towards)` in inject: where `probeIrradiance(position, up, -towards)` and the same for down both answer (x ≥ 0), it returns `(E_up + E_down) / (2π)`. Otherwise it returns `fogSkyLight(skyTexture, towards)`. Baked spaces' probes come later. Say so in `design.md`.

- [ ] **Step 1: Write the failing GPU test.** `Fog_in_a_closed_room_is_not_lit_by_the_sky`: a closed box room with a bright sky, live probes on, fog of 0.05 and no lights. The fog's light in the frame is less than 10% of the same room with probes off (that is, sky ambient).
- [ ] **Step 2: Run it and see it fail.**
- [ ] **Step 3: Implement.** When `Graphics.LightProbes` is off, the probe counts are 0 and `fogAmbient` falls back to the sky.
- [ ] **Step 4: Run the GPU tests.** Pass. Compare the Courtyard at the reach before and after for a seam (Review Focus 3). If there's one, fade from probe to sky ambient over the last 25% of the reach.
- [ ] **Step 5: Docs.** Update `rendering.md` and `design.md`. Commit `feat: light the volumetric fog from the light probes`.

### Task 5: Underwater shafts through the grid

**Files:**
- Modify: `src/Rendition/Passes/VolumetricFog.cs` (under water, Pack runs with reach `UnderwaterSettings.ShaftDistance`), `src/Rendition/Shaders/volumetric_inject.comp`, `src/Rendition/Shaders/volumetric_integrate.comp`, `src/Rendition/Shaders/water.glsl` (remove `lightShafts`; `throughWater` uses the grid), `src/Rendition/Shaders/fog.glsl`, `src/Rendition/Renderer.cs`
- Test: `tests/Super.Tests/VolumetricFogTests.cs`, `tests/Rendition.Gpu.Tests` (the underwater test that exists, or a new `Underwater_shafts_are_cut_by_shadows`)

**Interfaces:**
- Under water, each cell adds `σ_w = frame.waterAbsorption.rgb` to the extinction. The integrate pass then carries an rgb transmittance internally, but stores only the scalar fog transmittance in a. The water's own transmittance is `waterAbsorbed(metres)` at lookup. Water scatters `σ_w · waterColor(towards)` (so that the grid's ambient equals the old closed form) plus the shaft term `strength · causticAt(p, l, scale·0.3) · waterAbsorbed(toSurface) · sunShadow · towardsSun · waterShallow` per metre.
- Under water the height fog stays off (as today); fog volumes still add.

- [ ] **Step 1: Write the failing tests.** `Under_water_the_volume_reaches_the_shaft_distance` (Pack under water, with no fog, gives reach = ShaftDistance) and `Under_water_with_volumetric_fog_off_there_is_no_volume`. On the GPU: a sunlit underwater scene with a slab overhead, where the shaft light in the slab's shadow is under 20% of the light beside it.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Check that with the shaft strength at 0, the Reef's far water colour matches the old closed form to within 2% (the ambient term).
- [ ] **Step 4: Record 5 s of the Reef** (`--record`). Look at it frame by frame for smearing shafts. If they smear, use history 0.75 under water.
- [ ] **Step 5: Docs.** Update the water section of `rendering.md`: shafts now respect shadows, and they need `Graphics.VolumetricFog`, so the Low and Medium presets lose them. Update the `Underwater` row and `design.md`. Commit `feat: draw the underwater light shafts in the volumetric fog`.

### Task 6: Forest shafts and a misty Crypt

**Files:**
- Modify: `examples/Forest/ForestGame.cs` (a lower morning sun, fog with density about 0.02 and falloff about 0.3, and enough canopy between the camera and the sun to cut shafts), `examples/Crypt/CryptGame.cs` (a floor mist: a box of fog volume about 0.4 m deep over the floor, density about 0.3, edge fade 0.5, noise 0.6, NoiseSize 1.5, a slow wind), plus a key to toggle the volumes in each, as Courtyard does
- Create: `docs/media/forest-shafts.webp`, `docs/media/crypt-mist.webp`

- [ ] **Step 1: Tune both scenes** with `--screenshot --validate --seed 1 --ticks 120 --size 1280x720` until the shafts and the mist read clearly. Expected: 0 validation messages.
- [ ] **Step 2: Record 10 s of each** (`--record --seconds 10`). Step through the frames with ffmpeg and look for flicker, especially torch-lit mist under the flickering torch lights and shafts as the camera moves.
- [ ] **Step 3: Make labelled before/after shots** with ffmpeg `hstack` + `drawtext`, about 1200 px wide, as `.webp` in `docs/media/`, and use them in `rendering.md`. Commit `feat: light shafts in the Forest and mist in the Crypt`.

### Task 7: Finish

- [ ] **Step 1:** `dotnet tool restore && dotnet roe`. Fix any dead code. Rewrite the baseline only for findings that moved.
- [ ] **Step 2:** Run `dotnet test Super.slnx -c Release --filter "Needs!=GPU"` and `dotnet test tests/Rendition.Gpu.Tests -c Release`. Both pass.
- [ ] **Step 3:** Search `docs/` for `lightShafts`, `VOLUMETRIC_SLICES`, "64 deep", "35 MB" and `VolumetricFog.Pack` for stale descriptions. Tick the issue's checklist in the PR text.
- [ ] **Step 4:** Run one review of the whole branch, fix what it finds, then push and open the PR with the shots linked by commit SHA.
