# Courtyard realism Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Courtyard looks like a scene from a current game: bounced light, grounded
contact shadows, a real sky and air, exposure and tone that behave like a camera, wet
ground round a reflecting puddle, weathered and dirty surfaces, and vegetation with
species, translucency and wind.

**Architecture:** Five phases. Phases 1–3 teach the renderer and the cooker (a probe
volume, GTAO, contact shadows, a Preetham sky, height fog, auto exposure, AgX, render
scale, depth of field, projected decals, a puddle layer, foliage and wind rules, baked
wear masks). Phase 4 reworks the material graphs. Phase 5 rebuilds the scene on top and
takes the pictures. Every engine feature is a setting on `View`, `Graphics`, `SkySettings`,
`PostSettings` or a sidecar rule, defaults to today's behaviour, and is documented in the
same commit.

**Tech Stack:** .NET 10, Vulkan via Rendition (GLSL compiled by glslc), `Super.Materials`
graphs, Blender 4 Python for the model, xunit, ffmpeg for the media.

**Spec:** `docs/superpowers/specs/2026-11-08-courtyard-realism-design.md`

## Global Constraints

- Work on `feat/material-graphs` (worktree `rendition-materials`), one commit per task,
  conventional commits, no Co-Authored-By lines and no Claude author.
- Braces on every `if` and `while`. `TreatWarningsAsErrors` is on.
- Every new effect gets an on/off switch, and the default is the setting that looks
  best. Screen-space effects that need no scene data (GTAO, contact shadows,
  multiscatter GGX, AgX) are on by default; effects that need a scene value (a probe
  volume, fog density, a focus distance, a puddle level, wind) are on as soon as the
  scene gives that value. Each gets a `Graphics` bool switch the way `Graphics.Bloom`
  and `Graphics.AmbientOcclusion` work today: `ContactShadows`, `LightProbes`, `Fog`,
  `DepthOfField`, `Decals`, `Puddles`, `Wind`, `VolumetricFog`, all default `true` (plus `Graphics.Upscaler`, Task 28), documented in the
  Graphics table of `docs/rendering.md`. Check with `--screenshot` of `examples/Crypt`
  before and after a renderer task and confirm the change is the intended one, not drift.
- Docs change in the same commit as the code: user-facing options in `docs/rendering.md`
  (and `docs/content.md` for sidecar rules, `docs/materials.md` for graph behaviour),
  internals in `docs/design.md`, gaps in `docs/roadmap.md`. Before committing, grep
  `docs/` for the names touched.
- Tests follow `CLAUDE.md`: prove each can fail, no fixed sleeps, no asserting constants
  copied from the code, check exception messages. GPU-side logic is tested through the
  CPU side that feeds it (uniform packing, settings validation, layout sizes) and through
  cook output, as `tests/Super.Tests/HazeTests.cs`, `GpuMaterialTests.cs` and
  `PostGradeTests.cs` do today.
- Screenshots for the plan: `--seed 1 --ticks 60 --size 1600x900` from the Courtyard's
  default camera, into `shots/`, with the validation count noted. The baseline is
  `shots/plan-base.png` (0 validation messages).
- Shaders live in `src/Rendition/Shaders`, pass classes in `src/Rendition/Passes`, Vulkan
  resources in `src/Rendition/Vulkan`. The Super side is `src/Super/View.cs`,
  `Graphics.cs`, `Sky.cs`, `Rendering/SceneRenderer.cs`.
- `--validate` is not parsed by Super; validation is already on for every screenshot run.
  Report the count the run prints.

## Review Focus

1. **A probe inside a wall or under the ground.** Its capture sees the back of a face
   and would pour black into the yard. The capture must flag a probe whose faces are
   mostly back-faces and give it zero weight (Task 3 tests the weight; the capture flag is
   checked by a probe placed inside a closed box in `Rendition.Sample`).
2. **A screenshot run that adapts exposure for 60 ticks.** Auto exposure must reach the
   same value on every run and must start from the metered value on frame 0, not from
   black, or the first frames of every `--record` flash (Task 8).
3. **A decal box that straddles the sky.** Pixels with no depth must be discarded, not
   projected onto the far plane (Task 13).
4. **A surface flagged `puddles` that is itself above the level but has a deep height
   map.** The water test uses world Y plus height times depth, so a 3 cm mortar groove in
   a wall 2 m up never floods (Task 14).
5. **A model with overlapping UVs asking for `bakeMasks`.** The bake must fail the cook
   with a message naming the material, not silently bake garbage (Task 16).

---

## Phase 1: Light that bounces

### Task 1: GTAO with bent normals and specular occlusion

**Files:**
- Modify: `src/Rendition/Shaders/ao.frag` (rewrite), `ao_blur.frag` (blur 4 channels),
  `lighting.frag` (read bent normal, add `specularOcclusion`)
- Modify: `src/Rendition/Types.cs` (`FrameView.Occlusion`), `src/Rendition/Passes/FrameUniforms.cs`
- Modify: `src/Rendition/Vulkan/GBuffer.cs` or wherever the AO target is made (R8 → RGBA8)
- Create: `src/Rendition/Passes/Occlusion.cs` (settings → uniform packing)
- Modify: `src/Super/View.cs` (`AmbientOcclusion(float radius, float strength)`), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/OcclusionTests.cs`
- Docs: `docs/rendering.md` (Graphics settings table gets a line; a short "Ambient occlusion" paragraph), `docs/design.md` (replace the Alchemy AO paragraph)

**Interfaces:**
- Produces: `public readonly record struct OcclusionSettings(float Radius = 0.6f, float Strength = 0.9f)` in Rendition; `FrameView.Occlusion`; `View.AmbientOcclusion(float radius = 0.6f, float strength = 0.9f)`; `Occlusion.Pack(in OcclusionSettings) → Vector4 (radius, strength, 0, 0)`.
- The AO target is RGBA8: xy octahedral bent normal (view space), z visibility, w unused.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Packs_radius_and_strength() => Assert.Equal(new Vector4(0.8f, 0.5f, 0f, 0f), Occlusion.Pack(new OcclusionSettings(0.8f, 0.5f)));
[Theory, InlineData(0f, 1f), InlineData(-1f, 1f), InlineData(0.6f, 1.5f)]
public void Refuses_a_radius_that_is_not_above_zero_or_a_strength_outside_0_to_1(float radius, float strength)
{
    var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().AmbientOcclusion(radius, strength));
    Assert.Contains(radius <= 0f ? "radius" : "strength", error.Message);
}
[Fact] public void Defaults_match_the_renderer_without_a_call() => Assert.Equal(new OcclusionSettings(), new View().Occlusion);
```

- [ ] **Step 2: Run them, expect compile failures on `Occlusion`, `OcclusionSettings`, `AmbientOcclusion`**
- [ ] **Step 3: Implement.** `ao.frag`: 2 slices × 8 steps per side, slice angle from interleaved gradient noise + frame index, horizon search with falloff at `radius`, cosine-weighted visibility (Jimenez 2016), bent normal as the average unoccluded direction, output as above. `ao_blur.frag` blurs all four channels depth-aware. `lighting.frag`: `bentNormal` replaces `n` when sampling irradiance; `specularOcclusion(NoV, ao, roughness) = saturate(pow(NoV + ao, exp2(-16 * roughness - 1)) - 1 + ao)` multiplies environment, probe and SSR fallback specular. Keep the "overhead occlusion" term.
- [ ] **Step 4: Tests pass; screenshot `shots/t01-gtao.png`; compare the corner under the sill and the barrel's foot with the baseline**
- [ ] **Step 5: Commit** `feat: ground-truth ambient occlusion with bent normals and specular occlusion`

### Task 2: Sun contact shadows and shadow resolution

**Files:**
- Modify: `src/Rendition/Shaders/lighting.frag` (`contactShadow()`), `src/Rendition/Types.cs` (`ShadowSettings.ContactLength`), `src/Rendition/Passes/ShadowPass.cs` (`Resolution` becomes a constructor argument), `src/Rendition/Renderer.cs` (reads `RenderOptions.ShadowResolution`)
- Modify: `src/Super/View.cs` (`Shadows(..., float contact = 0.25f)`), `src/Super/Graphics.cs` (`ShadowResolution`), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/ShadowCascadeTests.cs` (add), `tests/Super.Tests/GraphicsTests.cs` (add)
- Docs: `docs/rendering.md` Shadows section and Graphics table; `docs/design.md` shadows

**Interfaces:**
- Produces: `ShadowSettings.ContactLength` (metres, 0 = off, default 0.25); `Graphics.ContactShadows` (bool, default true; false sends 0 to the renderer); `Graphics.ShadowResolution` (`int`, 1024, 2048 or 4096, default 2048); `View.Shadows(float radius, int cascades = 1, float sunSize = DefaultSunSize, float contact = 0.25f)`.

- [ ] **Step 1: Tests:** `Contact_length_reaches_the_frame` (view.Shadows(7f, 3, contact: 0.4f) → `view.ShadowSettings.ContactLength == 0.4f`); `A_contact_length_below_zero_is_refused` with message containing "contact"; `Shadow_resolution_must_be_1024_2048_or_4096` (`Graphics { ShadowResolution = 3000 }` throws on validation, message names the three sizes); `Changing_shadow_resolution_while_running_is_applied_next_frame` through the same path `GraphicsTests` uses for `Vsync`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** `contactShadow(worldPos, n, L)`: 12 steps along `L` over `ContactLength` in view space, project each, compare linear depth; a hit is thicker than 0.02 m and closer than the ray; fade the result over the last quarter of the ray and by `smoothstep(0.9, 1.0, distance / cascade0Radius)`. Multiply into the sun's `visibility` after PCSS. Shadow resolution rebuilds the cascade image when it changes, as the atlas already handles resize.
- [ ] **Step 4: Tests pass; screenshot `shots/t02-contact.png`; check the wheel's rim and the sacks meet their shadows**
- [ ] **Step 5: Commit** `feat: screen-space contact shadows for the sun, and Graphics.ShadowResolution`

### Task 3: Light probe volume

**Files:**
- Create: `src/Rendition/Vulkan/ProbeVolume.cs` (atlases, capture scheduling, uniform), `src/Rendition/Shaders/probe_fold.comp` (cube → octahedral irradiance and depth tiles), `src/Rendition/Shaders/probes.glsl` (`probeIrradiance()`), `src/Rendition/Passes/ProbeGrid.cs` (pure: grid layout, probe index ↔ position, validity weights)
- Modify: `src/Rendition/Renderer.cs` (capture rounds, invalidation hash), `src/Rendition/Shaders/lighting.frag` (diffuse ambient from probes when present), `src/Rendition/Types.cs` (`FrameView.LightProbes`), `src/Rendition/Vulkan/ReflectionProbes.cs` (share the cube capture)
- Modify: `src/Super/View.cs` (`LightProbes(Vector3 min, Vector3 max, float spacing)`), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/ProbeGridTests.cs`
- Docs: `docs/rendering.md` new "Bounced light" section; `docs/design.md` "Light probes" with the capture schedule and why depth is stored; `docs/roadmap.md` (remove "probes give no diffuse light")

**Interfaces:**
- Produces: `public readonly record struct ProbeVolumeSettings(Vector3 Min, Vector3 Max, float Spacing)`; `ProbeGrid.Counts(in ProbeVolumeSettings) → (int x, int y, int z)` (ceil of extent / spacing + 1, each ≥ 2); `ProbeGrid.Position(settings, index) → Vector3`; `ProbeGrid.MaxProbes = 4096`; `ProbeGrid.Weight(Vector3 point, Vector3 normal, Vector3 probe, float meanDepth, float meanSquares, float distance) → float` (trilinear × backface × Chebyshev, the same maths as the shader so the test pins it); `View.LightProbes(min, max, spacing)` validating `spacing > 0`, `Max > Min` per axis, count ≤ 4096, messages naming the offending axis or the limit; `Graphics.LightProbes` (bool, default true; false skips the capture and the lookup, the sky irradiance is used).
- Tile sizes: irradiance 8×8 + border, depth 16×16 + border, RGBA16F and RG16F atlases.
- Capture: 32² faces, up to 64 probes a frame, two rounds; a probe whose faces are more than half back-faces is marked invalid (weight 0). No point or spot lights in captures (as reflection probes).
- Invalidation: hash of sun direction and colour, sky settings, environment intensity, and the volume. Changing any recaptures from round one.

- [ ] **Step 1: Tests** (`ProbeGridTests`): `A_7_by_3_by_5_metre_box_at_1_metre_spacing_has_8_by_4_by_6_probes`; `Probe_positions_run_x_fastest_from_min` (index 1 is Min + (spacing, 0, 0), index counts.x is Min + (0, spacing, 0)); `More_than_4096_probes_is_refused` (message contains "4096"); `A_probe_behind_the_surface_gets_no_weight` (normal (0,1,0), probe below the point → 0); `A_probe_seen_through_a_wall_is_weighted_down_by_chebyshev` (meanDepth 0.5, variance small, distance 2 → weight < 0.1; distance 0.4 → weight > 0.9); `A_zero_spacing_is_refused`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** Capture through the reflection-probe cube path at 32², fold with `probe_fold.comp` (cosine-weighted irradiance over the octahedral tile, depth mean and mean-squares with a soft power-50 weight as DDGI), write tiles with borders. `probes.glsl`: `vec3 probeIrradiance(vec3 p, vec3 n, vec3 v)` biases `p` by `0.2 * spacing * (0.75 n + 0.25 v)`, blends the 8 cell corners by `ProbeGrid.Weight`'s maths, returns irradiance or `vec3(-1)` when total weight < 1e-3 so lighting falls back to the sky cube. Invalid probes carry a flag in the depth tile's border texel.
- [ ] **Step 4: Tests pass; add a probe volume to `Rendition.Sample`'s closed-box scene and check the box's inside stays unlit (Review Focus 1); Courtyard screenshot `shots/t03-probes.png` after adding `view.LightProbes(new(-2f, -0.5f, -9f), new(9f, 5f, 6f), 1f)` to `CourtyardGame.cs`; the shade under the window should take a brick-coloured warmth**
- [ ] **Step 5: Commit** `feat: a light probe volume gives bounced, leak-free diffuse light`

### Task 4: Reflection probes from Super, and glass that uses them

**Files:**
- Modify: `src/Super/View.cs` (`ReflectionProbe(int key, Vector3 position, Vector3 boxMin, Vector3 boxMax, float blend = 0.5f)`), `src/Super/Rendering/SceneRenderer.cs` (hand probes to `Renderer.CaptureReflectionProbe` once per key, recapture when the probe or the invalidation hash from Task 3 changes), `src/Rendition/Shaders/transparent.frag` (sample probes before the sky cube)
- Test: `tests/Super.Tests/ReflectionProbeTests.cs`
- Docs: `docs/rendering.md` "Reflections" paragraph, `docs/design.md` (glass now reads probes)

**Interfaces:**
- Produces: `View.ReflectionProbe(...)`; `View.ReflectionProbes` list of `ReflectionProbeRequest(int Key, Vector3 Position, Vector3 Min, Vector3 Max, float Blend)`; at most `FrameUniforms.MaxProbes` (4) per frame, the fifth throws with a message containing "4".

- [ ] **Step 1: Tests:** `A_fifth_probe_is_refused`; `A_probe_whose_box_excludes_its_position_is_refused` (message names "position"); `The_same_key_twice_in_a_frame_is_refused`; `A_probe_is_captured_once_until_it_changes` (through `SceneRenderer`'s capture bookkeeping with a fake renderer interface, if one exists for `GameCaptureTests`; otherwise a pure `ProbeSchedule` class decides capture: `ShouldCapture(request, hash)` true the first time, false the second, true after the hash changes).
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** Glass: in `transparent.frag` replace the sky-cube reflection with the probe lookup used by `lighting.frag` (box-corrected, blended), falling back to the cube.
- [ ] **Step 4: Tests pass; Courtyard: one probe centred on the yard, box (-3, -1, -10) to (10, 6, 7); screenshot `shots/t04-probe.png`; the window glass should now reflect the dry-stone wall, not just sky**
- [ ] **Step 5: Commit** `feat: view.ReflectionProbe, and glass reflects the probes`

### Task 5: Multiscatter GGX

**Files:**
- Modify: `src/Rendition/Shaders/brdf.glsl` (`multiscatterCompensation(F0, NoV, roughness)` after Fdez-Agüera 2019 using the existing analytic `environmentBrdf`), `lighting.frag` (multiply direct and indirect specular by it)
- Docs: `docs/design.md` BRDF paragraph

- [ ] **Step 1: There is no CPU-side seam to test. Verification is visual: in `--lineup`, the rusty-metal sphere's rough steel must get brighter, not darker, than before at grazing angles**
- [ ] **Step 2: Implement**
- [ ] **Step 3: Screenshot the lineup before and after into `shots/t05-lineup-{before,after}.png`; the rough metal rows must be brighter**
- [ ] **Step 4: Commit** `feat: rough metals keep the energy they used to lose`

## Phase 2: Sky, air and camera

### Task 6: Preetham sky with a sun disc

**Files:**
- Create: `src/Rendition/Shaders/sky.glsl` (`preethamRadiance(dir, sunDir, turbidity)`), `src/Rendition/Passes/SkyModel.cs` (pure: the Perez coefficients and zenith values from turbidity, as the shader computes them)
- Modify: `src/Rendition/Shaders/environment.comp` and the background draw in `lighting.frag` (use `sky.glsl` when `turbidity > 0`), `src/Rendition/Types.cs` (`FrameView.Sky.Turbidity`), `src/Super/Sky.cs` (`SkySettings.Turbidity`), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/SkyModelTests.cs`
- Docs: `docs/rendering.md` Sky section; `docs/design.md`

**Interfaces:**
- Produces: `SkySettings.Turbidity` (float, 0 = gradient sky, 1.7–10 analytic; other values throw with a message giving the range); `SkyModel.Perez(float turbidity) → (Vector3 A, B, C, D, E)`; `SkyModel.Zenith(float turbidity, float sunZenithAngle) → Vector3 (Y, x, y)`; the sun disc uses `ShadowSettings.LightAngle` as its angular radius and the sun's colour as its radiance over that disc.
- `Brightness` still scales everything. `Zenith`, `Horizon`, `Ground` are ignored when `Turbidity > 0`; `Ground` is kept for below the horizon.

- [ ] **Step 1: Tests:** `Zenith_luminance_for_turbidity_2_with_the_sun_60_degrees_up_matches_preetham` (hand-worked value from the paper's zenith formula, tolerance 1%); `A_clearer_sky_is_bluer_at_the_zenith` (chromaticity y at turbidity 2 < at 6); `Turbidity_outside_1_7_to_10_is_refused`; `Turbidity_zero_keeps_the_gradient` (`SceneRenderer.Surroundings` still passes the three colours).
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** The environment bake runs `sky.glsl` per texel when turbidity is set and includes the sun disc, so the irradiance cube carries the sun's contribution only through the sky model's circumsolar term, not the disc (clamp the disc out of the irradiance pass, keep it in the reflection cube's mip 0).
- [ ] **Step 4: Tests pass; Courtyard with `Turbidity = 2.5f`: `shots/t06-sky.png`; the sky should brighten towards the sun at the top left and deepen overhead**
- [ ] **Step 5: Commit** `feat: an analytic clear sky with a sun disc, from SkySettings.Turbidity`

### Task 7: Height fog with sun in-scatter

**Files:**
- Create: `src/Rendition/Passes/Fog.cs` (pure: `Pack(in FogSettings) → Vector4 (density, height, falloff, sunScatter)`, and `Transmittance(float density, float height, float falloff, Vector3 from, Vector3 to) → float`, the analytic integral the shader uses)
- Modify: `src/Rendition/Shaders/common.glsl` (frame fog uniform), `lighting.frag`, `transparent.frag`, `particle.frag` (apply after lighting), `src/Rendition/Types.cs`, `src/Super/Sky.cs` (`SkySettings.Fog`), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/FogTests.cs`
- Docs: `docs/rendering.md` ("Fog" after "Haze"), `docs/design.md`

**Interfaces:**
- Produces: `public readonly record struct FogSettings(float Density = 0f, float Height = 0f, float Falloff = 0.1f, float SunScatter = 0.5f)`; `SkySettings.Fog`; `Graphics.Fog` (bool, default true; false sends density 0); fog colour is the sky in the view direction (the cube), brightened by `SunScatter × pow(max(dot(view, sun), 0), 8)` times the sun colour.

- [ ] **Step 1: Tests:** `No_density_means_full_transmittance`; `Level_fog_follows_beer_lambert` (density 0.1 over 10 m at the fog height → `exp(-1)`); `Fog_thins_with_height` (a ray 20 m up transmits more than one at the height, same length); `Negative_density_or_falloff_is_refused`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass; Courtyard with `Fog = new(0.004f, 0f, 0.15f, 0.6f)`: `shots/t07-fog.png`; the far wall should sit a touch behind the near flagstones in the air**
- [ ] **Step 5: Commit** `feat: exponential height fog lit by the sun`

### Task 8: Auto exposure

**Files:**
- Create: `src/Rendition/Shaders/exposure_histogram.comp`, `exposure_adapt.comp`, `src/Rendition/Passes/Exposure.cs` (pure: `Target(ReadOnlySpan<uint> bins, float minEv, float maxEv) → float` mean EV of the 40th–95th percentile; `Adapt(float current, float target, float speedStopsPerSecond, float dt) → float`)
- Modify: `src/Rendition/Shaders/tonemap.frag` (multiply by the adapted exposure buffer when enabled), `src/Rendition/Renderer.cs` (dispatch before tonemap), `src/Rendition/Types.cs`, `src/Super/PostSettings` in `src/Super/Sky.cs` or wherever `PostSettings` lives (`AutoExposure`, `AdaptationSpeed`), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/ExposureTests.cs`
- Docs: `docs/rendering.md` Finish section; `docs/design.md`

**Interfaces:**
- Produces: `PostSettings.AutoExposure` (bool, default false), `PostSettings.AdaptationSpeed` (stops per second, default 2, must be > 0), `Exposure` becomes compensation in stops when on. Histogram: 64 bins over −8..+16 EV of the lit HDR frame before bloom. The first frame after enabling, or after a scene restart, snaps to the target (no adaptation from black).

- [ ] **Step 1: Tests:** `A_frame_all_in_one_bin_targets_that_bin` (bin 32 only → EV of bin 32's centre); `Target_ignores_the_darkest_40_and_brightest_5_percent` (half the pixels in bin 0, the rest in bin 40 → the EV of bin 40); `Adaptation_moves_at_most_speed_times_dt_stops` (current 0, target 3, speed 2, dt 0.5 → 1); `Adaptation_snaps_when_told` (`Adapt(float.NaN, 3, 2, dt)` → 3); `Sixty_fixed_steps_from_the_same_frame_give_the_same_exposure_every_run` (two loops of `Adapt` at dt 1/60 agree bit for bit); `Adaptation_speed_must_be_above_zero`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass; Courtyard with `AutoExposure = true, Exposure = -0.3f`: `shots/t08-exposure.png` twice and `cmp` the files to prove the same bytes (Review Focus 2)**
- [ ] **Step 5: Commit** `feat: auto exposure that meters the frame and adapts like an eye`

### Task 9: AgX tonemapper, temperature and tint

**Files:**
- Modify: `src/Rendition/Shaders/tonemap.frag` (`agx()` with the Blender 4 default look, white balance by a Bradford adaptation matrix built on the CPU), `src/Rendition/Passes/PostGrade.cs` (`WhiteBalance(float temperature, float tint) → Matrix4x4`), `src/Rendition/Types.cs` (`PostSettings.Tonemapper`, `Temperature`, `Tint`), `src/Super` `PostSettings`
- Test: `tests/Super.Tests/PostGradeTests.cs` (add)
- Docs: `docs/rendering.md` Finish table

**Interfaces:**
- Produces: `enum Tonemapper { Aces, Agx }`, `PostSettings.Tonemapper` default `Agx` (the better-looking default; examples that want the old look set `Aces`); `Temperature` and `Tint` in −1..1, 0 neutral, mapped to 4000–12000 K and ±20 Duv; values outside throw naming the field.

- [ ] **Step 1: Tests:** `Neutral_white_balance_is_identity`; `A_warm_temperature_raises_red_over_blue_for_white` (apply to (1,1,1): x > z); `Tint_towards_magenta_lowers_green`; `Temperature_outside_minus_one_to_one_is_refused`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass; Courtyard with `Tonemapper = Tonemapper.Agx, Temperature = 0.08f`: `shots/t09-agx.png`; the bricks in sun should hold detail where ACES went to flat orange**
- [ ] **Step 5: Commit** `feat: AgX tone mapping, temperature and tint`

### Task 10: Render scale

**Files:**
- Modify: `src/Rendition/Renderer.cs` and `src/Rendition/Vulkan/*` target creation (scene size = window × scale), `src/Rendition/Shaders/finish.frag` (resample: bicubic Catmull-Rom when scale ≠ 1), `src/Rendition/Types.cs` (`RenderOptions.RenderScale`), `src/Super/Graphics.cs` (`RenderScale`), `src/Super/GameArguments.cs` (`--render-scale`), `docs/command-line.md`
- Test: `tests/Super.Tests/GraphicsTests.cs`, `tests/Super.Tests/GameArgumentsTests.cs`
- Docs: `docs/rendering.md` Graphics table; `docs/roadmap.md` (remove the render scale gap)

**Interfaces:**
- Produces: `Graphics.RenderScale` float 0.5–2.0 default 1; `--render-scale 1.5`; `Renderer.SceneSize(windowSize, scale) → (int w, int h)` rounding to even numbers.

- [ ] **Step 1: Tests:** `Scene_size_rounds_to_even` (1600×900 at 1.5 → 2400×1350; 1601×901 at 1 → 1600×900); `Render_scale_outside_half_to_two_is_refused`; `Render_scale_is_parsed_from_the_command_line`; `A_changed_render_scale_rebuilds_the_targets_next_frame` via the resize path `TallScreenTests` uses.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** TAA jitter is in scene pixels; the overlay and UI stay at window size.
- [ ] **Step 4: Tests pass; `shots/t10-ss.png` at `--render-scale 2`; grass blades and the window bars should lose their stair-steps**
- [ ] **Step 5: Commit** `feat: Graphics.RenderScale draws the scene smaller or larger than the window`

### Task 11: Depth of field

**Files:**
- Create: `src/Rendition/Shaders/dof_coc.frag`, `dof_blur.frag`, `dof_combine.frag`, `src/Rendition/Passes/DepthOfFieldPass.cs`, `src/Rendition/Passes/ThinLens.cs` (pure: `CircleOfConfusion(depth, focusDistance, aperture, focalLengthMm, sensorMm = 36) → float` in metres on the sensor and `InPixels(coc, imageWidth)`)
- Modify: `src/Rendition/Renderer.cs` (after TAA, before bloom), `src/Rendition/Types.cs`, `src/Super` `PostSettings` (`FocusDistance`, `Aperture`, `FocalLength = 35`), `src/Super/Graphics.cs` (`DepthOfField` switch, default true)
- Test: `tests/Super.Tests/ThinLensTests.cs`
- Docs: `docs/rendering.md` Finish table and a "Focus" paragraph; `docs/roadmap.md` (remove DOF from film effects); `docs/cutscenes.md` cross-link

**Interfaces:**
- Produces: `PostSettings.FocusDistance` metres, `Aperture` f-number (0 = off), `FocalLength` mm; `Graphics.DepthOfField`.

- [ ] **Step 1: Tests:** `A_point_at_the_focus_distance_has_no_circle`; `Circle_matches_the_thin_lens_formula` (focus 10 m, f/2.8, 50 mm, point at 5 m → 0.179 mm, hand-worked); `Aperture_zero_turns_it_off` (returns 0 for every depth); `Negative_focus_distance_is_refused`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** Half-resolution near and far layers, 48-tap disc gather weighted by circle size with a max of 16 px, far layer composited by circle, near layer dilated 2 px, bilinear upsample.
- [ ] **Step 4: Tests pass; Courtyard `FocusDistance = 11f, Aperture = 8f`: `shots/t11-dof.png`; the near flagstones should soften a little, the window stay crisp**
- [ ] **Step 5: Commit** `feat: depth of field from a thin lens`

## Phase 3: Surfaces that remember weather

### Task 12: Shaders in sidecars, and alpha from graphs

**Files:**
- Modify: `src/Super.Cook.Models/ImportSettings.cs` (`MaterialRule.Shader`, `ShaderParameters` (`float[][]`, ≤ 8 of 4), `ShaderTextures` (≤ 4 paths)), `src/Super.Cook.Models/ModelCooker.cs` (write them into the model's material part), `src/Super/Content.cs` (model materials resolve the shader by name and the textures by path at load), `src/Super.Cook.Models/ModelCooker.cs` (`alphaCutoff` with a `graph`: the cutoff reads the graph's opacity, written into the colour's alpha)
- Test: `tests/Super.Cook.Tests/ModelShaderRuleTests.cs`
- Docs: `docs/content.md` sidecar table; `docs/rendering.md` "A material graph on a model"; `docs/materials.md` (Opacity with alphaCutoff)

**Interfaces:**
- Produces: sidecar `"shader": "puddles"`, `"shaderParameters": [[...]]`, `"shaderTextures": ["../textures/ripples.png"]`; graph `Opacity` becomes the colour part's alpha when the rule has `alphaCutoff`.

- [ ] **Step 1: Tests:** `A_shader_rule_names_the_shader_in_the_cooked_material` (cook a one-material glb from the test fixtures with the rule, read the material part); `More_than_eight_parameters_is_refused` (message contains "8"); `A_ninth_parameter_component_is_refused` (a parameter of 5 numbers, message names "4"); `A_graph_with_alpha_cutoff_writes_its_opacity_into_the_colour_alpha` (a fixture graph with `Opacity = Gray.Of(0.25f)` → alpha byte 64 across the colour part).
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass**
- [ ] **Step 5: Commit** `feat: sidecar rules name a surface shader, and a graph's opacity drives alphaCutoff`

### Task 13: Projected decals

**Files:**
- Create: `src/Rendition/Passes/DecalPass.cs`, `src/Rendition/Shaders/decal.vert`, `decal.frag`, `src/Rendition/Passes/DecalBatch.cs` (pure: packs decals to the instance buffer, enforces the limit)
- Modify: `src/Rendition/Renderer.cs` (after GBufferPass: copy RT1 to `NormalCopy`, run decals), `src/Rendition/Types.cs` (`DecalDraw`, `DecalBlend`), `src/Super/View.cs` (`Decal(int key, Matrix4x4 box, Surface material, DecalBlend blend = default)`), `src/Super/Content.cs` (validate the decal's surface like any surface), `src/Super/Rendering/SceneRenderer.cs`
- Test: `tests/Super.Tests/DecalTests.cs`
- Docs: `docs/views.md` "Decals"; `docs/rendering.md` (replace the dithered-quad advice with decals, keep `dithered` documented); `docs/design.md` "Decals" (pass order, why the normal is copied)

**Interfaces:**
- Produces: `public readonly record struct DecalBlend(float Color = 1f, float Normal = 1f, float Roughness = 1f, float AngleFade = 0.5f)`; `View.Decal(...)`; `Graphics.Decals` (bool, default true; false skips the pass); `DecalBatch.MaxDecals = 256`; projection down the box's local −Y, UV from local XZ over −0.5..0.5; pixels without depth are discarded; surfaces whose normal is more than `acos(AngleFade)` from the box's up fade to nothing; weights are the material alpha × blend weights.

- [ ] **Step 1: Tests:** `A_257th_decal_is_refused` (message contains "256"); `Blend_weights_outside_0_to_1_are_refused` naming the field; `A_decal_matrix_that_is_not_invertible_is_refused`; `Decals_pack_the_inverse_box_and_the_weights` (`DecalBatch.Pack` of a translated box → inverse has the negated translation; weights copied).
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** `decal.frag` reconstructs the world position from depth, transforms into the box, discards outside or where depth is the far plane (Review Focus 3), samples the material's colour, surface and normal textures with the material's `baseColor`/`roughness` factors, writes RT0 with alpha blending at `alpha × Color`, and writes RT1 whole: normal = RNM of the decal's tangent-space normal over `NormalCopy`, lerped by `alpha × Normal`; roughness lerped by `alpha × Roughness`; AO copied.
- [ ] **Step 4: Tests pass; a quick check in the Courtyard: replace the three stain quads with three `view.Decal` calls using the same grime surfaces and compare `shots/t13-decals.png` with the baseline: the bricks' bumps must show through the damp**
- [ ] **Step 5: Commit** `feat: projected decals that blend colour, roughness and normal onto what is under them`

### Task 14: Puddles and wetness

**Files:**
- Modify: `src/Rendition/Shaders/surface.glsl` (`applyPuddles(Surface, worldPos, height)` called from `materialSurface()` when `MATERIAL_PUDDLES`), `material.glsl` (flag), `common.glsl` (frame `puddleLevel`, `puddleBand`), `src/Rendition/Types.cs` (`FrameView.Puddles`), `src/Super/View.cs` (`Puddles(float level, float band = 0.08f)`), `src/Super/Surface.cs` (`Puddles` bool), `src/Super.Cook.Models/ImportSettings.cs` (`MaterialRule.Puddles`), `ModelCooker.cs`
- Create: `src/Rendition/Passes/Puddles.cs` (pure: `Pack(level, band) → Vector4`; `Wetness(float surfaceHeight, float level, float band) → (float water, float damp)` the same curve as the shader)
- Test: `tests/Super.Tests/PuddleTests.cs`, `tests/Super.Cook.Tests` (rule round-trips)
- Docs: `docs/rendering.md` new "Puddles" section; `docs/content.md` rule; `docs/design.md`; `docs/roadmap.md` (remove the puddle shader gap)

**Interfaces:**
- Produces: `View.Puddles(level, band)`; rule `"puddles": true`; `Surface.Puddles`; `Graphics.Puddles` (bool, default true; false leaves every surface dry); surface height for the test is `worldPos.y + (height − 1) × heightDepth` so a groove sits below its face (Review Focus 4). Water: normal = up blended with two scrolling procedural ripple normals (`frame.time`, 0.4 and 0.9 Hz, 3 mm amplitude), roughness 0.03, metallic 0, colour = `surfaceColor × mix(1, vec3(0.35, 0.3, 0.25), saturate(depth / 0.05))`. Damp: colour squared and roughness × 0.5 at the waterline, linear to dry over `band`.

- [ ] **Step 1: Tests:** `Below_the_level_is_water` (height −0.02, level 0 → water 1, damp 1); `Half_a_band_up_is_half_damp`; `Above_the_band_is_dry`; `A_groove_in_a_high_wall_never_floods` (worldY 2, height 0, depth 0.03, level 0 → water 0, damp 0; the helper that computes surface height is the thing under test); `A_negative_band_is_refused`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement.** No `puddleLevel` set means the flag does nothing.
- [ ] **Step 4: Tests pass; Courtyard: `"puddles": true` on `graph_dirt`, `graph_flagstone`, `graph_wood` (plank and deck), `graph_hoop`, `cy_wheel_wood`; delete the `cy_water` mesh from the script; `view.Puddles(-0.045f)`; `shots/t14-puddles.png`; the puddle must reflect the wall and sky with ripples, and the mud must darken towards it**
- [ ] **Step 5: Commit** `feat: puddles and a damp band on any surface that lies below the water level`

### Task 15: Foliage translucency and wind

**Files:**
- Modify: `src/Super.Cook.Models/ImportSettings.cs` (`Foliage` becomes `FoliageRule? { Translucency = 0.6 }`, with `"foliage": true` still accepted; `Wind` bool), `ModelCooker.cs`, `src/Rendition/Shaders/lighting.frag` (per-material translucency replaces `FOLIAGE_TRANSLUCENCY`; stored in the G-buffer's shared channel as today's negative number scaled), `gbuffer.vert` and `shadow.vert` (`windSway()`), `instances.glsl` (per-draw bounds min Y, already known to culling), `common.glsl` (frame wind), `src/Rendition/Types.cs` (`FrameView.Wind`), `src/Super/View.cs` (`Wind(Vector3 direction, float strength, float gustiness = 0.5f)`), `src/Super/Surface.cs` (`Foliage` translucency, `Wind`)
- Create: `src/Rendition/Passes/Wind.cs` (pure: `Pack`, and `Sway(Vector3 position, float heightAboveBase, Vector3 direction, float strength, float gustiness, float time) → Vector3` the same function as the shader)
- Test: `tests/Super.Tests/WindTests.cs`, `tests/Super.Cook.Tests` (foliage rule round-trip)
- Docs: `docs/rendering.md` Foliage paragraph and a "Wind" paragraph; `docs/content.md`; `docs/design.md`

**Interfaces:**
- Produces: `"foliage": { "translucency": 0.8 }`; `"wind": true`; `View.Wind(...)`; `Graphics.Wind` (bool, default true; false sends strength 0); sway = `direction × strength × h² × (0.6 + 0.4 × gust(worldXZ, time))` where `h` is height above the draw's base in metres clamped to 1 and `gust` is value noise scrolling along the direction at 1.5 m/s, plus a 0.02 m flutter at 4 Hz on `h`. Shadows sway the same.

- [ ] **Step 1: Tests:** `No_strength_is_no_sway`; `Sway_grows_with_the_square_of_height` (h 1 ≈ 4 × h 0.5 at the same gust); `Sway_never_moves_the_base` (h 0 → zero); `The_same_time_and_place_sway_the_same` (determinism); `Translucency_outside_0_to_1_is_refused` in the cook with a message naming the material.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass; `--record shots/t15-wind.mp4 --seconds 4` of the Courtyard with wind on the grass, ivy and tree; the frames must show the blades move and their shadows follow**
- [ ] **Step 5: Commit** `feat: foliage sets its own translucency, and wind sways anything flagged for it`

### Task 16: Baked wear and dirt masks

**Files:**
- Create: `src/Super.Cook.Models/MaskBaker.cs` (`Bake(MeshData mesh, int size) → byte[] RGBA`: red curvature, green occlusion, blue height above the mesh's base, alpha coverage; UV overlap detection), `src/Super.Cook.Models/Bvh.cs` (triangle BVH for the occlusion rays)
- Modify: `ImportSettings.cs` (`BakeMasks` bool, `Wear` and `Dirt` as `LayerRule { Color float[], Roughness float?, Amount float }`), `ModelCooker.cs` (bake per material with the rule, write `#mask`, pass layer values), `src/Rendition/Shaders/material.glsl` (`maskTexture`, `wear`, `dirt` vec4s; the `GpuMaterial` grows by 3 vec4 and 1 scalar, update `GpuMaterialTests`), `surface.glsl` (`applyLayers()` in `materialSurface()`), `src/Rendition/Vulkan/ResourceTable.cs`
- Test: `tests/Super.Cook.Tests/MaskBakerTests.cs`, `tests/Super.Tests/GpuMaterialTests.cs`
- Docs: `docs/content.md` rules; `docs/rendering.md` "Wear and dirt"; `docs/design.md` "Mask bakes" (curvature from the normal divergence across each triangle fan, 64 cosine rays over a BVH, 1024², 4 px dilation); `docs/roadmap.md` (curvature and occlusion bakes are done as masks; graph inputs still not)

**Interfaces:**
- Produces: `"bakeMasks": true`, `"wear": { "color": [0.8, 0.75, 0.7], "roughness": 0.4, "amount": 0.6 }`, `"dirt": { "color": [0.2, 0.17, 0.14], "roughness": 0.95, "amount": 0.7 }`; shader: wear mask = `smoothstep(0.55, 0.8, curvature) × amount`, dirt mask = `max(1 − occlusion, 1 − heightAboveBase / 0.3) × amount × (1 − wear)`; colour and roughness lerp to the layer's.

- [ ] **Step 1: Tests:** `A_cube_bakes_high_curvature_along_its_edges_and_none_on_its_faces` (bake a unit cube with a 6-island UV layout; the texel at a face centre has red ≈ 0.5, a texel 2 px from an island's edge has red > 0.8); `A_cube_inside_a_bigger_open_box_is_occluded_on_the_side_facing_the_wall` (green lower than the free side by at least 0.3); `Height_above_base_is_zero_at_the_bottom_and_one_at_the_top`; `Overlapping_UVs_fail_the_cook_naming_the_material` (two triangles on the same UV square; message contains the material name and "overlap") (Review Focus 5); `GpuMaterial` layout test updated with the new offsets, worked by hand.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass; Courtyard: `bakeMasks` with `wear` and `dirt` on `graph_wood` (barrel, deck, plank, crates are Sketchfab: on `cy_crate*` too), `graph_hoop`, `graph_stone` (plinth); the script gives these meshes a Smart UV Project second pass only where UVs overlap (box UVs already don't); `shots/t16-wear.png`; the barrel's stave edges should pale and its hollows darken**
- [ ] **Step 5: Commit** `feat: the cooker bakes curvature, occlusion and height masks, and materials wear and dirty by them`

## Phase 4: The graphs

Every graph task: edit the `.material.cs`, run `dotnet run -c Release --project examples/Courtyard -- --lineup --screenshot shots/t<N>-lineup.png --seed 1 --ticks 60 --size 1600x900`, cook the model and take the standard shot, run `tests/Courtyard.Tests` (no warnings allowed), update the README's table row, commit. The tests already prove the graphs cook clean; the lineup and the scene prove the look. Record the cook time of each changed graph at its size in the commit message.

### Task 17: Bricks

**Files:** `examples/Courtyard/content/materials/bricks.material.cs`, `brick-face.material.cs`, `courtyard.glb.cook.json` (`graphSize` 4096 for `graph_bricks`), README row.

- [ ] Hue: per-brick colour from a ramp of eight stops spanning dark plum `#4a2a26`, red `#8a3f32`, orange-brown `#9c5a3c`, ochre `#a8784a`, pale buff `#b89a72`, with saturation reduced about 20% from today, weighted so a third of bricks are the two middle reds. Prove in the lineup that no two neighbouring bricks share a tone in a 4×4 patch.
- [ ] Efflorescence: white veil `#d8d4cc` from a low-frequency Perlin through `HistogramScan` at position 0.78, contrast 0.5, only on bricks whose hardness is below 0.4, opacity 0.35.
- [ ] Soot: darken by 25% in a band from a `Gradient.Linear` down the tile (under the string course), broken by directional noise.
- [ ] Mortar: deepen `Depth` to 0.02 m and raise the mortar's own Perlin to ±30% so joints are uneven; recessed mortar in 15% of joints by a cells mask.
- [ ] Roughness: hard bricks 0.55–0.65, soft 0.8–0.95, mortar 0.9, efflorescence 0.95.
- [ ] Chips: a second `SlopeBlur` pass with larger cells at amount 0.05 on 20% of bricks.
- [ ] Screenshot `shots/t17-bricks.png`; commit `feat: the Courtyard's bricks range from plum to buff, with efflorescence, soot and uneven mortar`.

### Task 18: Stone and flagstones

**Files:** `stone.material.cs`, `flagstones.material.cs` (now used for the path, replacing the plain stone tint), sidecar (`graph_flagstone` → `flagstones.material.cs` at 4096 with `puddles`), README.

- [ ] Flagstones: chamfered and chipped edges (distance from the joint through a `Curve`, with `SlopeBlur` nicks), 1–2 hairline cracks across a third of the stones from warped cell edges, dirt and fine gravel filling the joints (`HeightBlend` with a grit height), moss only in joints and on the low half of each stone by its own height, a per-stone tone from `FloodFill` across five greys and a warm sandstone, roughness 0.7–0.9 with polished 0.5 on the stones' crowns where feet go (a broad `Gradient.Radial` mask).
- [ ] Plinth stone: keep, add a 1 cm bevel highlight on the arrises via `EdgeDetect` and lighten the height's peaks 5%.
- [ ] Screenshot `shots/t18-stone.png`; commit `feat: the flagstones chip, crack, hold dirt in their joints and wear smooth where feet go`.

### Task 19: Ground

**Files:** `mud.material.cs` (`Width` 4 m), new `gravel.material.cs` (0.5 m, for decals and the path edges), sidecar, README, `art/courtyard.py` (decal boxes for gravel along the flagstones' edges, written to `content/decals.json`).

- [ ] Mud: clods twice as coarse, three pebble sizes, flecks of straw (`Noise.Directional` short strokes, pale), a dark organic layer from Perlin at 0.3 opacity, leaf litter stamps via `Splatter` of a leaf `Shape`, roughness 0.75 dry to 0.3 in the hollows, colour range widened from `#3a2a1c` to `#7a6248`.
- [ ] Gravel graph: `Splatter` of 400 discs 2–8 mm in three greys with a sand fill, used as a decal with `Normal 1, Color 0.9` along both arms of the path and round the barrel.
- [ ] Screenshot `shots/t19-ground.png`; commit `feat: coarser, littered mud and gravel that spills off the path`.

### Task 20: Wood and iron

**Files:** `wood.material.cs`, `hoop-iron.material.cs`, README, `art/courtyard.py` (nail heads on the deck and crate frames as small lathed discs with `graph_hoop`).

- [ ] Wood: per-board tone from `FloodFill` of the plank grid across six stops from grey-silver `#8c8678` to warm `#8a5a34`; knots (`Splatter` of a radial gradient, 2–4 per metre, that also warps the grain locally with `Warp`); checks (short cracks along the grain from `Noise.Directional` through a narrow `Levels`); silvering: a weathering mask from Perlin that desaturates and lifts the colour and raises roughness to 0.9; end grain darker. Parameters `Weathering` (0–1) and `Tone` so the barrel staves (`Weathering 0.6`) and deck (`0.8`) use variants.
- [ ] Hoop iron: rust runs below each hoop as a `Noise.Directional` streak mask into the colour (the staves get a `rust-streak` decal under each hoop from the decal list), bare iron sheen in the wear mask, roughness 0.35 on bare metal, 0.9 on rust.
- [ ] Screenshot `shots/t20-wood.png`; commit `feat: boards of different tones, knots, checks and silvering, and hoops that bleed rust`.

### Task 21: Cloth, lime and glass

**Files:** `burlap.material.cs`, `whitewash.material.cs`, `glass.material.cs`, README.

- [ ] Burlap: stains (two Perlin blotches, one dark `#4a3f2e`, one pale flour), a square patch from a `Shape.Square` with its own weave offset, loose threads as thin directional strokes in the normal, dirt gradient from a `Gradient.Linear` (bottoms darker), roughness 0.85–0.95.
- [ ] Whitewash: edges of the flakes irregular (`SlopeBlur` with Perlin at 0.08), exposed brick under the flakes drawn by the brick-face graph's colour logic in a second layer with its height lower by 3 mm, lime run-off streaks beneath each flake.
- [ ] Glass: dirt gathered in the corners of each pane (a `Gradient.Radial` inverted per pane via `Tiles.Grid` and `FloodFill`), two panes with a slightly different ripple seed, one cracked pane (a thin `Distance` line), opacity 0.05–0.8.
- [ ] Screenshot `shots/t21-cloth.png`; commit `feat: stained sacks, flakes that show the brick beneath, and dirtier glass`.

### Task 22: Leaves and blades

**Files:** new `ivy-leaf.material.cs` (0.2 m, opacity cut-out of a five-lobed leaf with veins, two seeds for two sizes), `grass-blade.material.cs` (0.3 m, three species via `Species` parameter: meadow, broad, dry seed-head), `cobweb.material.cs` (0.5 m, opacity threads), `dead-leaf.material.cs`, sidecar (`alphaCutoff 0.5`, `foliage: { translucency }` 0.7 leaves / 0.85 blades, `twoSided`, `wind`), README.

- [ ] Leaf cut-outs: `Shape` unions and `Distance` for the lobes, veins as directional noise ridges in the height, colour from dark `#2f4a1e` at the base to `#6f9a3a` at the tips with 10% yellowing on a Perlin mask, roughness 0.45 with a 0.3 sheen band.
- [ ] Blades: a tapering strip in opacity, a rib in the normal, green to straw along the length, species change width, length ratio and colour.
- [ ] Screenshot of the lineup `shots/t22-leaves.png`; commit `feat: leaf, blade, cobweb and dead-leaf graphs cut out by their opacity`.

## Phase 5: The scene

### Task 23: The model: cards, species, clutter, decals and UVs

**Files:** `examples/Courtyard/art/courtyard.py`, `content/decals.json` (written by the script), `content/models/courtyard.glb` (re-exported), sidecar.

- [ ] Ivy: replace `leaf()` stars with quads 6–12 cm wearing `ivy-leaf`, two sizes, each rotated about its stem by ±25° and tilted off the wall 10–40°, denser low on the stems and sparser at the tips, with 8% dead leaves in a brown tint; stems keep the tube geometry.
- [ ] Grass: `blade()` becomes a card 1–4 cm wide wearing `grass-blade` species by position (meadow by the walls, broad by the puddle's bank, dry on the deck's edge), tuft sizes from 0.1 to 0.5 m, 1 in 10 tufts a seed-head; weeds: single blades and a dandelion rosette (five leaf cards) in 20 flagstone joints and against the plinth.
- [ ] Clutter: 40 more dead leaves in three tints, 12 twigs (thin tubes), 6 more brick fragments, a few pebbles half in the puddle (below the level), a snail shell is not needed.
- [ ] Cobwebs: two triangular cards in the window's upper corners and one between the drainpipe and the wall.
- [ ] Nails: 2 per board end on the deck and crates, 6 mm discs 1 mm proud, `graph_hoop`.
- [ ] UVs: Smart UV Project (angle 66°, margin 0.002) on the barrel, plank, deck and the plinth blocks that take `bakeMasks`.
- [ ] Decals: the script writes `content/decals.json` as a catalog of `{ "name", "position", "rotation", "size", "material", "blend" }` for: damp band along the wall's foot (replacing the quad), sill drip, two anchor rust runs, rust under each hoop, a dirt ring under the barrel, the wheel, each crate and each sack (0.1 m larger than the footprint, `gravel` or `grime`), moss patches on the plinth's north faces and the dry wall's damp course (a `moss` variant of `grime` with `Rust false` and a green tint), water stains under the string course every 1.5 m, the gravel strips from Task 19, and six boot prints on the flagstones near the puddle as roughness-only decals (`Color 0.3, Normal 0, Roughness 1` with a wet grime surface).
- [ ] `CourtyardGame.Load` reads `decals.json` via the JSON content API from `docs/content.md`, resolves each material through `content.Surface`, and `Draw` issues `view.Decal` for each.
- [ ] `tests/Courtyard.Tests`: `Every_decal_names_a_cooked_material` (every `material` in the catalog is in the manifest) and `No_two_decals_share_a_name`.
- [ ] Commit `feat: the Courtyard grows leaf and blade cards, weeds, cobwebs, nails, more clutter and a decal list`.

### Task 24: The light, the air and the camera

**Files:** `examples/Courtyard/CourtyardGame.cs`, README's lighting paragraphs.

- [ ] Sky `Turbidity 2.5`, `Brightness 1`; sun direction unchanged, colour re-set so a sunlit flagstone meters at about 0.7 after auto exposure; `Fog = new(0.004f, -0.1f, 0.15f, 0.6f)`.
- [ ] `view.LightProbes(new(-2f, -0.5f, -9f), new(9f, 5f, 6f), 1f)`; `view.ReflectionProbe(3, new(3.5f, 1.5f, -1.5f), new(-3f, -1f, -10f), new(10f, 6f, 7f))`.
- [ ] `view.AmbientOcclusion(0.8f, 1f)`; `view.Shadows(7f, 3, sunSize: 0.02f, contact: 0.3f)`; `graphics.ShadowResolution = 4096` behind `--size` wider than 1920 only, else 2048.
- [ ] `view.Puddles(-0.045f, 0.1f)`; `view.Wind(new(0.6f, 0f, -0.3f), 0.35f)`.
- [ ] Finish: start from what Tasks 8 and 9 tuned (`AutoExposure true, Exposure 1.05, Tonemapper Agx, AgxLook Punchy, Contrast 0.25, Saturation 0.15`), add `Temperature 0.08, Tint 0, Vignette 0.3, Sharpen 0.15, Bloom 0.05, FocusDistance 11, Aperture 8, FocalLength 35`; lift and gain cleared. Retune Exposure so the frame meters near luma 108, not by the old ACES numbers.
- [ ] Volumetric fog (Task 26): pick the density and `VolumetricDistance` so the far wall sits in the air and shafts show only where the sun grazes past the wall; `Upscaler` as Task 28 decided.
- [ ] Lamp and room lights: lamp `color` halved now that exposure adapts; room light unchanged.
- [ ] Screenshot `shots/t24-final.png` and `--render-scale 2` `shots/t24-final-ss.png`; `--script plain` for the clay check; `--record shots/t24.mp4 --seconds 6` for the ripples and wind. Validation count must stay 0.
- [ ] Commit `feat: the Courtyard is lit by a clear sky, bounced light and an adapting camera`.

### Task 25: Media, docs and roadmap

**Files:** `docs/media/courtyard-realism.webp` (whole frame before and after), `courtyard-realism-wall.webp`, `-puddle.webp`, `-props.webp`, `-plants.webp` (crops before and after with labels), `examples/Courtyard/README.md`, `docs/rendering.md` intro bullet list, `docs/roadmap.md`, `docs/design.md` "Known simplifications".

- [ ] Make each image with ffmpeg `hstack` and `drawtext` from `shots/plan-base.png` and `shots/t24-final.png`, `-c:v libwebp`, about 1200 px wide; look at every image and confirm the labels are true.
- [ ] README: rewrite the lighting paragraph, add the decal list, the puddle rule, the baked masks and the new graphs to the files table.
- [ ] `rendering.md` intro bullets: bounced light from probes, contact shadows, GTAO, a clear-sky model, height fog, volumetric fog, auto exposure, AgX, depth of field, decals, puddles, wind, wear and dirt masks, render scale, motion vectors, FSR.
- [ ] Retake `docs/media/material-lineup.webp` once, now that the graphs are final (moved here from Task 18).
- [ ] Roadmap: remove what shipped, add "probes are static and recapture only on a change", "no parallax self-shadow", "bounced light doesn't follow moving lights or objects (next step: probes that update at runtime, DDGI; a surface cache like Lumen later)", "no DLSS".
- [ ] Commit `docs: the Courtyard before and after, and what the renderer learned`.

## Phase 6: Light in the air, and upscaling

Added on 2026-11-09 after comparing the renderer with what Unity is shipping. These run
before Task 24, so the final light pass tunes them: Task 26 after Task 10, Task 27 after
Task 15 (wind moves vertices) and Task 28 after Task 27. Bits: `VolumetricFog` takes the
next free `RenderEffects` bit (13 unless another task took it first).

### Task 26: Volumetric fog

**Files:**
- Create: `src/Rendition/Passes/VolumetricFog.cs` (pure: `GridSize(int sceneWidth, int sceneHeight) → (int x, int y, int z)` at one cell per 8×8 scene pixels and 64 slices; `SliceDepth(int slice, int slices, float near, float far) → float` and `SliceOf(float depth, …) → float`, an exponential split; `Phase(float cosTheta, float anisotropy) → float` Henyey-Greenstein; `Integrate(…)`, the CPU mirror of the front-to-back sum), `src/Rendition/Shaders/volumetric_inject.comp`, `volumetric_integrate.comp`, `volumetric.glsl` (lookup), `src/Rendition/Vulkan/VolumetricFogVolume.cs` (two 3D targets, last frame's for history)
- Modify: `src/Rendition/Shaders/fog.glsl` (inside `VolumetricDistance` the volume replaces the analytic fog; past it the analytic fog continues from the volume's transmittance), `lighting.frag`, `transparent.frag`, `particle.frag`, `src/Rendition/Renderer.cs` (record after the shadow pass, before lighting), `src/Rendition/Types.cs` (`FogSettings.Anisotropy`, `VolumetricDistance`; `RenderEffects.VolumetricFog`), `src/Super/Graphics.cs`, `src/Super/Sky.cs`
- Test: `tests/Super.Tests/VolumetricFogTests.cs`, `GraphicsTests.cs`
- Docs: `docs/rendering.md` ("Fog" gains a volumetric paragraph and the Graphics row), `docs/design.md`

**Interfaces:**
- Consumes: `FogSettings` and `Fog.Transmittance` (Task 7); sun cascades; light list and spot shadow maps; `frame.previousViewProjection`.
- Produces: `FogSettings.Anisotropy` (−0.9..0.9, default 0.6, forward scattering), `FogSettings.VolumetricDistance` (metres, default 48, 0 = analytic only); `Graphics.VolumetricFog` (bool, default true; off falls back to Task 7's analytic fog exactly); lights add to the volume by default, with `Light.Volumetric` strength (0..∞, default 1).
- Inject per cell: density from the height fog, light = sun × sun shadow × phase + sky ambient + each point and spot light × its shadow where it has one × phase. Cell depth is jittered by a fixed 8-step sequence from the frame index and blended 90% with the reprojected history; a cell whose history falls off screen takes the new value. Fixed frame index → identical shots.

- [ ] **Step 1: Tests:** `Slices_cover_near_to_far_and_invert` (`SliceOf(SliceDepth(i))` = i for all i; slice 0 at near, slice n at far); `Isotropic_phase_is_one_over_four_pi`; `Phase_integrates_to_one_over_the_sphere` (numeric sum, g 0.6 and −0.6, within 1%); `Forward_scattering_is_brighter_towards_the_light` (g 0.6: θ 0 > θ 90°); `A_uniform_unlit_medium_matches_beer_lambert` (`Integrate` of density 0.1 over 10 m → transmittance exp(−1)); `Grid_follows_the_scene_size_not_the_window` (1600×900 → 200×113×64; at render scale 2 → 400×225×64); `Anisotropy_outside_range_is_refused` (message names the field, checked under sv-SE); `Volumetric_fog_off_is_the_analytic_fog` (switch maps to the bit; `Fastest()` turns it off).
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass. Courtyard with `VolumetricDistance = 48, Anisotropy = 0.6` and density raised for the shot only to 0.02: `shots/t26-volumetric.png`; aim a camera for the shot only towards the sun past the wall and ivy, `shots/t26-shafts.png`; the shafts must follow the shadow edges and the wall lamp must have a soft halo. Put the density back. Crypt before and after: the torches gain halos, nothing else moves. Two runs byte-identical (`cmp`). Validation count 0. Add `docs/media/volumetric-fog.webp`, before and after with labels.**
- [ ] **Step 5: Commit** `feat: volumetric fog with sun shafts and lamp halos`

### Task 27: Motion vectors

**Files:**
- Create: `src/Rendition/Passes/Motion.cs` (pure: `Pack(Vector2 ndcNow, Vector2 ndcBefore) → Vector2` and the decode the shader uses)
- Modify: `gbuffer.vert`/`gbuffer.frag` (an RG16F motion target: each vertex placed twice, with this frame's and last frame's model matrix, bones and `windSway` time), `src/Rendition/Vulkan` G-buffer targets, `instances.glsl` (last frame's model matrix per instance), skinning (last frame's palette), `taa.frag` (reads the motion target; camera-only reprojection stays as the fallback where it's cleared), `common.glsl` (`previousTime`), `src/Rendition/Renderer.cs` (keeps last frame's matrices per entity key)
- Test: `tests/Super.Tests/MotionTests.cs`
- Docs: `docs/design.md`; `docs/roadmap.md` (remove "no motion vectors")

**Interfaces:**
- Consumes: Task 15's `Wind.Sway(… time)`; the skinned palette from animation.
- Produces: a motion target in scene pixels, the input Task 28's upscaler needs; no new switch (TAA's `Graphics.AntiAliasing` covers it). A draw that appears this frame has no history and writes camera-only motion.

- [ ] **Step 1: Tests:** `A_still_object_under_a_still_camera_has_no_motion`; `Moving_right_one_pixel_is_one_pixel` (hand-worked at 1600×900); `A_new_draw_falls_back_to_camera_motion`; `Swaying_grass_has_motion_where_the_base_has_none` (via `Wind.Sway` at two times).
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass. `--record shots/t27-motion.mp4 --seconds 4` of the Courtyard with wind: the grass and ivy must not smear or ghost under TAA as they did; compare a frame crop with Task 15's recording. Crypt pixel-identical (nothing moves there). Validation count 0.**
- [ ] **Step 5: Commit** `feat: motion vectors for moving, skinned and swaying draws`

### Task 28: FSR upscaling

**Files:**
- Create: `src/Rendition/Vulkan/Fsr.cs` (P/Invoke into AMD FidelityFX SDK's Vulkan `ffx` API: create the context at the scene and window sizes, dispatch with colour, depth, motion, exposure, jitter and reset, destroy on resize), `src/Rendition/Passes/Upscale.cs` (pure: `JitterPhaseCount(float scale) → int`, `Jitter(int frame, int phases) → Vector2`, the Halton sequence the SDK expects, and `Choose(Upscaler wanted, bool libraryLoaded) → Upscaler`), `third_party/fidelityfx/` (the MIT licence and the prebuilt Windows DLL, copied to the output)
- Modify: `src/Rendition/Renderer.cs` (with FSR, its dispatch replaces TAA and the finish resample), `src/Rendition/Types.cs` (`enum Upscaler { Taa, Fsr }`, `RenderOptions.Upscaler`), `src/Super/Graphics.cs` (`Upscaler`), `src/Super/GameArguments.cs` (`--upscaler fsr|taa`), `docs/command-line.md`
- Test: `tests/Super.Tests/UpscaleTests.cs`, `GraphicsTests.cs`, `GameArgumentsTests.cs`
- Docs: `docs/rendering.md` (an "Upscaling" section beside render scale; Graphics row), `docs/design.md`, `docs/roadmap.md` (DLSS listed as not supported)

**Interfaces:**
- Consumes: `Graphics.RenderScale` and `Renderer.SceneSize` (Task 10), the motion target (Task 27), the adapted exposure buffer (Task 8).
- Produces: `Graphics.Upscaler` (`Taa` or `Fsr`). Where the DLL doesn't load (Linux, missing file) `Choose` falls back to `Taa` and logs once. With FSR, render scales below 1 are where it pays: `RenderScale 0.67` is FSR's quality mode.
- Default: decided by the Step 4 crops. If FSR at scale 1 is visibly cleaner than TAA on the grass, ivy and window bars, the default is `Fsr`; otherwise `Taa`. The report shows both crops either way.

- [ ] **Step 1: Tests:** `Jitter_phase_count_follows_the_scale` (hand-worked from AMD's rule, 8 × (1/scale)²: scale 1 → 8, 0.5 → 32); `Jitter_is_halton_two_three` (first three hand-worked values); `A_missing_library_falls_back_to_taa`; `Upscaler_is_parsed_from_the_command_line`; `Fastest_uses_taa`.
- [ ] **Step 2: Run, expect failures**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Tests pass. Courtyard shots at `--upscaler taa` and `--upscaler fsr` at scale 1, and `--upscaler fsr --render-scale 0.67`: `shots/t28-taa.png`, `t28-fsr.png`, `t28-fsr-067.png`; crop the grass and window bars side by side with labels; measure each with `--profile shots/t28-X.txt` and compare the GPU frame times. Two runs byte-identical. Validation count 0. Add `docs/media/fsr.webp`.**
- [ ] **Step 5: Commit** `feat: FSR upscaling, which renders smaller and rebuilds the full picture`

## Self-review notes

- Spec coverage: every row of the spec's table maps to a task (GI 3, AO 1, contact 2,
  shadows 2, reflections 4 and 14, exposure 8, tone 9, sky and fog 6 and 7, DOF 11, AA and
  resolution 10 and 17, decals 13 and 23, wetness 14, moss 23, wear 16, vegetation 15, 22
  and 23, props 20 and 21, bricks 17, small details 23). Insects, ray tracing and
  photogrammetry are listed as out of scope in the spec.
- Names used across tasks: `ProbeVolumeSettings` and `View.LightProbes` (3, 24);
  `DecalBlend`, `View.Decal` (13, 19, 23); `View.Puddles` and rule `puddles` (14, 18, 24);
  `FoliageRule.Translucency`, rule `wind`, `View.Wind` (15, 22, 24); `bakeMasks`, `wear`,
  `dirt` (16, 23); `SkySettings.Turbidity`, `SkySettings.Fog`, `FogSettings` (6, 7, 24);
  `PostSettings.AutoExposure`, `Tonemapper`, `Temperature`, `FocusDistance`, `Aperture`,
  `FocalLength` (8, 9, 11, 24); `Graphics.ShadowResolution`, `RenderScale` (2, 10, 24).
- Review Focus items are pinned: 1 in Task 3, 2 in Task 8, 3 in Task 13, 4 in Task 14,
  5 in Task 16.
