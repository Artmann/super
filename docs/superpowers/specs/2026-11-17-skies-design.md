# Skies: fast updates, a physical sky, night and HDRI

Issue: [#15](https://github.com/Artmann/super/issues/15).

## Why

The sky can't animate. Every change to the sky or the sun waits for the GPU and rebakes
three cube maps in about ten blocking submits, so Hamlet's `DayCycle` steps its sky every
half second, and every change marks all light probes stale. The clear sky (Preetham 1999)
is daytime only: a sun below the horizon is held on it, so there is no sunset glow,
twilight or night. Colony and city builders want day–night cycles, and many games want a
photographed HDRI sky.

The user wants the existing outdoor scenes to benefit too. Courtyard, Forest and Gallery
use the clear sky and should look better with no changes of their own; Hamlet's day should
glide; Archer and Hamlet move from gradients to the new sky.

## Goals

- The sky and sun can change every frame with no hitch and no CPU wait.
- A physical sky driven by the sun, with real sunsets and twilight, that replaces Preetham.
- Distant things fade into the air the way the sky says (aerial perspective).
- A moon disc and stars at night.
- `content.Sky("skies/meadow.hdr")` lights and reflects correctly, with rotation and exposure.

## Non-goals

- Clouds (follow-up).
- Auto-exposure (#20). Hamlet keeps its hand-set exposure curve for night.
- Light probes that follow the sky instantly. They keep their rolling refresh (below).

## Anti-goals

- A scene that looked right before looks worse or noticeably darker/brighter overall with
  the physical sky. Existing scenes keep roughly the same sky brightness through the
  renderer's existing unit scale.
- New per-frame GPU cost for a scene whose sky doesn't change.
- Validation messages.

## Constraints

- Render targets are half floats; the sun disc stays under `SkyModel.MaxDiscRadiance`.
- Chunk bakes already store sky transfer and read the sky's L1 SH (`SkySh`) each frame they
  draw; they must keep working unchanged.
- Every new effect gets a `Graphics` switch; defaults must look best.
- Descriptor setup goes through `Descriptors`; no new Roe duplicates or dead code.
- Docs (`rendering.md` "The sky", `design.md` "Environment lighting") change with the code.

## Approaches

For the per-frame update, three ways were considered:

1. **Keep the current bake, move it into the frame command buffer and time-slice it.** No
   stall, but each rough mip still takes 1024 analytic sky samples per texel; mip 1 alone is
   about 400 million evaluations, so a full refresh would take many frames and lag the sun.
2. **Render mip 0 from the sky, then prefilter each rough mip from the mip chain with
   filtered importance sampling (few samples reading a lower mip), and compute irradiance
   from spherical harmonics.** Cheap enough to redo every frame; the standard approach
   (Unreal's SkyLight realtime capture, Frostbite). Picked.
3. **Drop the cubes and evaluate the sky analytically in the lighting pass.** No good for
   rough reflections or HDRI, and doesn't serve probes.

For the physical sky: Hillaire 2020 (transmittance, multi-scattering and sky-view lookup
tables, plus an aerial-perspective volume) versus Bruneton 2017 (4D precomputed scattering).
Hillaire is cheaper to update when the atmosphere changes, what Unreal ships, and gives aerial
perspective directly. Picked.

Order is the builder's call by ease, as a GitHub stack (`gh stack`), each PR on the one
before, based on `main`:

1. `feat/sky-fast-updates`
2. `feat/physical-sky`
3. `feat/night-sky`
4. `feat/hdri-sky`

## Design

### 1. Fast sky updates

- The environment rebuild is recorded in the frame's command buffer when the sky changes,
  with pipeline barriers instead of `DeviceWaitIdle` and `SubmitAndWait`. Barriers in this
  frame's command buffer already order after earlier frames' reads on the same queue, so the
  cubes need no double buffering.
- Mip 0 of the reflection cube is drawn straight from the sky. Each rougher mip is
  prefiltered with GGX importance sampling that reads a lower mip of the cube chosen from the
  sample's PDF (filtered importance sampling), with a small sample count (32–64).
- Irradiance comes from L2 spherical harmonics projected on the GPU from a small mip, and the
  irradiance cube (or the shaders directly) reads them. `Flat` (the average) is worked out on
  the GPU into a buffer the shaders read, replacing the CPU readback in `CubeCentres.Average`.
- Budget: a full rebuild under about 0.5 ms on the dev GPU at the High preset. If it is over,
  the rough mips are time-sliced across frames; mip 0 and irradiance stay every frame.
- Light probes: a sky change no longer marks every probe. Their rolling refresh (oldest first)
  picks the new sky up, so a slow day–night lags by the refresh period. A jump in the sky
  (a different sky, or a large change in one frame, such as a scene load) still marks them
  all, so it doesn't fade in for seconds. The threshold is the builder's call.
- The gradient sky, panels and studio keep their look: tests compare the new prefilter with
  the old bake within a tolerance.
- Hamlet drops `SkyStep`; its sky glides.
- Docs: drop the "keep colours steady" note in `rendering.md`.

### 2. Physical sky

- Hillaire 2020 in compute: a transmittance LUT (redone only when the atmosphere changes), a
  multi-scattering LUT (same), a sky-view LUT each frame the sun moves, and an
  aerial-perspective froxel volume each frame.
- It replaces Preetham: `Turbidity` above zero turns it on, and maps to the aerosol (Mie)
  density, so Courtyard, Forest and Gallery switch with no changes. `Preetham` code and its
  C# mirror go; `SkyModel` keeps the sun and disc selection.
- The sun may go below the horizon: the sky darkens through twilight to night, and the sun's
  disc is hidden by the planet. Brightness is scaled into the renderer's units so a noon sky
  in today's examples is within a few percent of its Preetham brightness.
- The background draws from the sky-view LUT plus the sun disc; the environment cubes are
  drawn from the same LUT by step 1's path, every frame it changes.
- Aerial perspective: surfaces, glass and particles fade by the volume's transmittance and
  gain its in-scattered light. It replaces haze when the physical sky is on and `HazeStart`/
  `HazeEnd` are zero; set haze still applies. Height fog and volumetric fog stay on top.
  `Graphics.AerialPerspective` turns it off.
- Hamlet and Archer move to the physical sky. Hamlet's `SkyColors` table goes.
- Tests check the LUTs against hand-worked values at a few points (transmittance straight up,
  at the horizon) and the sky's brightness at noon against the old Preetham value.

Settled after the builder check (calls marked as such):

- Which sun (call): the sky follows the same light as today, the shadow-casting one or else
  the first. Its brightness comes from a fixed solar strength times `Brightness`, not from the
  light's colour, so a game that dims its sun light at dusk doesn't darken the sky twice.
  The light may sit below the horizon; the sky then shows twilight and night. Hamlet keeps its
  sun as that light all day and gets its moon from step 3, rather than swapping the sun for
  the moon.
- Night brightness (call): physics to about 8 stops below noon, then an artistic floor, a faint
  blue night glow that stays above half-float precision. Stars and the moon sit above it.
  Hamlet's exposure curve is retuned to it.
- Sunlight tint (the user's choice): the sky's sun light is multiplied by the atmosphere's
  transmittance towards it, normalised so the sun 60° up is unchanged. Low suns warm and dim
  by themselves and a set sun lights nothing. No opt-out setting; a game that wants exact
  colours uses the gradient sky. Its own `Graphics` switch is not needed, since
  `AerialPerspective` is about the fade, not the sun; the tint goes with the physical sky.
- Below the horizon (call): `Ground` still colours it, as now, faded in from the sky at the
  horizon; no planet surface is drawn.
- Aerial perspective strength (call): real air, with the aerosol density from `Turbidity`. Small
  scenes barely fade, which is right; Forest and Hamlet (hundreds of metres) show it. No new
  setting; haze still works as before for a stronger, stylised fade.
- HDRI brightness and its sun (call): `Exposure` 0 is the image as stored. The cooker finds the
  brightest small region, takes it out of the lighting cubes (it stays in the background and
  mip 0), and stores its direction, which the sky asset exposes as `SunDirection` so a game can
  aim its sun light there. The docs say so.

### 3. Night sky

- A moon disc drawn where the moon light is (the shadow-casting light when the sun is down,
  or an explicit `SkySettings.Moon` direction; builder's call, documented).
- Stars: procedural, fixed to the sky (optionally turning with time), fading in as the sky
  darkens, only in the background and mip 0, not in lighting.
- `Graphics` switch for stars if they cost anything measurable.
- Hamlet shows both.

### 4. HDRI sky

- `content.Sky("skies/meadow.hdr")` returns a sky asset; `SkySettings.Image`, `Rotation`
  (radians about up) and `Exposure` (stops) use it. The cooker turns the equirectangular
  `.hdr` into a half-float cube (BC6H later), and the runtime fills the environment through
  step 1's prefilter and SH.
- The HDRI shows as the background and lights and reflects. The sun stays a separate light the
  game sets; the docs say to aim it at the sun in the image. The disc isn't drawn (the image
  has its own).
- One example (Gallery, or the Courtyard Lineup) gets a CC0 HDRI from Poly Haven, small
  (2k), with its licence noted.
- Tests: a uniform-colour HDRI gives the same irradiance as a gradient of that colour; a
  rotated HDRI rotates the irradiance.

## Each PR

- Fast tests while working; `--filter "Needs!=GPU"` and the GPU suite before committing.
- `dotnet roe` clean.
- Before/after shots per CLAUDE.md (same seed, ticks, size, `--validate`), saved as `.webp`
  in `docs/media/`, used in the docs. Step 1 has no visual change apart from Hamlet, so it
  shows a recorded Hamlet day instead.
- A frame-time check: GPU time of the sky update per frame, and none when nothing changes.

## Left to the builder

- Sample counts, LUT sizes, the probe "jump" threshold, whether irradiance stays a cube or
  becomes SH read in shaders, the moon's API, and the HDRI cooker's cube size.
