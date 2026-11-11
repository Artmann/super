# Courtyard realism: where we stopped

This note picks up the plan in `2026-11-08-courtyard-realism.md` after a pause on 2026-11-10.
The full step-by-step log of every task, review and ruling is in
`2026-11-08-courtyard-realism-progress.md` next to this file.

## Where the branch is

The work is on `feat/material-graphs` (PR #73), rebased on main at f23b9b4 plus the raw mouse
fix. The last finished and reviewed commit is 6d919f1 (Task 15, wind). After that comes one
work-in-progress commit for Task 19, followed by the commit that adds this note.

Tasks 1 to 18, 20, 26, 14 and 15 are done. Each passed an independent review and a fix round.
At 6d919f1 the test counts were Super.Tests 2020, Super.Cook.Tests 334 and Courtyard.Tests 28, all passing.
Shots from the default camera (`--seed 1 --ticks 60 --size 1600x900 --validate`) had 0 validation messages.

Effect switches use bits 0 to 17 of `RenderEffects`. Puddles is 16 and Wind is 17, so the next
free bit is 18.

## Task 19 is half done

Task 19 (ground) was stopped partway. The work-in-progress commit holds a rewritten
`mud.material.cs` (coarse clods, three pebble sizes, straw, leaves) and a new
`gravel.material.cs`. It has not been cooked, tested or reviewed, and the gravel decals in
`art/courtyard.py` and `content/decals.json` are not written yet. The agent's last note was to
tone the leaves down a little and then look at the gravel. Pick it up from the brief in the plan.
Squash the work-in-progress commit into the finished Task 19 commit.

## What's left, in order

1. Task 19, Ground: finish the mud, add the gravel decals along both arms of the path and round
   the barrel, and make a before/after.
2. Task 21, Cloth, lime and glass.
3. Task 22, Leaves and blades.
4. Task 23, The model: cards, species, clutter, decals and UVs.
5. Task 27, Motion vectors. This fixes the slight TAA smear on swaying grass, because the engine has no
   velocity buffer yet.
6. Task 28, FSR upscaling.
7. Task 24, The light, the air and the camera. It runs after 27 and 28, as the plan's amendment says.
8. Task 25, Media, docs and roadmap.
9. A final review of the whole branch, then getting PR #73 ready to merge.

Each task follows the same loop: an implementer, an independent review, fix rounds until it is
approved, then a labelled before/after image against the plan's starting shot.

## Put off for the final review

- `SceneRenderer.UploadMesh` replaces the `Transparent` flag, so a generated mesh with opacity
  below 1 never draws as glass. This bug was there before this work.
- The clutter plank is saturated orange, the barrel staves alternate silver and orange, and the
  silvered deck boards are slightly blue. Task 24 should fix these.
- `crypt.webp` and `graphics-fastest.webp` predate the Crypt's fog and need retaking in Task 25.
- Squash the volumetric-fog work-in-progress commit before merging.
- Sun shafts stop short because sun shadows only reach 7 m.
- The puddle ripples show a faint value-noise grid up close.
- The cook cache key ignores `Only`, which was already the case before this work.
- design.md line 1030 says volumes "stay in the general layout for good", which is no longer true now that they are freed.
- The plan text still mentions a wall-lamp halo, which was waived because in daylight there should be none.
- WindTests has `=>File` with the spaces missing.

## Working notes

- Build single projects, not the solution. Build examples with
  `-p:BuildProjectReferences=false` and run them with `--no-build`.
- A Courtyard cook takes 10 to 16 minutes.
- Use headless Blender 4.3 with `--background` for `art/courtyard.py`.
- Every new effect gets an on/off switch that is on by default, and the default should look best.
