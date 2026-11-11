# SDD ledger — plan: docs/superpowers/plans/2026-11-08-courtyard-realism.md
Spec: docs/superpowers/specs/2026-11-08-courtyard-realism-design.md (read).
Branch: feat/material-graphs, start commit 5f5b0b5.

## Pre-flight scan
| Pair / task | Produces vs consumes | Found |
| T1/T3 | T1 AO target RGBA8 bent normal; T3 lighting samples probes with bent normal | consistent |
| T2/T24 | View.Shadows(radius, cascades, sunSize, contact) | consistent |
| T3/T4 | cube capture + invalidation hash shared | T4 consumes T3 hash, order ok |
| T3/T24 | View.LightProbes(min,max,spacing) | consistent |
| T6 vs spec | plan: ShadowSettings.LightAngle for the disc; spec: sunSize | same value (SceneRenderer maps SunSize->LightAngle) |
| T6/T7 | both touch SkySettings; fog colour reads sky cube | sequential, ok |
| T8/T9 | both touch tonemap.frag, PostSettings | sequential, ok |
| T10/T11 | DOF targets at scene size | T11 after T10, ok |
| T12/T22 | graph Opacity -> alphaCutoff | consistent |
| T13/T19/T23 | DecalBlend(Color, Normal, Roughness, AngleFade) | consistent; BUT T19 says the script writes gravel decals to decals.json while the game only reads decals.json in T23 |
| T14/T18 | puddles rule on graph_flagstone | consistent |
| T15/T22 | foliage.translucency, wind rules | consistent |
| T16/T13 | GpuMaterial grows 3 vec4 + scalar; decals read Surface factors | independent |
| T16/T23 | both do Smart UV Project on barrel/plank/deck/plinth | overlap, harmless |
| T5 | no CPU test, visual only | allowed; not a test asserting nothing |
| T1 test | `new View()` may need ctor args | implementer adapts |
| each task | own tests vs own code | agree |

Ruling: T19's gravel decal placements are deferred to T23's catalog (T23 already lists "the gravel strips from Task 19"); T19 delivers the gravel graph, the mud rework and the sidecar only — the game cannot read decals.json before T23 — costs nothing if wrong, the strips land one phase later.
Ruling: T16 step 4 does the Smart UV Project the bake needs; T23 only checks it — avoids doing it twice — no cost.

## Progress
Task 1: dispatch 1 interrupted by the user mid-work (uncommitted plumbing in tree, no shaders, no report); re-dispatched to continue from the working tree.
Task 1: Ruling: irradiance blends surface normal -> bent normal by (1 - ao) instead of replacing it outright — keeps normal-map detail in open areas; the AO target still holds the pure bent normal for T3 — if wrong, creases get slightly less bent-normal lighting.
Task 1: minor (deferred): ao_blur.frag averages raw octahedral values, wrong at the z=0 fold (floor normals at head height); decode-sum-reencode fix. LOAD-BEARING for T3 (probes sample by bent normal): carry into T3 dispatch.
Task 1: minor (deferred): View.AmbientOcclusion accepts +Infinity radius (use float.IsFinite).
Task 1: minor (deferred): OcclusionTests refusal message check satisfied by the ParamName suffix; assert ParamName + a phrase.
Task 1: minor (deferred): no test that SceneRenderer copies view.Occlusion into FrameView.
Task 1: minor (deferred): ao.frag viewPosition does two matrix multiplies per tap.
Task 1: complete (commits 90ae40f..a428799, review clean; t01-gtao.png 0 validation messages)
Ruling (user direction 2026-11-08): every new effect gets a Graphics on/off switch and the best-looking default. Plan Global Constraints and Tasks 2, 3, 7, 9, 13, 14, 15 amended (ContactShadows, LightProbes, Fog, Decals, Puddles, Wind switches; Tonemapper default Agx). Kept opt-in because they change the meaning of existing scene values: AutoExposure (Exposure becomes compensation), Turbidity (replaces the three sky colours), DOF (needs a focus distance), fog density, render scale 1. Cost if wrong: examples look different by default; a one-line setting restores any of them.
Task 2: implementer reported DONE_WITH_CONCERNS (a9888e2): ivy darkened by self-occluding contact shadows. Ruling: foliage pixels receive no contact shadows; twice-contact-length blocker limit accepted. Resumed implementer for the fix before review.
Task 2: Ruling: the Courtyard ivy is not flagged foliage (twoSided only), so the foliage skip alone cannot fix it. Keep the foliage skip and tighten the blocker depth limit to a fixed 0.15 m (contact shadows cover the last centimetres; PCSS already handles leaf-on-leaf). Ivy becomes real foliage in Task 15. Cost if wrong: contact shadows a little weaker under thick objects.
Task 2: minor (deferred): lighting.frag contactShadow doc comment still describes the old 2*reach limit.
Task 2: minor (deferred): GraphicsTests Changing_shadow_resolution_while_running_is_applied_next_frame only checks the pass-through; rename or exercise the runtime rebuild with validation.
Task 2: minor (deferred): 1024/2048/4096 rule and message duplicated between Graphics.cs and ShadowPass.CheckResolution; constructor re-validates.
Task 2: minor (deferred): default(ShadowSettings) gives ContactLength 0; note on the field doc.
Task 2: minor (deferred): contact march runs where PCSS visibility is already 0.
Task 2: minor (deferred): contact noise changes per frame with AntiAliasing off (shimmer).
Task 2: minor (deferred): rendering.md says a longer contact length "costs no more"; 12 fixed steps lose thin blockers past ~1.5 m.
Task 2: minor (deferred): ivy ~6% darker until Task 15 flags it foliage.
Task 2: complete (commits 5c5b33d..481a388, review clean; t02-contact.png 0 validation messages)

User request (2026-11-08 16:03): after every task, take shots/tNN-<name>.png (Courtyard, --seed 1 --ticks 60 --size 1600x900) and rebuild shots/progress-tNN.png = plan-base.png | latest, labelled, via ffmpeg hstack+drawtext (also a crop of the area the task changed). Show it to the user when reporting progress. Delivered progress-t02.png + progress-t02-crop.png.

Ruling: two lanes from 2026-11-08 16:10 (user approved "ok") — renderer lane serial in C:\Users\Artga\Code\rendition on feat/material-graphs (T3..T11, T13, T14, T15, then T19, T22, T23, T24, T25); content lane in worktree C:\Users\Artga\Code\rendition-content on feat/courtyard-content (branched at 481a388): T12 → T16 → T17, T18, T20, T21 (two or three at once in further worktrees if useful). Why: those tasks share no files with the renderer lane. Cost if wrong: merge conflicts in ImportSettings.cs/docs and a rebuild per merge. Each lane rebases onto the other's commits before its review; Courtyard shot after every merge.
Task 12 dispatched at BASE 481a388 in rendition-content.
Task 3: implementer DONE_WITH_CONCERNS, commit 5c13710 (session restart mid-task; resumed and committed). Concerns: shade ~4% darker, corners dark (judge by eye); probes static vs spinning Sample cube; capture frames hitch (6×64 waits); round-1 overwrites in-use tiles on recapture. Review dispatched on review-481a388..5c13710.diff. progress-t03.png / progress-t03-crop.png built.
Task 3 review round 1: Needs fixes. Important: (1) capture sky samples reflection LOD0 with panels, irradiance bake has none → panel light counted twice (and sun disc after T4); (2) Sample spinning cube shows streaks by default. Ruling: Sample volume goes behind `--probes` flag (default off) — the sample's job is to be a clean rig and the Review Focus 1 check stays reproducible — cost if wrong: default sample exercises no probe code. Also sent: docs claim "old probes stay in use" is untrue; LightingHash misses Materials/Cascades/LightAngle. Fix round 1 sent to implementer (resume).
Task 3 deferred minors: ProbeGrid.Weight uses one point for all terms so tests don't pin bias/invalid skip/1e-3 fallback; "never quite" comment vs wrap²=0; lookup snaps to sky ~1 spacing outside the box (fade by total weight would hide a seam); probe_fold shared memory 24,580 B > guaranteed 16,384 (gate Supported); depth 2×2 pre-average underestimates variance; rendering.md should say a room smaller than spacing falls back to sky; capture frame = 1 DeviceWaitIdle + 384 fence waits, animated sun recaptures every frame (documented).
Task 12: implementer DONE_WITH_CONCERNS, commit a2159e6 on feat/courtyard-content (rendition-content). Concerns: cutoff test uses half-0.25/half-1 opacity not Gray.Of(0.25) everywhere; names resolved in SceneRenderer.PrepareModel/UploadModel not Content.cs; load path untested; duplicate pipelines when a shader is named by both a rule and content.Shader; uncooked shader texture fails at load not cook. Review dispatched on review-481a388..a2159e6.diff.
Task 12 review round 1: Needs fixes. Important: PrepareModel load path untested and never run. Fix round 1 sent (PrepareModel test; refuse empty/path shader names; rename A_ninth→A_fifth_parameter_component_is_refused; roadmap lines for texture-existence-only check and duplicate pipelines). Accepted deviations: half-and-half opacity fixture (constant 0.25 under 0.5 cutoff is refused by CutoutProblem); names resolved in SceneRenderer.PrepareModel/UploadModel not Content.cs.
Task 12 deferred minors: ImportShader runs for unused materials (inconsistent with cutout checks); ToBytes checks shader count but not per-entry sizes, no round-trip test with null entries; RuleFor runs twice per material.
Task 3 fix round 1: commit 1b1e086 (EnvironmentMap.Sky 64² panel-free cube for capture sky pixels; Sample volume behind --probes; docs wording; hash adds Cascades/LightAngle/Materials). Courtyard t03-probes unchanged (mean 98.59→98.60). Implementer concern: full-solution build hit the 30-min limit after all cooks came from cache. Scoped re-review dispatched on review-5c13710..1b1e086.diff. NOTE for T4: EnvironmentMap.Sky must stay without the sun disc.
Task 3 re-review: all findings addressed, no new breakage. Task 3: complete (commits 5c13710..1b1e086, review clean after 1 fix round; t03-probes.png 0 validation messages; Crypt unchanged).
Task 12 fix round 1: commit 85b4d4a (finished by controller; agent was stopped). Scoped re-review dispatched on review-a2159e6..85b4d4a.diff.
Task 12: complete (commits a2159e6..85b4d4a, re-review clean after 1 fix round; no visible change, so no progress shot; minor: '..' check also refuses names like a..b).
Task 16 dispatched at BASE 85b4d4a in rendition-content.
Task 16: implementer NEEDS_CONTEXT. Graph UVs are metric and tiling, and courtyard.py's box UVs overlap, so masks can't bake over UV0.
RULING (Task 16): Option A. Add a second UV set: Vertex.Uv2, glTF TEXCOORD_1, used only by the mask. Meshes without it get (0,0).
- A bakeMasks material without TEXCOORD_1 fails the cook. The error names the material and says it needs a second UV set.
- The overlap check runs on UV2.
- courtyard.py adds a "mask" UV layer by Smart UV Project (angle 66°, margin 0.002) on the bakeMasks meshes. UV0 stays metric. This takes Task 23's UV bullet, so Task 23 drops it.
- Re-export with headless Blender: "C:/Program Files/Blender Foundation/Blender 4.3/blender.exe" --background. Don't drive the user's live Blender over MCP.
- GpuMaterial 272→336 is accepted if hand-worked. MeshData is defined in this task.
- Vertex and gbuffer.vert also change in the renderer lane (Task 15); conflicts are resolved at rebase.
Task 4: implementer DONE_WITH_CONCERNS, commit c3a4a5f. Concerns: window barely changes (glass faces the open yard); camera/sun movement recaptures every frame; up to 3 captures during probe settling; removing a probe recaptures all; 5 new public Rendition members; full build timed out on cook, compile-only passed. Review dispatched on review-1b1e086..c3a4a5f.diff.
Task 4 review round 1: Needs fixes.
Important:
(1) Camera movement recaptures every frame, because LightingHash includes Shadows.Center/Radius, which follow the camera. This also restarts Task 3's light probes. Fix both: frame capture shadows on the probe or volume box, and leave camera framing out of the hash.
(2) CaptureProbeFaces resets _heldReady/_ldrReady during play, which breaks transitions. Save and restore them like CaptureLightProbes.
Also sent: rename CaptureLighting→ReflectionLightingHash and make its doc true (decide whether AO, contact-shadow and light-shadow switches and point lights belong in it); record the schedule only after a capture succeeds; docs say the probe can capture more than once while light probes settle.
RULING (Task 4): the brief's "window reflects the dry-stone wall" is dropped. The glass faces the camera and reflects the open yard and sky through the box's open sides. Wiring is proven by the red-tint run. Puddles (Task 14) will show probe reflections of geometry.
Task 4 deferred minors: _lightProbesInUse is one frame stale; removing one key recaptures all; SceneRenderer capture glue untested (no fake renderer); 5 new public Rendition members, justified.
Task 4 fix round 1: commit 0e63dad. Scoped re-review dispatched on review-c3a4a5f..0e63dad.diff.
Task 4: complete (commits c3a4a5f..0e63dad, re-review clean after 1 fix round; 0 validation messages; Crypt unchanged).
Deferred minors: first WriteTargetDescriptors in CaptureProbeFaces sits outside the try; CaptureLightProbes has no try/finally (pre-existing); long doc lines; slot drift if CaptureReflectionProbe throws after adding.
Task 5 dispatched at BASE 0e63dad in main checkout. Controller added: a Graphics.MultiscatterSpecular switch (default true), per the user's switch rule.
Task 16: implementer DONE_WITH_CONCERNS, commit f17b81c. Concerns: curvature by ray probing not normal divergence; layer colours read as sRGB; hoop values differ from brief; height dirt per-material; no Graphics switch; Courtyard.Tests 29 min in Debug; glb export not byte-stable; GpuMaterial 320 not 336; no layers on transparents. Review dispatched.
Task 16 controller visual check (shots/t16-barrel-zoom.png, progress-t16-crop.png): REGRESSION.
- Barrel: wear draws thin bright white lines on each stave edge (chalk lines, not a broad pale wear band).
- Sill and plinth blocks: bright white rims, plus diagonal white lines across block faces, i.e. curvature at internal triangle edges or UV2 island borders. Suspect bleed or dilation from the 4 px edge, or edges detected inside flat faces.
- Deck boards and plank: tops washed to flat grey.
These go into the fix round with the review's findings.
Task 16 review round 1: Needs fixes.
Important:
(1) Add the Graphics.WearAndDirt switch: RenderEffects 1<<7, OFF_WEAR_AND_DIRT, off in Fastest, row in the docs table, test.
(2) Background blue 0 reads as full dirt. Set it to 255, plus a test.
(3) Shot fails: chalk-line wear, grey deck and plank, no dark hollows.
(4) Courtyard.Tests takes 29 min. Limit occluders to bounds + 0.5 m, mark the lineup test Slow, time it.
RULINGS (Task 16 fix):
- Blue = height above the mesh's own base divided by min(0.3 m, mesh height), clamped. The shader height term is 1 − blue.
- Layers tint rather than replace: target albedo = clamp(albedo × layer.color × 2, 0, 1), mixed by the mask, so grain survives.
- Wear gets a wider curvature band (about 1–2 cm on the barrel) and a low-frequency noise breakup.
- Fix the diagonal lines on plinth faces.
- Mask textures are clamped at the edges, not wrapped.
Minors also sent: transparent bakeMasks warning or doc; mask_uvs IndexError; a doc note on sRGB layer colours vs the linear rule colour; mention the glb churn in the commit body.
Task 5: implementer DONE_WITH_CONCERNS, commit 0c05132. Concerns: fit's Ess = 1 − 0.55·roughness is view-independent, so no extra grazing brightening; about 2.1x specular at full roughness on bright metal; Fastest() turns it off; DungeonCrawler cook --watch locks the cook tool; stray diff file 'ummary>' deleted by controller. Review dispatched.
Task 5: complete (commit 0c05132, review approved first round; 0 validation messages; Crypt: only metals brighten).
Deferred minors: Fastest() turns MultiscatterSpecular off for no speed; rendering.md "darker by up to half" should say "less than half as bright"; multiscatterCompensation recomputes environmentBrdf; Ess is view-independent (a DFG LUT would fix it later).
Task 6 dispatched at BASE 0c05132 in main checkout. Turbidity 0 = off switch (scene value).
Task 16 fix round 1: commit bcad831 (WearAndDirt at bit 8, clashes with LightProbes; dirt occlusion term now smoothstep(0.25,0.75,1-sky), an unrequested change; cook 26.8s→17.3s). Sent: rebase content lane onto feat/material-graphs (Tasks 3-5), move WearAndDirt to 1<<10, add to probe hash. Re-review after rebase.
Task 6: implementer DONE_WITH_CONCERNS, commit 62dbef3. Concerns: disc radius = LightAngle/2 (brief said LightAngle); sun move rebakes the env and stalls; probes exclude the disc; no night; brightness scale 0.08 chosen; stopped a non-watch full cook. Review dispatched.
Task 6: review Needs fixes. Important: haze fade/particles sample mip0 with disc. Rulings: fix haze/particle disc, sun-straight-down NaN, Rendition turbidity guard; keep radius=LightAngle/2, note PCSS 2x mismatch in design.md limitations (PCSS fix deferred). Deferred: double sun specular on SSR miss, tests on C# copy only, weak turbidity-0 test, per-pixel skyState, rebake stall. Fix sent to implementer ae10e3cedc906ab5f.
Task 16: rebased onto feat/material-graphs; new SHAs ae59ad0 80c2849 (T12), 5bdc43f (feat), 5f38123 (fix, WearAndDirt bit 10, in LightingHash). Tests 1692/286/1. Controller visual check of t16-wear vs off: chalk lines and diagonals gone, subtle edge wear + base dirt. Sonnet re-review dispatched (diff task-16-fix.diff).
Task 16: complete (feat 5bdc43f, fix 5f38123 on feat/courtyard-content; sonnet re-review clean; 0 validation). Deferred: glb export churn; Courtyard.Tests still ~9 min (Slow); bake caching; transparents ignore layers (documented).
Task 17 dispatched in rendition-content at BASE 5f38123.
Task 6 fix round 1: commit bf070e5 (disc-free haze/particle helper, NaN fallback, turbidity clamp 1.7-10, PCSS 2x note). Evidence t06-haze-broken/fixed. Sonnet re-review dispatched.
Task 6: complete (62dbef3 + fix bf070e5; sonnet re-review clean; 0 validation). Deferred: double sun specular on SSR miss; tests check C# copy only; weak turbidity-0 test; per-pixel skyState; rebake stall; PCSS uses lightAngle as radius (2x vs disc); zero-length light direction NaN in SkyModel.Sun.
Task 7 dispatched at BASE bf070e5 in main checkout. Rulings: Graphics.Fog bit 1<<11 (10 reserved for WearAndDirt); fog colour disc-free sky; probes get no fog, not in hashes.
Task 17: implementer DONE_WITH_CONCERNS, commit 317136e. Concerns: buff/ochre patchwork; HistogramScan 0.78 = 78% coverage, broken up with finer Perlin; soot faint, repeats every 1.17 m; ordinary joints shallower; 4096 cook 10-14 min. Controller visual: too much of a checkerboard; buff bricks pop like a 2000s texture. Review dispatched with that note.
Task 17 review: Needs fixes (checkerboard from value range + flat weights; efflorescence tied to pale tones, ~100% coverage). RULINGS: bell-shaped counts [5,14,16,16,20,13,8,4]; pale stops darkened (#96683f/#92704f/#94765a), plum #52302a, reds desaturated; hardness 0.8 rand+0.2(1-tone); efflorescence calibrated 18-26% coverage test, joint bleed 4 cm, opacity 0.5→0; keep brick-face roughness change (note in commit); mortar fill 0.15; soot as is. Fix sent.
Task 7: implementer DONE_WITH_CONCERNS, commit e522b96. Concerns: effect mostly shade lift at 0.004; sun glow untested on screen; no OFF_FOG (density 0 on CPU); sky background not fogged; underwater check duplicates water condition; extra F key toggle + clear input script. Review dispatched.
Task 7 review: Needs fixes (Important: NaN for high camera in transmittance integral → difference form + test). Minors sent: Fog.cs:378 comment; design.md limitations (SSR double fog, sky not fogged/sun lobe). Kept F key + clear script. Fix sent.
Task 7 fix round 1: cf8bea7 (lower-end form, finite both above and below; 2 tests; comment; design.md limits). Sonnet re-review dispatched.
Task 7: complete (e522b96 + fix cf8bea7; sonnet re-review clean; 0 validation). Deferred: L=0 with overflow → NaN edge (not real); design.md long line; SSR double fog and sky not fogged (documented).
Task 8 dispatched at BASE cf8bea7. Rulings: Graphics.AutoExposure bit 1<<12 default true AND PostSettings.AutoExposure (scene, default false) both required; histogram clamps per pixel; GPU-side adapted value, byte-identical shots; Crypt pixel-identical.
Task 17 fix round 1: 0e870e4. Shots went to examples/Courtyard/shots/ (relative path) — copied as shots/t17-bricks-v2.png. Controller visual: reads as weathered red brick; a few ochre bricks pop slightly, rims faint (note for Task 24 polish). Sonnet re-review dispatched.
Task 17: complete (317136e + fix 0e870e4; sonnet re-review clean; 0 validation). Deferred: ochre bricks still pop slightly in sun (Task 24); salt rims faint; coverage test window narrow vs seed; shots land in examples/Courtyard/shots when run from there.
Task 18 dispatched in rendition-content at BASE 0e870e4. Rulings: skip puddles sidecar if unsupported (Task 14); sandstone ~1 in 8; subtle tone steps; absolute screenshot path.
Task 8 implementer done after rate-limit pause: c44935a (DONE_WITH_CONCERNS: Courtyard ~0.6 stop darker, luma 108.7→86.2; 1761 tests; 0 validation; Crypt identical). Opus review dispatched; asked to rule on compensation. Task 18 implementer resumed after rate limit.
Task 8 review (opus): Needs fixes — Important: Courtyard compensation -0.3 → +0.3 (look regression); Minor: roadmap gaps, Exposure.Speed negative guard, 0-speed mismatch, tests mirror shader. Fix round 1 sent to implementer.
Task 8: complete (c44935a + fix 3d791ae; sonnet re-review approved; 0 validation; luma 108.4). Deferred: tests mirror shader maths; NaN view.Time → NaN MaxStep; long line rendering.md:66; shaded left wall darker than baseline (Task 24).
Task 9 dispatched at BASE 3d791ae. Rulings: Tonemapper default Agx; keep Courtyard luma ≥ ~108 (retune compensation if needed); no new Graphics bit; Temperature/Tint validated in View.Finish; Crypt drift tonal only; docs/media/agx.webp.
Task 9 implementer: af156a5 (DONE_WITH_CONCERNS: enum order Agx,Aces; Courtyard contrast 0.3 sat 0.2; luma 111.0; Crypt tonal; 1789 tests). Controller visual: AgX holds highlight hue, a bit pastel/washed vs ACES. Opus review dispatched.
Task 9 review (opus): Approved; optional — Courtyard AgX grade pastel (tune or add Punchy look), alt text untrue, WB orientation test, D65 note. Controller: applying all as fix round 1 (default must look best).
Task 18 implementer: 198fe20 (DONE_WITH_CONCERNS: cracks are warped lines; slab sides streaked; lineup webp stale; puddles sidecar skipped; 0 validation). Controller visual: big win — real separate slabs with sandstone, cracks, dirty joints. Opus review dispatched.
Task 18 review (opus): Needs fixes — Important: slab side walls stretched dirt (box_uv on hard faces, oz≠0); Minor: crack tests don't prove Build uses cracks; Tones mutable. Lineup webp retake moved to end of plan (Task 25). Fix round 1 sent.
Task 9 fix round 1: 66de077 — added PostSettings.AgxLook {Punchy, Base}, Punchy default; Courtyard Exposure 1.05 Contrast 0.25 Sat 0.15, luma 107.2; WB 12000K test; alt text fixed. Concern: defaults ~0.8 stop darker mid-tones (Crypt unshot under Punchy; AgX-base Crypt was 54.7 vs ACES 47.6). Controller visual: bricks deep red, good. Sonnet re-review dispatched.
Task 9: complete (af156a5 + fix 66de077; sonnet re-review approved). Deferred: design.md:1370 'It' antecedent; sky still pale; window reflection banding; Crypt under Punchy unshot (Task 10 implementer to shoot it as its before shot).
Task 10 dispatched at BASE 66de077. Rulings: RenderScale default 1, refuse outside 0.5–2; screenshot stays window size; no LightingHash change; Crypt before-shot doubles as Punchy check; Crypt at scale 1 pixel-identical.
Task 9 addendum: Crypt under Punchy shot (t09-crypt-punchy, 0 validation, luma 42.7 vs ACES 47.6). Controller visual: warmer/redder, slightly darker, still readable — acceptable; possible +0.3 exposure in Task 24/25 polish.
Plan amended (user: 'Add things to the list'): Tasks 26 volumetric fog, 27 motion vectors, 28 FSR upscaling (run before Task 24); Task 24 finish values updated to Tasks 8/9 tuning; Task 25 gets lineup retake, DDGI/surface-cache and DLSS roadmap lines. Plan edit uncommitted until Task 10 commits (avoid polluting its review range).
Task 10 implementer: 1e20511 (DONE_WITH_CONCERNS: scale 1 keeps odd sizes; no max-image clamp; 4x4 downsample at 2; LOD by scene pixels; 1819 tests; Crypt identical; Crypt Punchy luma 42.7). Plan amendment committed 2da8de3. Opus review dispatched.
Task 10 review (opus): Approved; minors (test name, design.md 4x4 wording, LOD note, bloom/SSS scale note, max image clamp) sent as fix round 1.
Task 10 fix round 1: 6b83ffe; sonnet re-review Approved (all 5 fixed). Task 10 COMPLETE (1e20511..6b83ffe). Deferred: no test that renderer passes the device limit (needs GPU); clamp at scale 1 over limit untested. Task 11 dispatched at 6b83ffe (brief's 0.179 mm corrected to 0.0897 mm).
Task 18 fix round 1: d38d112 (sides box_uv, crack material test, Build() makes cracks first). Sonnet re-review dispatched.
Task 18 re-review (sonnet): Approved. Task 18 COMPLETE (198fe20..d38d112). Deferred: material-lineup.webp stale (Task 25 retakes); model re-cook ~10 min.
Task 11 implemented: 0f0d604. Concerns: f/8@35mm blur ~0.7px (subtle); grass sway not deterministic across runs (wind ignores tick clock -> Task 15); FocalLength 0 = 35.
Task 11 review (opus): Needs fixes - docs 16px radius vs across; webp shows grass sway not DOF. Minors: IsValid vs try/catch, lazy targets, weak test name, glass/particle + halo limits. Fix round 1 sent (incl. wind determinism if small).
Task 11 fix round 1: 239b6bf. Correction: no wind exists yet (grass static); earlier 'sway' was misread. webp v2 checked: visible near blur, window crisp, grass matches. Sonnet re-review dispatched.
Task 11 re-review (sonnet): Approved. Task 11 COMPLETE (0f0d604..239b6bf). Deferred: far-layer 1px fringe (documented).
Task 20 implemented: 0c32d9c. Concerns: barrel staves near-white in sun (edge wear), deck at 0.8 reads flat grey, export drift on 4 meshes.
Task 20 review (opus): Needs fixes - barrel zebra (tone luminance 0.24 vs 0.13 + deal), deck flat grey + glossy edges (weathering 0.8, wear 0.45), false butt joints. Crate nails dropped: accepted. Fix round 1 sent.
Task 13 implemented: 5b85dc4. Concerns: overlapping normals replace; independentBlend required unchecked; subtle at default camera.
Task 13 review (opus): Needs fixes - decal bitangent flips normal green (Important). Minors: doc summary, profiler doc, NaN derivs at sky, material cache growth, independentBlend check, stain quads, alt text; plus make damp more visible. Fix round 1 sent.
- 17:03 Resumed after rate limit: T13 fix round 1 (a147158fa86bfa6a1), T20 fix round 1 (a5b5b48dda62e0750).
- 17:04 T13 fix 74ca296 (dome check OK, 1870 pass, damp 9.3 darker). Sonnet re-review dispatched. progress-t13.png made.
- 17:05 T13 re-review APPROVED (sonnet). Task 13 COMPLETE. Deferred: remove build_stains/keepMeshes (needs re-export).
- 17:06 Ruling: T14/T15 touch ImportSettings/ModelCooker/courtyard sidecars the content lane also changes; run them after the content lane merges (after T20). T26 (renderer-only) dispatched now at BASE 74ca296, bit 15.
- 17:29 T20 fix 83c8ad9 (barrel stripes gone; deck tone -0.1 instead of 0.15; real board lengths; 28/28). Sonnet re-review dispatched. progress-t20-crop.png made.
- 17:30 T20 re-review: all findings addressed except deck (murky at tone -0.1, edge highlights). Fix round 2 sent to implementer (deck retune ~0.05, short tail pieces).
- 17:39 PAUSED by user. Committed T26 WIP aa6824f, T20 round-2 WIP c668ccf (content), merged content lane 3271a19 into feat/material-graphs (conflicts: switch bits, decal/shader fields; union), pushed. 1906/1907 pass; PostGradeTests layout test fails due to T26 WIP FrameUniforms.Volumetric. Stopped orphaned T20 cook.
- 22:45 RESUMED by user ('finish all of the work'). Main checkout is on main; feat/material-graphs now checked out in rendition-content (has cooked assets). Work continues there; ledger stays here.
- 22:45 T26 finish dispatched (fresh opus, prior agent lost on restart) in rendition-content at 3271a19. Ruling: T26 review range = aa6824f^..aa6824f plus commits after 3271a19 — merge brings unrelated content — cost if wrong: reviewer misses merge-resolution bugs (final review covers).
- 23:04 User: rebase on latest main (4 cooker commits #74,#76,#77,393689d). Rebase agent dispatched in rendition-rebase (tmp/rebase-trial from 3271a19). Ruling: linearize the content-lane merges; after T26 lands, cherry-pick its new commits onto the rebased tip, move feat/material-graphs there and force-push with lease — user asked for rebase, which implies rewriting the PR branch — cost if wrong: PR history rewritten (old tip 3271a19 recorded here).
- 23:22 T26 implementer done: 4c65213 (DONE_WITH_CONCERNS: wall-lamp halo invisible in daylight; Crypt has no fog so no halos; shafts faint, sun shadows reach 7 m; 1907/1907; 0 validation; cmp identical).
- 23:28 T26 review (opus): spec ❌ (no wall-lamp halo; Crypt has no fog so no torch halos), quality Approved. Ruling: Crypt gets a faint default fog so torches gain halos — brief's premise assumed fog; default must look good — cost if wrong: Crypt look shifts slightly. Ruling: Courtyard wall-lamp halo in daylight waived as physically right — cost if wrong: brief item unmet. Ruling: off-path exactness accepted from code reading (reviewer traced identical arithmetic), no pixel proof vs 74ca296 because the merged content changed the scene — cost if wrong: subtle drift with switch off. Deferred to PR: squash WIP aa6824f. Fix round 1 sent (Crypt fog, Fade.Z test, weak test, free volumes/reword, slice wording, GPU timing).
- 23:38 Rebase done: tmp/rebase-trial 9fa4412 (89 commits on f23b9b4, merges linearized, +1 fix: tests via ModelCookers.StandAlone). Cook.Tests 315/315, Super.Tests 1915/1916 (known). Courtyard cooked, same-camera shot matches. Added TextureCompression memo. Opus rebase review dispatched.
- 23:39 T26 fix round 1: 459cd97, c903a71 (Crypt fog 0.008, torches volumetric 2; Fade.Z test; volumes freed when off; wording; timings 0.16 ms Courtyard / 0.30 ms Crypt; fixed unnamed Subsurface GPU timer mark from 2360175). 1908/1908.
- 23:41 T26 re-review (sonnet): all 7 ADDRESSED. Task 26: complete (aa6824f + 4c65213..c903a71, review clean). Deferred: design.md:1030 'stay in the general layout for good' vs freeing; long lines; crypt.webp & graphics-fastest.webp predate Crypt fog (Task 25 retake); shafts limited by 7 m shadow reach; faint sun rings in diff; SunScatter ignored in volume; plan text still says lamp halo.
- 23:43 Rebase review (opus): Approved; Important: memo untested/sharing tests lost compression-on path; shaderTextures vs Only. Minors: timing labels, cached faults, test name, docs. T26 commits cherry-picked onto rebase: b8fa50e 9c04b22 57cb3b8. Fix round 1 sent to rebase agent. T20 round 2 will start on the rebased branch after the switch.
- 23:50 Rebase fix round 1: bea2b20..c53e276 (Only refusal, memo tests + no cached faults, timings, docs, test rename); Cook 320/320, Super 1917/1917. Switched: feat/material-graphs now = c53e276 checked out in rendition-rebase (fresh cook there); rendition-content back on feat/courtyard-content. Old PR tip 3271a19 / pre-switch c903a71. Sonnet re-review dispatched; push after it.
- 23:50 T20 fix round 2 continued by fresh opus implementer in rendition-rebase (prior agent lost).
- 23:52 Rebase re-review (sonnet): all 6 ADDRESSED. Deferred: cook cache key ignores Only (pre-existing). Force-pushed c53e276 to origin/feat/material-graphs (lease 3271a19).
- 00:32 Session restart; T20 round-2 agent resumed (uncommitted README/courtyard.py/glb in rendition-rebase).
- 01:00 T20 fix round 2: a5e1ef6 + fb4282e (root cause: box() bevel dropped main faces' UVs/material; tone 0.05 weathering 0.45; tails ≥0.747 m; Courtyard 28/28, Super 1917/1917, 0 validation). Controller visual: deck reads as weathered timber; default view unchanged except clutter plank now saturated orange. Sonnet re-review dispatched.
- 01:01 T20 re-review round 2 (sonnet): all 3 ADDRESSED. Task 20: complete (0c32d9c..fb4282e, review clean). Deferred: clutter plank saturated orange (Task 24 polish / lower tone); silvered boards slightly blue; row gaps faint; README 0.75 vs 0.746 m.
- 01:01 T14 dispatched (opus) in rendition-rebase at BASE fb4282e. Rulings: Puddles bit 16; brief from rendition-rebase workspace.
- 01:58 T14 implemented 88121d0 (DONE_WITH_CONCERNS: default view reflects bank not wall/sky; ripples cut to 0.4 mm; Courtyard band 3.5 cm; water on hollow floor -> orange far-bank band; 1929/321/28). Controller visual: close-up good; default view darker/duller than old sheet. Opus review dispatched.
- 02:00 T14 review (opus): spec ❌ (default-camera puddle dark hole, no wall/sky), quality Needs fixes (tests check C# copy not shader). Ruling: ripple 0.4 mm (brief 3 mm) accepted — 3 mm shattered reflections — cost if wrong: calmer water than spec. Ruling: Courtyard band 3.5 cm accepted — 8 cm darkened all mud — cost: thinner damp edge. Ruling: shader-constant binding test acceptable instead of GPU readback if no GPU test path — cost: shader logic beyond constants untested. Fix round 1 sent.
- Task 14 fix round 1 done: 6421563 (water shaded at the water plane, shader-constant source tests, NaN/inf cases, docs). Super 1936/1936. Before/after looked at: no dark hole, reads as wet mud with reflections. Scoped sonnet re-review dispatched on 88121d0..6421563.
- Task 14 re-review (sonnet): Approved; item 2 "partly" (source-text checks brittle to reformat) accepted, no GPU test path. Task 14 COMPLETE at 6421563. progress-t14.png made and looked at.
- Task 15 (wind) dispatched to opus implementer in rendition-rebase at 6421563; bit 17 for Wind.
- Task 15 implemented: 3008d0f (Super 1996, Cook 334, Courtyard 28; 0 validation). Departures: per-material sway share, base from mesh lowest point (64-byte draw data), translucency in subsurface slot. wind-before-after.webp looked at: grass leans, bases fixed. Opus review dispatched on 6421563..3008d0f.
- Task 15 review (opus): Needs fixes. Important: culling bounds ignore wind sway; material→GPU wiring untested. Minors: transparent pass no sway, Rendition Material.Translucency default 0, "turned" docs wording, extra shader-text tests. TAA smear: no change (no velocity buffer, documented). Departures all ruled sound. Ruling: fix Important 1-2 and Minor 1-4 in one round; sent to implementer.
- Task 15 fix round 1 done: 6d919f1 (WindReach culling pad, wiring helpers+tests, transparent sway, Material default 0.6, docs, shader-text checks). Super 2020, Cook 334; shot pixel-identical, 0 validation. Implementer flags possible pre-existing UploadMesh Transparent-flag overwrite. Scoped sonnet re-review dispatched on 3008d0f..6d919f1.
- Task 15 re-review (sonnet): Approved. Task 15 COMPLETE at 6d919f1. Parked for final review: pre-existing bug, SceneRenderer.UploadMesh replaces the Transparent flag (Opacity<1 generated meshes never draw as glass); not caused by Task 15. Cosmetic: WindTests "=>File" spacing.
- progress-t15.png made and looked at; pushed 6d919f1. Task 19 (ground) dispatched to opus implementer at 6d919f1.
- Task 19 implementer hit the 5-hour rate limit; resumed via SendMessage.
- PAUSED by user 2026-11-10: T19 agent stopped; T19 WIP committed; handoff note + ledger copy committed to docs/superpowers/plans and pushed.
