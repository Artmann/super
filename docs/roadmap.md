# What's not here yet

Super is early. These are the known gaps, so you can plan around them.

- **Bone attachment.** Skeletal [animation](animation.md) works, but `view.Bone`, for
  hanging a model on a joint, doesn't exist yet. See
  [Drawing with views](views.md#planned). Until then, `animator.Joint` gives a joint's
  place for drawing a model there by hand. A sword that is a mesh in the character's own
  file follows its hand already, and a [mesh mask](views.md#showing-some-meshes) shows
  only the one you want.
- **More from particles.** [Particles](views.md#particles) are soft puffs for dust and
  smoke, and can glow, as flames. There are no textured or stretched sparks yet, and
  they cast no shadow. Nodes must work from the age alone, so particles can't bounce off the
  ground or react to what they hit.
- **Film effects.** [Cutscenes](cutscenes.md) have [transitions](transitions.md) between
  shots and [depth of field](rendering.md#focus), but there's no motion blur yet, and no transitions that move
  both pictures, such as a push or a slide.
- **More from audio.** [Audio](audio.md) plays stereo sound with no echo or reverb, walls
  that muffle sound, Doppler or surround. There is no ducking (music dipping under
  speech), no ambience that blends by where the camera stands, and sounds can't be tied
  to animation moments yet. A `Crossed` event can start a one-shot by hand.
- **Picking by shape.** Clicks are tested against balls, boxes, capsules and the ground,
  not a model's triangles.
- **Some camera moves.** A camera can't roll, and asymmetric views are a shift, so head
  tracking and tiled displays can't be done yet. Underwater views are wrong under an
  orthographic camera.
- **Local lights in reflections.** [Reflection probes](rendering.md#reflections) are
  photographed by the sun and sky alone, so a lantern or a lit window doesn't show in a
  reflection, and turning one on doesn't photograph the probe again.
- [Light probes](rendering.md#bounced-light) are traced with ray queries, as DDGI does, where
  the GPU can, but each update adds only one more bounce, so bounce between walls takes about
  12 seconds to mostly build up and a minute or two to finish, and a moved object reaches the bounce only when its probes
  come round again. On a GPU that can't trace they are rasterized a few a frame and take about
  a thousand frames to settle after a load or a teleport. A surface cache like Unreal's Lumen
  is a later step.
- [Height maps](rendering.md#height-and-detail-maps) sink mortar and grooves with parallax,
  but the grooves cast no shadow of their own, so a low sun across a brick wall doesn't
  darken the side of each joint away from it.
- [Auto exposure](rendering.md#auto-exposure) meters the whole frame, the sky included,
  with every pixel counted the same, so there is no centre weighting yet. It has no lower
  or upper limit on where it settles, so a scene meant to be dark is pushed up to middle
  grey, and it brightens and darkens at the same speed, where an eye opens up more slowly
  than it closes down.
- [AgX](rendering.md#tone-mapping) is a close polynomial fit of Blender 4's default look
  in Rec.709, not its exact curve. Of Blender's looks it has Punchy and the base curve,
  but none of the others, such as Golden. There is no output for HDR screens.
- [Render scale](rendering.md#graphics-settings) changes how wide glow and subsurface
  scattering look. Bloom shrinks the picture a fixed number of times, and subsurface light
  spreads a set number of scene pixels, so at 2 the glow and the soft light under skin are
  narrower than at 1, and at 0.5 wider.
- [Upscaling](rendering.md#upscaling) is AMD FSR 3.1 or the engine's TAA. NVIDIA's DLSS
  and Intel's XeSS are not supported, and FSR isn't used above a render scale of 1 or for
  frame generation. It needs `amd_fidelityfx_vk.dll`, so it is Windows only, like the rest.
- **Large numbers of objects.** Rendition's efficiency with many thousands of objects
  still has to be measured and improved.
- **More from physics.** [`Super.Physics`](physics.md) has bodies that fall and collide,
  characters, trigger areas, rays, shape casts and overlap tests, on Jolt. There are no
  mesh or height-field colliders, joints, ragdolls, vehicles or contact events between
  solid bodies yet.
- **Typing in interfaces.** The interface takes the mouse, but there are no text inputs,
  and it can't be used with a keyboard or gamepad yet.
- **Saved games and levels.** `saves.Write` and `saves.Read` are the plan; see
  [JSON content](content.md#planned).
- **Run logs.** Recording game events and checking them in tests is planned; see
  [Testing](testing.md#planned).
- **`--headless` and `--json`.** A command-line run with no GPU that prints a result.
  Tests already run headless.
- **Asset pipeline, step 2: hot reload of every asset kind.** Step 1 is done: everything
  is cooked into a manifest and objects, and a game run from source cooks again on every
  save. Saving a JSON file reloads its catalogs, but a model, texture or sound that is
  already loaded keeps what it has until the game restarts. See
  [Cooking](content.md#cooking) and [Change it while the game runs](content.md#change-it-while-the-game-runs).
- Step 1 of [material graphs](materials.md) is done: graphs cook into textures, with the
  Courtyard lineup of six starter graphs. `graph` in model sidecars is done too, and the
  Courtyard is now a scene modelled in Blender that wears nineteen of the example's graphs,
  with cards cut out by a graph's opacity, decals laid with graphs, and a puddle of
  [standing water](rendering.md#puddles) at a level the game sets. Next: hot reload of a
  saved graph in the running game, `repeat` in model
  sidecars, mesh bakes with a `Triplanar` node, and curvature and
  occlusion bakes as graph inputs. Curvature, occlusion and height are baked already, as
  [masks for wear and dirt](rendering.md#wear-and-dirt) in a second UV set, but a graph
  can't read them. Graphs run only at cook time, and there is no node editor
  and no way to share helpers between graph files.
- The Courtyard's Blender export isn't repeatable. Running `art/courtyard.py` twice gives a
  `courtyard.glb` whose dry-stone wall differs in about a hundred of its 300,000 triangles,
  and whose pebbles, plinth, splash stone and some mask UVs come out in another order, so a
  re-export shows them as changed when nothing about them did. Smart UV Project and loops
  over Python sets are the suspects.
- The cook's [step cache](command-line.md#the-cache) has gaps. Mip levels aren't kept, so a model
  that cooks again builds every texture's mips again, about 10 s for the Courtyard. A
  stand-alone `.material.cs` doesn't use it either: only graphs that a model's sidecar names
  are kept. And the cook's own BC7 encoder has no mode 4 yet, so a baked mask's alpha channel
  is up to 3.5 dB PSNR worse than BCnEncoder.Net gave it.
- A model sidecar can name a [surface shader](rendering.md#a-surface-shader-on-a-model)
  with its parameters and textures. The cook only checks that each shader texture's source
  is there, so a texture under a `"cook": false` rule, or a file that doesn't cook to a
  texture, fails when the model loads, and deleting the texture doesn't cook the model again.
  A shader named both by a model rule and by `content.Shader` is made into two pipelines.
- **Mods.** A mod should be a folder of JSON that adds or replaces entries by id. Several
  content roots can be cooked together, but a name that two of them make is a cook error,
  so a mod can't replace anything yet.
- **Unloading game states.** Entities can be [scoped to a state](ecs.md#entities-that-belong-to-a-state),
  but models and other content stay loaded unless you call `content.Unload`.
- **NuGet packages.** Clone the repository and reference the projects for now.
- **Other platforms.** Windows only for now, with Linux kept possible.

An editor is not planned. Super is a library, and your code and JSON files are the source
of truth.
