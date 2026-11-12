# Lighting roadmap

The lighting design (`docs/superpowers/specs/2026-11-11-lighting-design.md`) is six
subsystems that each ship on their own. Each gets its own plan, written when its turn comes,
so it argues from the code as it stands then rather than from guesses about earlier steps.

| # | Plan | Spec order step | Depends on | Can run beside | Status |
| --- | --- | --- | --- | --- | --- |
| 1 | Ray tracing foundation | 1 | nothing | 2 | Planned: `2026-11-11-ray-tracing-foundation.md` |
| 2 | Lightmap UVs | 3 | nothing | 1, 3 | Planned: `2026-11-11-lightmap-uvs.md` |
| 3 | Live probes | 2 | 1 for the traced path only | 2, 4 | To plan after 1 lands |
| 4 | Bake API without a bake | 4 | nothing | 2, 3 | To plan |
| 5 | Baked spaces: baker, runtime, Gallery | 5, 6, 10 (Gallery) | 1, 2, 4 | 3 | To plan after 1, 2 and 4 land |
| 6 | Baked chunks and Hamlet | 7, 10 (Hamlet) | 5 | | Planned: `2026-11-12-baked-chunks.md` |
| 7 | Bakes without a build step | 8, 9 | 6 | | To plan after 6 |
| 8 | Crypt and Courtyard shots | 10 (rest) | 5 for the Crypt, 6 for nothing | 7 | Folded into 5 and 7 |

Plans 1 and 2 touch different files (Vulkan device and a new tracing layer, versus the
model cooker and a new unwrapper) and can run in parallel worktrees now. Plan 4 is mostly
Super's API and hashing and could join them; it's planned once 1 and 2 are under way so
it can use the `Uv2` work's names.

## What each later plan must carry over

These are decisions already made in the spec that cross plan boundaries, listed so no
plan forgets them:

- `DrawItem.BakeId` is a `ulong`, zero means not static. Super computes it; inside a chunk
  the transform is relative to the chunk. (Plan 4.)
- `LightMode` defaults to `Mixed`, and without a fresh bake every light is `Realtime`, so
  plans 3 and 4 change no existing scene's look except where the live layer improves it.
- The traced hit reads the material table and bindless textures from `ResourceTable`, and
  skips surface shaders, world variation, wear, dirt and puddles. Plan 1 builds that hit
  path; plans 3 and 5 reuse it, not copies of it.
- `LightmapUnwrap` lives in Rendition so the cooker and meshes made by code share it.
  Plan 2 builds it; plan 5's layout scales and offsets the `Uv2` it produces.
- Every new effect gets a `Graphics` switch with the best-looking default:
  `Graphics.ProbeQuality` (plan 3), `Graphics.BakedLighting` (plan 5),
  `Graphics.BakeOnArrival` and `Graphics.BakeBudget` (plan 7).
- GPU tests live in `tests/Rendition.Gpu.Tests`, which plan 1 creates. The bake tests in the
  spec's "How it's tested" go there in plans 5 and 6.
- Before and after shots with `--screenshot --validate` at one `--seed`, `--ticks` and
  `--size` per comparison, as `docs/media/*.webp`. Plan 3 shoots every example; plans 5 and
  6 shoot Gallery and Hamlet; plan 7 adds the Crypt and the Courtyard.

## Review points

One review at the end of each plan's branch, not per task. Things worth a reviewer's eye
across the whole effort:

- Light counted twice where a lightmap and the probes meet (plans 3 and 5).
- Anything that hitches: BLAS builds on first use (plan 1), probe updates (plan 3), bakes
  in the background (plan 7).
- Validation messages on a GPU without ray tracing, run with `--no-trace` (plan 1 adds it).
